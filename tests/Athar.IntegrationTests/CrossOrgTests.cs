using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace Athar.IntegrationTests;

/// <summary>AT-07 (FR-AC-03, NFR-09): org A can't reference or read org B rows.</summary>
[Collection(AtharCollection.Name)]
public class CrossOrgTests(AtharApp athar)
{
    [Fact]
    public async Task AT_07_composite_fk_rejects_a_reference_to_another_org()
    {
        await AssertForeignKeyViolation(new Space
        {
            Id = Guid.CreateVersion7(), OrgId = QaSeed.Org, WorkspaceId = QaSeed.OtherOrgWorkspace, Name = "Leak", Slug = "leak",
        });
        await AssertForeignKeyViolation(new ChunkEmbedding
        {
            Id = Guid.CreateVersion7(), OrgId = QaSeed.Org, ChunkId = QaSeed.OtherOrgCanaryChunk, ModelId = "bge-m3", Embedding = QaSeed.CanaryVector(),
        });
    }

    [Fact]
    public async Task AT_07_raw_fts_and_vector_queries_never_return_other_org_rows()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(QaSeed.Org);
        var vector = new Vector(QaSeed.CanaryVector());
        IQueryable<Chunk> Fts() => db.Chunks.FromSql($"SELECT * FROM chunks WHERE tsv_simple @@ plainto_tsquery('simple', {QaSeed.CanaryText})");
        // Only the canary's own vector: the worker embeds other chunks (memories) into the same table (T051).
        IQueryable<ChunkEmbedding> Nearest() => db.ChunkEmbeddings.FromSql($"SELECT * FROM chunk_embeddings WHERE embedding <=> {vector} < 0.0001 ORDER BY embedding <=> {vector}");

        // The canary is in both orgs, so without the org predicate both rows come back.
        Assert.Equal(2, await Fts().IgnoreQueryFilters().CountAsync(ct));
        Assert.Equal(2, await Nearest().IgnoreQueryFilters().CountAsync(ct));

        Assert.Equal([QaSeed.CanaryChunk], await Fts().Select(c => c.Id).ToListAsync(ct));
        Assert.Equal([QaSeed.CanaryEmbedding], await Nearest().Select(e => e.Id).ToListAsync(ct));

        db.CurrentOrgId = QaSeed.OtherOrg;
        Assert.Equal([QaSeed.OtherOrgCanaryChunk], await Fts().Select(c => c.Id).ToListAsync(ct));
    }

    private async Task AssertForeignKeyViolation(OrgEntity row)
    {
        await using var db = athar.Db(QaSeed.Org);
        db.Add(row);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(AtharApp.Timeout()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }
}
