using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace Athar.Web.Services;

public sealed record SearchInput(string Query, string[]? Kinds = null, Guid[]? SpaceIds = null, Guid[]? ProjectIds = null, int Limit = 10);
public sealed record SearchResult(bool Degraded, IReadOnlyList<SearchHit> Hits);
// Nullable members default to null so the generated output schema marks them optional.
public sealed record SearchHit(string Kind, Guid Id, string Title, string Snippet, string Url, double Score, Guid? SpaceId = null, string? Scope = null)
{
    /// <summary>A source document's hit, linked per kind: GitHub at the synced commit (T070), a URL to the page itself, a document to its project (T142).</summary>
    public static SearchHit Source(SourceKind kind, string repo, string? title, string path, string? commit, Guid projectId, Guid id, string snippet, double score) => kind switch
    {
        SourceKind.Github => new("source", id, $"{repo}: {title ?? path}", snippet, $"https://github.com/{repo}/blob/{commit}/{path}", score, Scope: "project"),
        SourceKind.Url => new("source", id, title ?? repo, snippet, repo, score, Scope: "project"),
        _ => new("source", id, title ?? path, snippet, $"/projects/{projectId}", score, Scope: "project"),
    };
}

/// <summary>A chunk's rank in the fused list, with its owner.</summary>
public sealed record RankedChunk(Guid ChunkId, ChunkOwner OwnerKind, Guid OwnerId, double Score);

/// <summary>
/// Hybrid search (FR-SR-01..02, docs/03 §5): FTS (<c>english</c>, <c>arabic</c>, <c>simple</c>) and bge-m3 cosine, fused
/// with reciprocal rank fusion. Permissions are inside the query: the caller's visible pages and memories are materialized
/// from the EF access predicates first, and the raw FTS and vector SQL takes them, with <c>org_id</c>, as parameters.
/// Without a query embedding (Ollama down), results are FTS only and flagged <c>degraded</c>.
/// </summary>
public sealed class SearchService(AtharDbContext db, QueryEmbedder embedder)
{
    /// <summary>RRF's k (Cormack et al.): dampens the weight of top ranks so both lists count.</summary>
    public const int RrfK = 60;

    public Task<AiSnapshot> SnapshotAsync(Guid org, CancellationToken ct) => embedder.SnapshotAsync(org, ct);

    /// <summary>Below this many permitted rows an exact scan is cheaper and complete; above it, HNSW with iterative scans (docs/03 §4.3).</summary>
    public const int ExactBelow = 5000;

    public async Task<SearchResult> SearchAsync(Actor actor, SearchInput input, CancellationToken ct)
    {
        if (!actor.Scopes.Contains("read")) throw new AtharError("forbidden", "The token lacks the 'read' scope.");
        db.CurrentOrgId = actor.OrgId;
        var kinds = input.Kinds is { Length: > 0 } k ? k.Select(x => x.ToLowerInvariant()).ToHashSet() : ["page", "memory", "source"];
        if (kinds.Except(["page", "memory", "source"]).Any()) throw new AtharError("invalid_input", "kinds must be page, memory or source");
        var limit = Math.Clamp(input.Limit, 1, 50);

        Guid[] pages = kinds.Contains("page") ? await VisiblePagesAsync(actor, input.SpaceIds, ct) : [];
        Guid[] memories = kinds.Contains("memory") ? await VisibleMemoriesAsync(actor, input.ProjectIds, ct) : [];
        Guid[] sources = kinds.Contains("source") ? await VisibleSourceDocsAsync(actor, input.ProjectIds, ct) : []; // Lane D (T070)
        var (ranked, degraded) = await RankAsync(actor.OrgId, input.Query, pages, memories, limit * 4, ct, sources);

        // Collapse to owners (best chunk wins), then hydrate titles and snippets.
        var best = ranked.GroupBy(r => (r.OwnerKind, r.OwnerId)).Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score).Take(limit).ToList();
        var chunkIds = best.Select(b => b.ChunkId).ToArray();
        var bodies = await db.Chunks.Where(c => chunkIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Body, ct);
        var pageIds = best.Where(b => b.OwnerKind == ChunkOwner.Page).Select(b => b.OwnerId).ToArray();
        var memoryIds = best.Where(b => b.OwnerKind == ChunkOwner.Memory).Select(b => b.OwnerId).ToArray();
        var pageRows = await db.Pages.Where(p => pageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => new { p.Title, p.SpaceId }, ct);
        var memoryRows = await db.Memories.Where(m => memoryIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => new { m.Title, m.Scope }, ct);
        var docIds = best.Where(b => b.OwnerKind == ChunkOwner.SourceDocument).Select(b => b.OwnerId).ToArray();
        var docRows = await db.SourceDocuments.Where(d => docIds.Contains(d.Id)).Join(db.KnowledgeSources, d => d.SourceId, s => s.Id,
            (d, s) => new { d.Id, d.Title, d.Path, d.CommitSha, s.Repo, s.Kind, s.ProjectId }).ToDictionaryAsync(d => d.Id, ct);

