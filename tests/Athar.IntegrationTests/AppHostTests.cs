using System.Text.Json;
using Athar.Data;
using Athar.Data.Seed;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

[Collection(AtharCollection.Name)]
public class AppHostTests(AtharApp athar)
{
    [Fact]
    public async Task G1a_apphost_starts_every_resource()
    {
        // The fixture already waited for every resource to be healthy.
        using var http = athar.Web();
        using var response = await http.GetAsync("/", AtharApp.Timeout());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AT_55_db_reset_reproduces_the_qa_seed()
    {
        var ct = AtharApp.Timeout();
        // Its own database on the same server: dropping the shared one would kill web's pooled connections under the
        // next test, and other tests' writes would make the snapshot drift.
        await using var db = athar.Db(QaSeed.Org, database: "athar_at55");
        try
        {
            // A separate unit of work creates it, so db tracks nothing before the snapshot.
            await using (var create = athar.Db(QaSeed.Org, database: "athar_at55")) await create.ResetAsync(ct);
            var seeded = await SnapshotAsync(db, ct);
            Assert.Contains(QaSeed.EngConfidential.ToString(), seeded);

            await db.Database.MigrateAsync(ct); // seeds again: a no-op
            Assert.Equal(seeded, await SnapshotAsync(db, ct));

            // drop, migrate, seed, while another client (as web would) holds a connection
            await using (var held = new Npgsql.NpgsqlConnection(athar.ConnectionString("athar_at55")))
            {
                await held.OpenAsync(ct);
                await db.ResetAsync(ct);
            }
            Assert.Equal(seeded, await SnapshotAsync(db, ct));

            // The org query filter: a unit of work only sees its own org.
            db.CurrentOrgId = QaSeed.OtherOrg;
            Assert.Equal([QaSeed.OtherOrgOwner], await db.Users.Select(u => u.Id).ToListAsync(ct));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(AtharApp.Timeout());
        }
    }

    [Fact]
    public async Task AT_55_db_reset_reproduces_the_minimal_seed()
    {
        // T093: the squashed Init migration builds every profile; minimal (a production first start) resets the same way.
        // ponytail: perf is checked by hand (10k pages); add it here if CI time allows.
        var ct = AtharApp.Timeout();
        AtharDbContext Fresh() => athar.Db(Guid.Empty, database: "athar_at55_minimal", seed: "minimal");
        await using var db = Fresh();
        try
        {
            // Each reset in its own unit of work, like the CLI (one per process).
            await using (var first = Fresh()) await first.ResetAsync(ct);
            var seeded = await SnapshotAsync(db, ct);
            Assert.Contains(QaSeed.Org.ToString(), seeded);
            await using (var second = Fresh()) await second.ResetAsync(ct);
            Assert.Equal(seeded, await SnapshotAsync(db, ct));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(AtharApp.Timeout());
        }
    }

    [Fact]
    public async Task Seeded_vectors_carry_the_active_embedding_model_id()
    {
        // Under the model gateway search reads only that model's vectors; a seed stamped "bge-m3" would be invisible and
        // re-embedded wholesale (T100). The qa canary and perf vectors take the configured id.
        var ct = AtharApp.Timeout();
        const string gateway = "Qwen3-Embedding-4B@1024";
        AtharDbContext Fresh() => athar.Db(QaSeed.Org, database: "athar_seed_model", embeddingModelId: gateway);
        await using var db = Fresh();
        try
        {
            await using (var reset = Fresh()) await reset.ResetAsync(ct);
            var ids = await db.ChunkEmbeddings.IgnoreQueryFilters().Select(e => e.ModelId).Distinct().ToListAsync(ct);
            Assert.Equal([gateway], ids);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(AtharApp.Timeout());
        }
    }

    private static async Task<string> SnapshotAsync(AtharDbContext db, CancellationToken ct) => JsonSerializer.Serialize(new
    {
        orgs = await db.Organizations.AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct),
        users = await db.Users.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct),
        orgMembers = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct),
        workspaces = await db.Workspaces.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct),
        workspaceMembers = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct),
        spaces = await db.Spaces.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct),
        spaceMembers = await db.SpaceMembers.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct),
        chunks = await db.Chunks.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.Id).Select(e => new { e.Id, e.OrgId, e.Body }).ToListAsync(ct),
    });
}
