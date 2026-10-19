using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.Worker.Jobs.Embed;

/// <summary>
/// <c>embed-page</c> (enqueued with each persisted revision, docs/03 §4.1): re-chunks the page at that revision for search,
/// then queues its vectors. Discarded if the page has moved on or been deleted (§7). The chunks carry the space's
/// classification (FR-CL-01) and space id, so search filters on them.
/// </summary>
public sealed class EmbedPageJob(TimeProvider clock, IConfiguration config) : IJob
{
    /// <summary>
    /// T100: a page is re-indexed at most this often while it's being edited (<c>Athar:Index:PageEvery</c>, default 2 min;
    /// tests use 0). Collab saves every 30 s while people type, and each save re-embedding the page kept the model gateway
    /// busy enough that query embeddings, and so search, waited behind it. The latest revision always gets indexed.
    /// </summary>
    private readonly TimeSpan pageEvery = config.GetValue("Athar:Index:PageEvery", TimeSpan.FromMinutes(2));

    public string Kind => IndexJobs.EmbedPage;

    public async Task RunAsync(Job job, AtharDbContext db, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<IndexJobs.PagePayload>(job.Payload, IndexJobs.Json)!;
        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.PageId, ct);
        if (page is null || page.DeletedAt != null || page.Revision != p.Revision) return; // superseded or gone
        var indexed = await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.Page && c.OwnerId == page.Id).MaxAsync(c => (DateTimeOffset?)c.UpdatedAt, ct);
        if (indexed + pageEvery - clock.GetUtcNow() is { Ticks: > 0 } wait)
            throw new JobDeferred("the page was indexed moments ago", wait); // a newer revision's job may take over meanwhile
        var confidential = await db.Spaces.Where(s => s.Id == page.SpaceId).Select(s => s.IsConfidential).SingleAsync(ct);

        void Keys(Chunk c)
        {
            c.SpaceId = page.SpaceId;
            c.IsConfidential = confidential;
        }
        var chunks = Chunker.Build(ChunkOwner.Page, page.Id, page.Revision, page.Title, page.ContentMd ?? "", Keys);
        // FR-PG-06 (T046): extracted attachment text is searchable as part of its page, under the file's name.
        foreach (var a in await db.Attachments.AsNoTracking().Where(a => a.PageId == page.Id && a.ExtractStatus == ExtractStatus.Done && a.TextContent != null)
                     .OrderBy(a => a.CreatedAt).Select(a => new { a.FileName, a.TextContent }).ToListAsync(ct))
            chunks.AddRange(Chunker.Build(ChunkOwner.Page, page.Id, page.Revision, $"{page.Title} / {a.FileName}", a.TextContent!, Keys));
        for (var i = 0; i < chunks.Count; i++) chunks[i].Idx = i;

        // T100: a chunk whose text is unchanged keeps its row and its vectors (the embedded text is exactly what ContentHash
        // covers), so an edit re-embeds only the sections it touched. Re-embedding whole pages on every save kept the model
        // gateway busy enough to slow query embeddings, and so search, while people were editing.
        var unchanged = (await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.Page && c.OwnerId == page.Id).ToListAsync(ct))
            .GroupBy(c => c.ContentHash).ToDictionary(g => g.Key, g => new Queue<Chunk>(g));
        foreach (var fresh in chunks)
        {
            if (unchanged.TryGetValue(fresh.ContentHash, out var same) && same.TryDequeue(out var kept))
                (kept.Idx, kept.OwnerRevision, kept.SpaceId, kept.IsConfidential) = (fresh.Idx, fresh.OwnerRevision, fresh.SpaceId, fresh.IsConfidential);
            else db.Chunks.Add(fresh);
        }
        db.Chunks.RemoveRange(unchanged.Values.SelectMany(q => q)); // their vectors go with them (cascade)
        // A revision can be indexed again (an attachment's text arrived, T046): re-queue its vectors job rather than insert a
        // duplicate (kind, key), which failed every retry. ponytail: a vectors job already running may miss the new chunks;
        // the next revision catches them.
        await QueueVectorsAsync(db, IndexJobs.Vectors(ChunkOwner.Page, page.Id, page.Revision, clock.GetUtcNow()), ct);
    }

    /// <summary>Queues an <c>embed-vectors</c> job, or makes a finished one with the same (kind, key) run again.</summary>
    public static async Task QueueVectorsAsync(AtharDbContext db, Job vectors, CancellationToken ct)
    {
        if (await db.Jobs.FirstOrDefaultAsync(j => j.Kind == vectors.Kind && j.Key == vectors.Key, ct) is { } queued)
        {
            if (queued.Status is JobStatus.Done or JobStatus.Dead)
                (queued.Status, queued.RunAfter, queued.Attempts, queued.LastError) = (JobStatus.Ready, vectors.RunAfter, 0, null);
        }
        else db.Jobs.Add(vectors);
    }
}