        var hits = best.Select(b =>
        {
            var snippet = Snippet(bodies.GetValueOrDefault(b.ChunkId) ?? "");
            return b.OwnerKind switch
            {
                ChunkOwner.Page => new SearchHit("page", b.OwnerId, pageRows[b.OwnerId].Title, snippet, $"/wiki/p/{b.OwnerId}", b.Score, SpaceId: pageRows[b.OwnerId].SpaceId),
                ChunkOwner.SourceDocument => SearchHit.Source(docRows[b.OwnerId].Kind, docRows[b.OwnerId].Repo, docRows[b.OwnerId].Title, docRows[b.OwnerId].Path,
                    docRows[b.OwnerId].CommitSha, docRows[b.OwnerId].ProjectId, b.OwnerId, snippet, b.Score),
                _ => new SearchHit("memory", b.OwnerId, memoryRows[b.OwnerId].Title, snippet, $"/memories/{b.OwnerId}", b.Score,
                    Scope: memoryRows[b.OwnerId].Scope.ToString().ToLowerInvariant()),
            };
        }).ToList();
        return new(degraded, hits);
    }

    /// <summary>Pages the caller may read now (access, AI policy, disclosure), not trashed, optionally in some spaces.</summary>
    /// <remarks>U-1 (T119): never drafts, not even for their editors: search finds published knowledge (the tree shows drafts).</remarks>
    public async Task<Guid[]> VisiblePagesAsync(Actor actor, Guid[]? spaceIds, CancellationToken ct) =>
        await db.PagesFor(actor.Caller).Where(p => p.DeletedAt == null && p.Status != PageStatus.Draft && (spaceIds == null || spaceIds.Contains(p.SpaceId)))
            .Select(p => p.Id).ToArrayAsync(ct);

    /// <summary>
    /// T070 (FR-KS-01): live documents synced into the caller's projects (project members are the audience), optionally only
    /// some projects; a confidential source is withheld from external agents (FR-CL-01).
    /// </summary>
    public async Task<Guid[]> VisibleSourceDocsAsync(Actor actor, Guid[]? projectIds, CancellationToken ct)
    {
        var who = await db.MemoryActorAsync(actor, ct);
        Guid[] projects = [.. who.ProjectIds.Where(p => projectIds == null || projectIds.Contains(p))];
        return await db.SourceDocuments.Where(d => d.TombstonedAt == null && db.KnowledgeSources.Any(s => s.Id == d.SourceId
                && projects.Contains(s.ProjectId) && (!who.ExternalAgent || !s.IsConfidential)))
            .Select(d => d.Id).ToArrayAsync(ct);
    }

    /// <summary>Active memories the caller may read; with project ids, project memories only from those projects.</summary>
    public async Task<Guid[]> VisibleMemoriesAsync(Actor actor, Guid[]? projectIds, CancellationToken ct)
    {
        var who = await db.MemoryActorAsync(actor, ct);
        return await db.Memories.Where(MemoryRules.Visible(who))
            .Where(m => m.Status == MemoryStatus.Active && (projectIds == null || m.Scope != Scope.Project || projectIds.Contains(m.OwnerProjectId!.Value)))
            .Select(m => m.Id).ToArrayAsync(ct);
    }

    /// <summary>
    /// The fused chunk ranking over the given visible owners (shared with recall). <paramref name="k"/> candidates come from
    /// each list; collapse and top-up are the caller's.
    /// </summary>
    public async Task<(List<RankedChunk> Ranked, bool Degraded)> RankAsync(Guid org, string query, Guid[] pages, Guid[] memories, int k, CancellationToken ct, Guid[]? sources = null, AiSnapshot? snapshot = null)
    {
        sources ??= [];
        if (pages.Length + memories.Length + sources.Length == 0 || string.IsNullOrWhiteSpace(query)) return ([], false);
        var fts = await FtsAsync(org, query, pages, memories, k, ct, sources);
        snapshot ??= await SnapshotAsync(org, ct);
        var vector = await embedder.EmbedAsync(snapshot, query, ct);
        if (vector is null) return (Fuse(fts), true);
        var exact = pages.Length + memories.Length + sources.Length < ExactBelow;
        List<RankedChunk> nearest;
        try { nearest = await NearestAsync(org, new Vector(vector), pages, memories, k, exact, ct, sources, snapshot.EmbeddingIdentity); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.InternalError or PostgresErrorCodes.UndefinedObject)
        {
            // `db reset` recreates pgvector's type with a new OID (dev only); reload Npgsql's cached types and retry once.
            await db.Database.OpenConnectionAsync(ct);
            await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync(ct);
            NpgsqlConnection.ClearAllPools();
            nearest = await NearestAsync(org, new Vector(vector), pages, memories, k, exact, ct, sources, snapshot.EmbeddingIdentity);
        }
        return (Fuse(fts, nearest), nearest.Count == 0);
    }

    /// <summary>At most this many matching chunks per FTS branch are ranked (T092).</summary>
    public const int FtsCandidates = 500;

    /// <summary>
    /// FTS over the visible owners: any query term, ranked by ts_rank across the three configs (FR-SR-05 normalized).
    /// Only the best candidates are ranked: up to <see cref="FtsCandidates"/> chunks with every term, plus as many with any
    /// term, so a common word doesn't make every request rank every chunk (T092: 195 ms → 15 ms on the perf seed).
    /// </summary>
    public async Task<List<RankedChunk>> FtsAsync(Guid org, string query, Guid[] pages, Guid[] memories, int k, CancellationToken ct, Guid[]? sources = null)
    {
        sources ??= [];
        var terms = TextNormalizer.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var any = string.Join(" or ", terms);
        var all = string.Join(" ", terms);
        // ponytail: past the cap, which matches get ranked is the scan's order, not relevance; the vector list still ranks
        // everything. A BM25 index (pg_search, RUM) ranks inside the index if real corpora need it.
        var rows = await db.Database.SqlQuery<Row>($"""
            (SELECT c.id AS "ChunkId", c.owner_kind AS "OwnerKind", c.owner_id AS "OwnerId",
                    ts_rank(c.tsv_en, websearch_to_tsquery('english', {any})) + ts_rank(c.tsv_ar, websearch_to_tsquery('arabic', {any}))
                      + ts_rank(c.tsv_simple, websearch_to_tsquery('simple', {any})) AS "Score"
             FROM chunks c
             WHERE c.org_id = {org}
               AND ((c.owner_kind = 'page' AND c.owner_id = ANY({pages})) OR (c.owner_kind = 'memory' AND c.owner_id = ANY({memories})) OR (c.owner_kind = 'source_document' AND c.owner_id = ANY({sources})))
               AND (c.tsv_en @@ websearch_to_tsquery('english', {all}) OR c.tsv_ar @@ websearch_to_tsquery('arabic', {all})
                    OR c.tsv_simple @@ websearch_to_tsquery('simple', {all}))
             LIMIT {FtsCandidates})
            UNION
            (SELECT c.id, c.owner_kind, c.owner_id,
                    ts_rank(c.tsv_en, websearch_to_tsquery('english', {any})) + ts_rank(c.tsv_ar, websearch_to_tsquery('arabic', {any}))
                      + ts_rank(c.tsv_simple, websearch_to_tsquery('simple', {any}))
             FROM chunks c
             WHERE c.org_id = {org}
               AND ((c.owner_kind = 'page' AND c.owner_id = ANY({pages})) OR (c.owner_kind = 'memory' AND c.owner_id = ANY({memories})) OR (c.owner_kind = 'source_document' AND c.owner_id = ANY({sources})))
               AND (c.tsv_en @@ websearch_to_tsquery('english', {any}) OR c.tsv_ar @@ websearch_to_tsquery('arabic', {any})
                    OR c.tsv_simple @@ websearch_to_tsquery('simple', {any}))
             LIMIT {FtsCandidates})
            ORDER BY "Score" DESC, "ChunkId"
            LIMIT {k}
            """).ToListAsync(ct);
        return [.. rows.Select(r => r.Ranked)];
    }

    /// <summary>
    /// Nearest chunks by cosine distance over the visible owners. Exact: the visible candidates are materialized first, so
    /// the scan is complete. Otherwise HNSW with pgvector 0.8 iterative scans (<c>relaxed_order</c>), so a filtered search
    /// still fills <paramref name="k"/>; the relaxed order is re-sorted here.
    /// </summary>
    public async Task<List<RankedChunk>> NearestAsync(Guid org, Vector v, Guid[] pages, Guid[] memories, int k, bool exact, CancellationToken ct, Guid[]? sources = null, string? modelId = null)
    {
        sources ??= [];
        modelId ??= (await SnapshotAsync(org, ct)).EmbeddingIdentity;
        // SET LOCAL needs a transaction; join the caller's if there is one.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        List<Row> rows;
        if (exact)
        {
            rows = await db.Database.SqlQuery<Row>($"""
                WITH candidates AS MATERIALIZED (
                    SELECT c.id, c.owner_kind, c.owner_id, e.embedding
                    FROM chunk_embeddings e JOIN chunks c ON c.org_id = e.org_id AND c.id = e.chunk_id
                    WHERE e.org_id = {org} AND e.model_id = {modelId}
                      AND ((c.owner_kind = 'page' AND c.owner_id = ANY({pages})) OR (c.owner_kind = 'memory' AND c.owner_id = ANY({memories})) OR (c.owner_kind = 'source_document' AND c.owner_id = ANY({sources}))))
                SELECT id AS "ChunkId", owner_kind AS "OwnerKind", owner_id AS "OwnerId", 1 - (embedding <=> {v}) AS "Score"
                FROM candidates ORDER BY embedding <=> {v}, id LIMIT {k}
                """).ToListAsync(ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = relaxed_order; SET LOCAL hnsw.ef_search = 100", ct);
            rows = await db.Database.SqlQuery<Row>($"""
                SELECT c.id AS "ChunkId", c.owner_kind AS "OwnerKind", c.owner_id AS "OwnerId", 1 - (e.embedding <=> {v}) AS "Score"
                FROM chunk_embeddings e JOIN chunks c ON c.org_id = e.org_id AND c.id = e.chunk_id
                WHERE e.org_id = {org} AND e.model_id = {modelId}
                  AND ((c.owner_kind = 'page' AND c.owner_id = ANY({pages})) OR (c.owner_kind = 'memory' AND c.owner_id = ANY({memories})) OR (c.owner_kind = 'source_document' AND c.owner_id = ANY({sources})))
                ORDER BY e.embedding <=> {v}
                LIMIT {k}
                """).ToListAsync(ct);
            rows = [.. rows.OrderByDescending(r => r.Score).ThenBy(r => r.ChunkId)];
        }
        if (tx is not null) await tx.CommitAsync(ct);
        return [.. rows.Select(r => r.Ranked)];
    }

    /// <summary>Reciprocal rank fusion: each list contributes 1/(k + rank) per chunk.</summary>
    public static List<RankedChunk> Fuse(params List<RankedChunk>[] lists) =>
        [.. lists.SelectMany(list => list.Select((r, i) => (r, score: 1.0 / (RrfK + i + 1))))
            .GroupBy(x => x.r.ChunkId)
            .Select(g => g.First().r with { Score = g.Sum(x => x.score) })
            .OrderByDescending(r => r.Score).ThenBy(r => r.ChunkId)];

    private static string Snippet(string body) => body.Length <= 200 ? body : body[..200];

    // The `ToLowerInvariant` names match how enums are stored (snake_case text).
    private sealed record Row(Guid ChunkId, string OwnerKind, Guid OwnerId, double Score)
    {
        public RankedChunk Ranked => new(ChunkId, Enum.Parse<ChunkOwner>(OwnerKind.Replace("_", ""), ignoreCase: true), OwnerId, Score);
    }
}
