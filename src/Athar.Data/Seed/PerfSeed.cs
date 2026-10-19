using Athar.Domain.Memory;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.Data.Seed;

/// <summary>
/// The <c>perf</c> profile (docs/04 §4.3, NFR-01): <c>qa</c> plus 10,000 pages (about 60k chunks) and 5,000 memories, with
/// fixed IDs and deterministic text, saved in batches. Vectors are stored directly (deterministic pseudo-random unit
/// vectors): embedding 60k chunks with bge-m3 on CPU takes hours (D8), and a load check needs the vector index's cost,
/// not its meaning. Run it with <c>dotnet run --project src/Athar.AppHost -- --Athar:Seed=perf</c>.
/// </summary>
public static class PerfSeed
{
    public const int Roots = 100, ChildrenPerRoot = 99, Memories = 5_000, SectionsPerPage = 6;

    // A separate ID range from qa ("…-8000-…"), so the two never collide.
    private static Guid Id(int range, int n) => new($"00000000-0000-7000-8001-{range:x4}{n:x8}");
    public static readonly Guid Space = Id(1, 0);
    public static Guid Page(int root, int child) => Id(2, root * 1000 + child);
    public static Guid MemoryId(int i) => Id(3, i);

    private static readonly string[] English =
    [
        "deploy", "rollback", "pipeline", "release", "incident", "runbook", "database", "migration", "cache", "queue", "latency",
        "throughput", "postgres", "index", "backup", "restore", "token", "access", "review", "approval", "staging", "production",
        "monitoring", "alert", "escalation", "on-call", "schema", "service", "endpoint", "contract", "retry", "timeout", "lease",
        "worker", "search", "embedding", "chunk", "permission", "audit", "policy", "convention", "decision", "project", "team",
    ];
    private static readonly string[] Arabic =
    [
        "النشر", "التراجع", "الإصدار", "الحادثة", "قاعدة", "البيانات", "النسخ", "الاحتياطي", "الاستعادة", "المراجعة", "الموافقة",
        "المراقبة", "التنبيه", "التصعيد", "المناوبة", "الخدمة", "الصلاحية", "التدقيق", "السياسة", "القرار", "المشروع", "الفريق",
    ];

    public static async Task AddAsync(AtharDbContext db, string modelId, CancellationToken ct)
    {
        db.CurrentOrgId = QaSeed.Org;
        if (await db.Pages.AnyAsync(p => p.Id == Page(0, 0), ct)) return; // already there (AT-55: idempotent)

        db.Spaces.Add(Stamp(new Space { Id = Space, OrgId = QaSeed.Org, WorkspaceId = QaSeed.Engineering, Name = "Perf wiki", Slug = "perf", AiPolicy = new(Read: true, Write: true) }));
        db.SpaceMembers.Add(Stamp(new SpaceMember { Id = Id(1, 1), OrgId = QaSeed.Org, SpaceId = Space, UserId = QaSeed.Member, Role = SpaceRole.Editor }));
        await SaveAsync(db, ct);

        var chunkNo = 0;
        for (var root = 0; root < Roots; root++)
        {
            var rng = new Random(root);
            for (var child = 0; child <= ChildrenPerRoot; child++)
            {
                var id = Page(root, child);
                var title = child == 0 ? $"Area {root}: {Words(rng, 3)}" : $"{Words(rng, 4)} ({root}.{child})";
                var md = string.Join("\n\n", Enumerable.Range(1, SectionsPerPage).Select(s => $"## {Words(rng, 3)}\n\n{Words(rng, 60)}"));
                db.Pages.Add(Stamp(new Page
                {
                    Id = id, OrgId = QaSeed.Org, SpaceId = Space, ParentId = child == 0 ? null : Page(root, 0), Position = child,
                    Title = title, ContentMd = md, Revision = 1,
                }));
                foreach (var c in Chunker.Build(ChunkOwner.Page, id, 1, title, md, c => (c.SpaceId, c.IsConfidential) = (Space, false)))
                    AddChunk(db, c, modelId, ref chunkNo);
            }
            await SaveAsync(db, ct); // one root and its children per batch: ancestry resolves inside the batch
        }

        Guid[] teams = [QaSeed.PlatformTeam];
        for (var i = 0; i < Memories; i++)
        {
            var rng = new Random(1_000_000 + i);
            var (scope, owner) = (i % 10) switch
            {
                < 4 => (Scope.Company, (Guid?)null),
                < 7 => (Scope.Project, QaSeed.PilotProject),
                < 9 => (Scope.Team, teams[0]),
                _ => (Scope.Personal, QaSeed.Member),
            };
            var type = (MemoryType)(i % 8);
            var (title, body) = (Words(rng, 6), Words(rng, 40));
            var m = Stamp(new Memory
            {
                Id = MemoryId(i), OrgId = QaSeed.Org, Scope = scope, Type = type, Title = title, BodyMd = body, Status = MemoryStatus.Active,
                OwnerUserId = scope == Scope.Personal ? owner : null, OwnerTeamId = scope == Scope.Team ? owner : null,
                OwnerProjectId = scope == Scope.Project ? owner : null, Overridable = MemoryRules.DefaultOverridable(type), Origin = "perf",
                CreatedByUser = QaSeed.KnowledgeOwner,
            });
            db.Memories.Add(m);
            db.MemoryVersions.Add(Stamp(new MemoryVersion { Id = Id(4, i), OrgId = QaSeed.Org, MemoryId = m.Id, Revision = 1, Title = title, BodyMd = body }));
            foreach (var c in Chunker.Build(ChunkOwner.Memory, m.Id, 1, title, body, c =>
                         (c.Scope, c.OwnerUserId, c.OwnerTeamId, c.OwnerProjectId) = (m.Scope, m.OwnerUserId, m.OwnerTeamId, m.OwnerProjectId)))
                AddChunk(db, c, modelId, ref chunkNo);
            if (i % 500 == 499) await SaveAsync(db, ct);
        }
        await SaveAsync(db, ct);
    }

    private static void AddChunk(AtharDbContext db, Chunk c, string modelId, ref int n)
    {
        (c.Id, c.OrgId) = (Id(5, n), QaSeed.Org);
        db.Chunks.Add(Stamp(c));
        db.ChunkEmbeddings.Add(Stamp(new ChunkEmbedding { Id = Id(6, n), OrgId = QaSeed.Org, ChunkId = c.Id, ModelId = modelId, Embedding = Vector(n) }));
        n++;
    }

    /// <summary>A deterministic pseudo-random unit vector (1024 dimensions, like bge-m3).</summary>
    public static float[] Vector(int seed)
    {
        var rng = new Random(seed);
        var v = new float[1024];
        for (var i = 0; i < v.Length; i++) v[i] = (float)(rng.NextDouble() * 2 - 1);
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    private static string Words(Random rng, int count) =>
        string.Join(' ', Enumerable.Range(0, count).Select(_ => rng.Next(5) == 0 ? Arabic[rng.Next(Arabic.Length)] : English[rng.Next(English.Length)]));

    private static T Stamp<T>(T e) where T : OrgEntity { (e.CreatedAt, e.UpdatedAt) = (Seeder.At, Seeder.At); return e; }

    private static async Task SaveAsync(AtharDbContext db, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}
