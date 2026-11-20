using Athar.Data;
using Athar.Worker;
using Athar.Worker.Jobs;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Athar.Worker;

/// <summary>What a host that builds on Athar adds to the worker. Athar's own worker passes none.</summary>
public sealed class AtharWorkerEdition
{
    /// <summary>Registers the edition's jobs and services, after Athar's own.</summary>
    public Action<HostApplicationBuilder>? ConfigureServices { get; init; }

    /// <summary>Runs once the services are built and before any job is claimed (for example a schema check).</summary>
    public Func<IServiceProvider, Task>? BeforeRunAsync { get; init; }

    /// <summary>
    /// True when the edition routes every model call itself. Athar then registers no default embedder or extractor and no
    /// default vector job, and does not pre-pull a local model: the edition supplies its own.
    /// </summary>
    public bool RoutesModelCalls { get; init; }
}

/// <summary>Athar's background worker: <c>Program.cs</c> calls this, and so does a host that builds on Athar.</summary>
public static class AtharWorkerHost
{
    public static async Task RunAsync(string[] args, AtharWorkerEdition? edition = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var routed = edition?.RoutesModelCalls ?? false;
        builder.AddServiceDefaults();
        // The worker never migrates or seeds; web does that first (the AppHost starts the worker after web).
        builder.Services.AddAtharData(
            builder.Configuration.GetConnectionString(AtharData.ConnectionName) ?? throw new InvalidOperationException("ConnectionStrings:athar is not set."),
            new SeedOptions("minimal"));
        builder.Services.AddHostedService<RetentionService>();
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Job runners register here as their lanes add them (docs/03 §7).
        // AI: the model gateway when Athar:Ai is configured (Phase P: the on-prem model gateway), else the local Ollama models (docs/03 D8).
        var ai = builder.Configuration.GetSection("Athar:Ai").Get<Athar.Domain.Memory.AiOptions>() ?? new();
        builder.Services.AddSingleton<AiSettingsResolver>().AddSingleton<AiOperationClients>();
        builder.Services.AddSingleton<OllamaEmbedder>().AddSingleton<OllamaExtractor>();
        if (!routed)
        {
            builder.Services.AddSingleton<IEmbedder>(sp => ai.Gateway ? new GatewayEmbedder(ai) : sp.GetRequiredService<OllamaEmbedder>());
            builder.Services.AddSingleton<ICandidateExtractor>(sp => ai.Gateway ? new GatewayExtractor(ai, sp.GetRequiredService<ILogger<GatewayExtractor>>()) : sp.GetRequiredService<OllamaExtractor>());
        }
        builder.Services.AddHostedService(sp => new ReembedSweep(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<AiSettingsResolver>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ReembedSweep>>()));
        builder.Services.AddSingleton<IJob, EmbedPageJob>();
        if (!routed) builder.Services.AddSingleton<IJob>(sp => new EmbedVectorsJob(sp.GetRequiredService<AiOperationClients>()));
        builder.Services.AddSingleton<IJob>(sp => new ExtractSessionJob(sp.GetRequiredService<AiOperationClients>()));
        builder.Services.AddSingleton<Athar.Data.AttachmentStore>().AddSingleton<IJob, Athar.Worker.Jobs.Upload.ExtractAttachmentJob>(); // Lane D (T046)
        // Lane D (T070): GitHub sync; the PAT is decrypted with the key ring web encrypts with.
        Athar.Data.AtharKeys.AddAtharKeys(builder.Services, builder.Configuration);
        builder.Services.AddHttpClient(Athar.Worker.Jobs.GitHubSync.GitHubClient.HttpClientName);
        builder.Services.AddSingleton<Athar.Worker.Jobs.GitHubSync.GitHubClient>().AddSingleton<IJob, Athar.Worker.Jobs.GitHubSync.GitHubSyncJob>();
        builder.Services.AddSingleton<IJob, Athar.Worker.Jobs.Ingest.IngestSourceJob>(); // Lane D (T142): URL and document sources
        builder.Services.AddHostedService<JobRunner>();

        // A host that builds on Athar adds its own jobs and services here.
        edition?.ConfigureServices?.Invoke(builder);

        var host = builder.Build();
        if (edition?.BeforeRunAsync is { } before) await before(host.Services);
        // Start pulling bge-m3 at startup when Ollama is the provider, so the first embed doesn't wait for it.
        if (!routed && !ai.Gateway && builder.Configuration.GetConnectionString("ollama") is not null)
            _ = host.Services.GetRequiredService<OllamaEmbedder>().EnsureReadyAsync(CancellationToken.None);
        await host.RunAsync();
    }
}
