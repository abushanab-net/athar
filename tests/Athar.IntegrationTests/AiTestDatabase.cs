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
public sealed class AiTestDatabase(AtharApp app) : IDbContextFactory<AtharDbContext>, IAsyncDisposable
{
    public string Name { get; } = "athar_ai_" + Guid.NewGuid().ToString("N");
    public IConfiguration Configuration { get; set; } = new ConfigurationBuilder().Build();
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public AiSettingsResolver Resolver => new(this, Configuration, Protection);
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
    public AtharDbContext CreateDbContext() => app.Db(QaSeed.Org, Name);
    public Task<AtharDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync(ct);
    }
    public async ValueTask DisposeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
    }
}
