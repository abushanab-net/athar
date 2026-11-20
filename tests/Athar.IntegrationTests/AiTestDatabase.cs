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
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

/// <summary>Isolated real Postgres database seeded through the existing QA profile, never shared worker state.</summary>
public class AiTestDatabase(AtharApp app) : IDbContextFactory<AtharDbContext>, IAsyncDisposable
{
    public string Name { get; } = "athar_ai_" + Guid.NewGuid().ToString("N");
    private readonly IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
    public virtual IConfiguration Configuration
    {
        get => configuration;
        set
        {
            var incoming = value.AsEnumerable().ToDictionary(p => p.Key, p => p.Value);
            foreach (var key in configuration.AsEnumerable().Select(p => p.Key).ToArray()) configuration[key] = null;
            foreach (var pair in incoming) configuration[pair.Key] = pair.Value;
        }
    }
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public virtual AiSettingsResolver Resolver => new(this, Configuration, Protection
        );
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
    }
    public virtual async ValueTask DisposeAsync()
    {
        await using var db = CreateDbContext(); await db.Database.EnsureDeletedAsync();
    }
}