/// <summary>
/// Model changes re-embed everything (T090, docs/03 D8): at start and every 30 seconds, every chunk without a vector from the current model
/// (<see cref="IEmbedder.ModelId"/>, which names the model and the stored size) gets its owner's <c>embed-vectors</c> job
/// queued again. Search reads only the current model's vectors and uses FTS for the rest until they're done.
/// </summary>
public sealed class ReembedSweep(IServiceScopeFactory scopes, IEmbedder? embedder, TimeProvider clock, ILogger<ReembedSweep> log, AiSettingsResolver? resolver = null) : BackgroundService
{
    public ReembedSweep(IServiceScopeFactory scopes, AiSettingsResolver resolver, TimeProvider clock, ILogger<ReembedSweep> log)
        : this(scopes, null, clock, log, resolver) { }
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        for (var attempt = 1; !stop.IsCancellationRequested; attempt++)
        {
            try
            {
                await SweepAsync(stop);
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stop);
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                log.LogInformation("Re-embed sweep waits for the database: {Reason}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), clock, stop);
            }
        }
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
        var count = 0;
        var organizations = await db.Organizations.AsNoTracking().Select(o => o.Id).ToListAsync(ct);
        foreach (var org in organizations)
        {
            try
            {
                db.CurrentOrgId = org;
                var model = resolver is null ? embedder!.ModelId : (await resolver.ResolveAsync(org, ct)).EmbeddingIdentity;
                var owners = await db.Chunks.Where(c => !db.ChunkEmbeddings.Any(e => e.ChunkId == c.Id && e.ModelId == model))
                    .Select(c => new { c.OwnerKind, c.OwnerId, c.OwnerRevision }).Distinct().ToListAsync(ct);
                foreach (var batch in owners.Chunk(500))
                {
                    var jobs = batch.Select(o => IndexJobs.Vectors(o.OwnerKind, o.OwnerId, o.OwnerRevision, clock.GetUtcNow(), resolver is null ? null : model)).ToArray();
                    var keys = jobs.Select(j => j.Key).ToArray();
                    var queued = await db.Jobs.Where(j => j.Kind == IndexJobs.EmbedVectors && keys.Contains(j.Key)).ToDictionaryAsync(j => j.Key, ct);
                    foreach (var job in jobs)
                    {
                        // Dead jobs stay dead; a new generation is a new key, never blocked by a running old job.
                        var existing = queued.GetValueOrDefault(job.Key);
                        if (existing is null) db.Jobs.Add(job);
                        else if (existing.Status == JobStatus.Done)
                            (existing.Status, existing.RunAfter, existing.Attempts, existing.LastError) = (JobStatus.Ready, job.RunAfter, 0, null);
                    }
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                }
                count += owners.Count;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                log.LogWarning("Re-embed sweep could not resolve organization {Org}.", org);
            }
        }
        return count;
    }
}

/// <summary>
/// <c>embed-vectors</c>: bge-m3 vectors for an owner revision's chunks that don't have them yet. Discarded when the chunks
/// are at another revision (the owner moved on; that revision's job does the work). Search keeps working on FTS meanwhile
/// (FR-SR-02 degraded mode), and a model still downloading defers the job without using an attempt.
/// </summary>
public sealed class EmbedVectorsJob(IEmbedder? embedder, AiOperationClients? clients = null) : IJob
{
    public EmbedVectorsJob(AiOperationClients clients) : this(null, clients) { }
    public string Kind => IndexJobs.EmbedVectors;

    public async Task RunAsync(Job job, AtharDbContext db, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<IndexJobs.VectorsPayload>(job.Payload, IndexJobs.Json)!;
        var active = clients is null ? embedder! : (await clients.ResolveAsync(job.OrgId, ct)).Embedder;
        if (p.EmbeddingIdentity is { } target && target != active.ModelId) return;
        var kind = Enum.Parse<ChunkOwner>(p.OwnerKind, ignoreCase: true);
        var chunks = await db.Chunks.Where(c => c.OwnerKind == kind && c.OwnerId == p.OwnerId).OrderBy(c => c.Idx).ToListAsync(ct);
        if (chunks.Count == 0 || chunks.Any(c => c.OwnerRevision != p.Revision)) return; // superseded

        var ids = chunks.Select(c => c.Id).ToArray();
        var done = await db.ChunkEmbeddings.Where(e => ids.Contains(e.ChunkId) && e.ModelId == active.ModelId).Select(e => e.ChunkId).ToListAsync(ct);
        var missing = chunks.Where(c => !done.Contains(c.Id)).ToList();
        // ponytail: one batch per owner, inside the completion transaction. Split prepare/apply if large CPU batches hold
        // connections too long (D8: ~2.6 chunks/s on CPU).
        foreach (var batch in missing.Chunk(32))
        {
            var vectors = await active.EmbedAsync([.. batch.Select(Chunker.EmbeddingText)], ct);
            db.ChunkEmbeddings.AddRange(batch.Zip(vectors, (c, v) => new ChunkEmbedding { ChunkId = c.Id, ModelId = active.ModelId, Embedding = v }));
        }
    }
}
