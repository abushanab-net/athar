using Athar.Data;
using Athar.Worker;
using Athar.Worker.Jobs;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = Host.CreateApplicationBuilder(args);
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
builder.Services.AddSingleton<IEmbedder>(sp => ai.Gateway ? new GatewayEmbedder(ai) : sp.GetRequiredService<OllamaEmbedder>());
builder.Services.AddSingleton<ICandidateExtractor>(sp => ai.Gateway ? new GatewayExtractor(ai, sp.GetRequiredService<ILogger<GatewayExtractor>>()) : sp.GetRequiredService<OllamaExtractor>());
builder.Services.AddHostedService(sp => new ReembedSweep(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<AiSettingsResolver>(),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ReembedSweep>>()));
builder.Services.AddSingleton<IJob, EmbedPageJob>();
builder.Services.AddSingleton<IJob>(sp => new EmbedVectorsJob(sp.GetRequiredService<AiOperationClients>()));
builder.Services.AddSingleton<IJob>(sp => new ExtractSessionJob(sp.GetRequiredService<AiOperationClients>()));
builder.Services.AddSingleton<Athar.Data.AttachmentStore>().AddSingleton<IJob, Athar.Worker.Jobs.Upload.ExtractAttachmentJob>(); // Lane D (T046)
// Lane D (T070): GitHub sync; the PAT is decrypted with the key ring web encrypts with.
Athar.Data.AtharKeys.AddAtharKeys(builder.Services, builder.Configuration);
builder.Services.AddHttpClient(Athar.Worker.Jobs.GitHubSync.GitHubClient.HttpClientName);
builder.Services.AddSingleton<Athar.Worker.Jobs.GitHubSync.GitHubClient>().AddSingleton<IJob, Athar.Worker.Jobs.GitHubSync.GitHubSyncJob>();
builder.Services.AddSingleton<IJob, Athar.Worker.Jobs.Ingest.IngestSourceJob>(); // Lane D (T142): URL and document sources
builder.Services.AddHostedService<JobRunner>();

var host = builder.Build();
// Start pulling bge-m3 at startup when Ollama is the provider, so the first embed doesn't wait for it.
if (!ai.Gateway && builder.Configuration.GetConnectionString("ollama") is not null)
    _ = host.Services.GetRequiredService<OllamaEmbedder>().EnsureReadyAsync(CancellationToken.None);
host.Run();
