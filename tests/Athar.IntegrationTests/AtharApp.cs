using System.Collections.Concurrent;
using System.Text;
using Aspire.Hosting;
using Athar.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Athar.IntegrationTests;

/// <summary>Every test class joins this collection, so the AppHost starts once per run and tests run one at a time.</summary>
[CollectionDefinition(Name)]
public class AtharCollection : ICollectionFixture<AtharApp>
{
    public const string Name = "athar";
}

/// <summary>
/// The whole AppHost (postgres, ollama, mailpit, collab, web, worker) on real Postgres. Web runs in Development,
/// so it migrates and seeds the <c>qa</c> profile; tests refer to rows by <see cref="Data.Seed.QaSeed"/> IDs.
/// </summary>
public sealed class AtharApp : IAsyncLifetime
{
    // A cold run pulls the postgres, ollama and mailpit images.
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    private readonly LogSink _log = new();
    private string _connectionString = null!;
    public DistributedApplication App { get; private set; } = null!;

    /// <summary>The GitHub the worker syncs from (T070); repos under <see cref="GitHubOrg"/> are on the allow-list.</summary>
    public FakeGitHub GitHub { get; private set; } = null!;
    public const string GitHubOrg = "qa-org";

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(StartTimeout);
        var ct = cts.Token;
        // Tests use the local Ollama models (as CI does) unless ATHAR_TEST_AI=gateway opts into the model gateway configured
        // in the AppHost's user-secrets (T090; T091 runs AT-40 that way). Blanking the key turns the gateway off.
        // Edited pages re-index at once in tests (the hosts throttle it, T100).
        string[] args = Environment.GetEnvironmentVariable("ATHAR_TEST_AI") == "gateway" ? ["--Athar:Index:PageEvery=00:00:00"] : ["--Parameters:ai-key=", "--Athar:Index:PageEvery=00:00:00"];
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Athar_AppHost>(args, ct);
        appHost.Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(_log));
        GitHub = await FakeGitHub.StartAsync();
        foreach (var name in new[] { "web", "worker" })
            appHost.CreateResourceBuilder<ProjectResource>(name)
                .WithEnvironment("Athar__GitHub__ApiUrl", GitHub.Url)
                .WithEnvironment("Athar__GitHub__AllowedRepos__0", $"{GitHubOrg}/*")
                .WithEnvironment("Athar__Sources__TrustedHosts__0", "127.0.0.1") // T142: the test site; real hosts only reach public addresses
                .WithEnvironment("Athar__Extraction__AutoPull", "false"); // tests use a fixed extractor; 4.7 GB of qwen would sink the CI runner
        App = await appHost.BuildAsync(ct);
        try { await App.StartAsync(ct); }
        catch (Exception ex) when (ex is OperationCanceledException or DistributedApplicationException)
        {
            throw new InvalidOperationException(Describe("application startup"), ex);
        }

        foreach (var name in new[] { "postgres", "athar", "ollama", "mailpit", "collab", "web" })
        {
            try { await App.ResourceNotifications.WaitForResourceHealthyAsync(name, ct); }
            catch (Exception ex) when (ex is OperationCanceledException or DistributedApplicationException)
            {
                throw new InvalidOperationException(Describe(name), ex);
            }
        }
        await App.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running, ct);
        _connectionString = (await App.GetConnectionStringAsync(AtharData.ConnectionName, ct))!;
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        if (GitHub is not null) await GitHub.DisposeAsync();
    }

    /// <summary>A unit of work in one org, seeded with <c>qa</c> like web (or <paramref name="seed"/>). <paramref name="database"/>: another database on the same server.</summary>
    public AtharDbContext Db(Guid orgId, string? database = null, string seed = "qa", string? embeddingModelId = null)
    {
        var options = new DbContextOptionsBuilder<AtharDbContext>();
        options.UseAthar(ConnectionString(database), embeddingModelId is null ? new SeedOptions(seed) : new SeedOptions(seed, embeddingModelId));
        return new AtharDbContext(options.Options) { CurrentOrgId = orgId };
    }

    /// <summary>The database's connection string (with its password); <paramref name="database"/> picks another one on the same server.</summary>
    public string ConnectionString(string? database = null) =>
        database is null ? _connectionString : new Npgsql.NpgsqlConnectionStringBuilder(_connectionString) { Database = database }.ConnectionString;

    public HttpClient Web() => App.CreateHttpClient("web", "https");

    public static CancellationToken Timeout() => new CancellationTokenSource(TestTimeout).Token;

    // Every resource's state and health, plus the tail of the AppHost log, so a CI failure says why.
    private string Describe(string name)
    {
        var sb = new StringBuilder($"'{name}' did not become healthy.\n");
        foreach (var r in App.Services.GetRequiredService<DistributedApplicationModel>().Resources)
            if (App.ResourceNotifications.TryGetCurrentState(r.Name, out var e))
                sb.AppendLine($"{r.Name}: {e.Snapshot.State?.Text} {string.Join(", ", e.Snapshot.HealthReports.Select(h => $"{h.Name}={h.Status} {h.ExceptionText}"))}");
        sb.AppendLine("--- log tail ---");
        foreach (var line in _log.Lines.TakeLast(400)) sb.AppendLine(line);
        return sb.ToString();
    }

    private sealed class LogSink : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public ILogger CreateLogger(string category) => new Logger(this, category);
        public void Dispose() { }

        private sealed class Logger(LogSink sink, string category) : ILogger
        {
            public IDisposable? BeginScope<T>(T state) where T : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<T>(LogLevel level, EventId id, T state, Exception? ex, Func<T, Exception?, string> format) =>
                sink.Lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {level} {category}: {format(state, ex)} {ex}");
        }
    }
}
