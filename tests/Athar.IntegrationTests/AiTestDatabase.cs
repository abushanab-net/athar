using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Athar.Worker.Jobs;
using Athar.Worker.Jobs.Extract;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

/// <summary>Isolated real Postgres database seeded through the existing QA profile, never shared worker state.</summary>
public class AiTestDatabase(AtharApp app) : IDbContextFactory<AtharDbContext>, IAsyncDisposable
{
    public string Name { get; } = "athar_ai_" + Guid.NewGuid().ToString("N");
    private readonly IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
    /// <summary>An edition's own services for these tests (they may supply an <see cref="IAiFunctionRouter"/>). Set once, before any test runs.</summary>
    public static Action<IServiceCollection>? EditionServices { get; set; }
    /// <summary>An edition's own schema and seed, applied after Athar's on the same database (the connection string is the test's).</summary>
    public static Func<AtharDbContext, string, CancellationToken, Task>? EditionSchema { get; set; }
    private readonly List<ServiceProvider> runtimes = [];
    private ServiceProvider? runtime;
    public virtual IConfiguration Configuration
    {
        get => configuration;
        set
        {
            var incoming = value.AsEnumerable().ToDictionary(p => p.Key, p => p.Value);
            runtime = null;
            foreach (var key in configuration.AsEnumerable().Select(p => p.Key).ToArray()) configuration[key] = null;
            foreach (var pair in incoming) configuration[pair.Key] = pair.Value;
        }
    }
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public virtual AiSettingsResolver Resolver => new(this, Configuration, Protection, router: Router);
    /// <summary>The edition's router, or null when Athar runs on its own.</summary>
    public IAiFunctionRouter? Router => EditionServices is null ? null : (runtime ??= CreateRuntime(Protection)).GetService<IAiFunctionRouter>();
    public IAiFunctionRouter? RouterFor(IDataProtectionProvider actualProtection) =>
        EditionServices is null ? null : CreateRuntime(actualProtection).GetService<IAiFunctionRouter>();
    private ServiceProvider CreateRuntime(IDataProtectionProvider actualProtection)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(Configuration)
            .AddSingleton<IDataProtectionProvider>(actualProtection).AddSingleton<IDbContextFactory<AtharDbContext>>(this)
            .AddScoped(_ => CreateDbContext()).AddScoped<AiSettingsResolver>();
        EditionServices!(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        runtimes.Add(provider); return provider;
    }
    public AiSettingsService Settings => new(this, Resolver);
    public QueryEmbedder Embedder => new(Configuration, NullLogger<QueryEmbedder>.Instance, Resolver);
    public QueryAnswerService Answers => new(this, Resolver, Embedder);
    public AiOperationClients Clients => new(Resolver, new ServiceCollection().BuildServiceProvider(), NullLogger<GatewayExtractor>.Instance);
    public static Actor Person(Guid user, Guid? org = null) => new(user, org ?? QaSeed.Org, null,
        new HashSet<string> { "read", "write", "memory:write" }, DisclosureClass.TrustedLocal, null);
    public static Actor Admin => Person(QaSeed.Admin);
    public static Actor Owner => Person(QaSeed.Owner);
    public static AiSettingsWrite Draft(FakeAiGateway gateway, long revision = 0, ApiKeyChange key = ApiKeyChange.Clear, string? secret = null) =>
        new(revision, gateway.BaseUrl, gateway.EmbeddingModel, gateway.QueryModel, gateway.VisionModel, key, secret);
    public string? ApplicationName { get; set; }
    private string OwnedConnection => new Npgsql.NpgsqlConnectionStringBuilder(app.ConnectionString(Name)) { ApplicationName = ApplicationName ?? "" }.ConnectionString;
    public AtharDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AtharDbContext>(); options.UseAthar(OwnedConnection, new SeedOptions("qa"));
        return new(options.Options) { CurrentOrgId = QaSeed.Org };
    }
    public Task<AtharDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    public virtual async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync(ct);
        if (EditionSchema is { } schema) await schema(db, OwnedConnection, ct);
    }
    public virtual async ValueTask DisposeAsync()
    {
        var owners = runtimes.ToArray(); runtimes.Clear(); runtime = null;
        foreach (var owner in owners) await owner.DisposeAsync();
        await using var db = CreateDbContext(); await db.Database.EnsureDeletedAsync();
    }
}
