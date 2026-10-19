using Athar.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Athar.Data;

/// <param name="Profile"><c>minimal</c> or <c>qa</c> (docs/04 §4.3).</param>
/// <param name="EmbeddingModelId">The <c>chunk_embeddings.model_id</c> seeded vectors carry: the active one (<c>AiModels.EmbeddingId</c>), so
/// search and the re-embed sweep accept them under the gateway as under Ollama.</param>
public record SeedOptions(string Profile, string EmbeddingModelId = Athar.Domain.Memory.AiModels.OllamaEmbeddingId);

public static class AtharData
{
    public const string ConnectionName = "athar";

    /// <summary>A scoped <see cref="AtharDbContext"/> per request, plus <see cref="IDbContextFactory{TContext}"/> for interactive components, which need one per operation.</summary>
    public static IServiceCollection AddAtharData(this IServiceCollection services, string connectionString, SeedOptions seed) =>
        services.AddDbContextFactory<AtharDbContext>(o => o.UseAthar(connectionString, seed));

    /// <summary>Npgsql with pgvector. The seed profile runs inside <c>MigrateAsync</c>.</summary>
    public static DbContextOptionsBuilder UseAthar(this DbContextOptionsBuilder o, string connectionString, SeedOptions seed) =>
        o.UseNpgsql(connectionString, n => n.UseVector())
         .AddInterceptors(new AuditImmutability())
         .UseAsyncSeeding((ctx, _, ct) => Seeder.SeedAsync((AtharDbContext)ctx, seed, ct));

    /// <summary><c>db reset</c>: drop, migrate, seed. Dev and test databases only: production refuses it (OD-5).</summary>
    public static async Task ResetAsync(this AtharDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureDeletedAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}

/// <summary>For <c>dotnet ef migrations add</c>; it never connects.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AtharDbContext>
{
    public AtharDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AtharDbContext>().UseNpgsql("Host=localhost", n => n.UseVector()).Options);
}
