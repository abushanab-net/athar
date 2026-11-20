using Athar.Data;
using Athar.Domain.Model;
using Athar.Web.Mcp;

namespace Athar.Web.Services;

/// <summary>What a reader is asked: the caller, the text and the caller's project and scope filters.</summary>
public sealed record EvidenceQuery(Actor Actor, string Text, Guid[]? ProjectIds = null, Scope[]? Scopes = null)
{
    public override string ToString() => nameof(EvidenceQuery);
}

/// <summary>
/// One ranked chunk from a reader. <see cref="Hit"/> is how it is listed and cited; <see cref="State"/> is the reader's own
/// record of what it read (revision, hashes), handed back to it for the current-access check.
/// </summary>
public sealed record ExternalEvidence(Guid ChunkId, Guid OwnerId, string Body, double Score, SearchHit Hit, object State)
{
    public override string ToString() => $"{nameof(ExternalEvidence)}:{Hit.Kind}:{OwnerId}:{ChunkId}"; // never the body
}

public sealed record ExternalEvidenceBatch(IReadOnlyList<ExternalEvidence> Evidence, bool Degraded);

/// <summary>
/// A source of search evidence that Athar's own tables don't hold (an edition or plug-in registers one). Search, answers and
/// recall keep the orchestration: one query embedding, fusion with their own results, grouping and the audit row. The reader
/// keeps what only it knows: who may read its rows, how they rank and whether they are still current.
/// </summary>
public interface IEvidenceReader
{
    /// <summary>The search kind its hits carry, for example <c>document</c>.</summary>
    string Kind { get; }

    /// <summary>A session on the caller's context: same connection, and the same transaction when one is open.</summary>
    Task<IEvidenceSession> OpenAsync(AtharDbContext db, Actor actor, CancellationToken ct);
}

public interface IEvidenceSession : IAsyncDisposable
{
    /// <summary>Whether the caller may read any evidence for this query. False means no ranking and no embedding call for it.</summary>
    Task<bool> HasAsync(EvidenceQuery query, CancellationToken ct);

    /// <summary>Whether vectors from this embedding snapshot can rank the reader's rows.</summary>
    bool AcceptsEmbedding(AiSnapshot snapshot);

    /// <summary>Throws <see cref="AtharError"/> when this answer model may not be given the reader's evidence.</summary>
    void RequireAnswerModel(AiSnapshot answer);

    /// <summary>Ranked chunks the caller may read now. <paramref name="vector"/> is the shared query embedding, or null (text ranking only).</summary>
    Task<ExternalEvidenceBatch> RankAsync(EvidenceQuery query, float[]? vector, string? embeddingIdentity, int take, CancellationToken ct);

    /// <summary>Throws <see cref="AtharError"/> unless the caller may still read every item exactly as it was ranked.</summary>
    Task RequireCurrentAsync(EvidenceQuery query, IReadOnlyList<ExternalEvidence> evidence, CancellationToken ct);
}

/// <summary>The registered readers opened for one request, addressed as one.</summary>
public sealed class EvidenceSessions : IAsyncDisposable
{
    private readonly List<(string Kind, IEvidenceSession Session)> sessions = [];
    private readonly HashSet<string> withEvidence = [];

    public static readonly ExternalEvidenceBatch Empty = new([], false);

    /// <summary>Opens every reader (each confirms the caller first), whatever kinds the request names.</summary>
    public static async Task<EvidenceSessions> OpenAsync(IEnumerable<IEvidenceReader>? readers, AtharDbContext db, Actor actor, CancellationToken ct)
    {
        var opened = new EvidenceSessions();
        try
        {
            foreach (var reader in readers ?? [])
                opened.sessions.Add((reader.Kind, await reader.OpenAsync(db, actor, ct)));
            return opened;
        }
        catch { await opened.DisposeAsync(); throw; }
    }

    public static IEnumerable<string> Kinds(IEnumerable<IEvidenceReader>? readers) => (readers ?? []).Select(r => r.Kind);

    /// <summary>Asks the readers of the requested kinds (null: all) and remembers which have evidence; only those rank.</summary>
    public async Task<bool> HasAsync(EvidenceQuery query, IReadOnlySet<string>? kinds, CancellationToken ct)
    {
        withEvidence.Clear();
        foreach (var (kind, session) in sessions)
            if ((kinds is null || kinds.Contains(kind)) && await session.HasAsync(query, ct)) withEvidence.Add(kind);
        return withEvidence.Count > 0;
    }

    public bool AcceptsEmbedding(AiSnapshot snapshot) => sessions.Any(s => withEvidence.Contains(s.Kind) && s.Session.AcceptsEmbedding(snapshot));

    public void RequireAnswerModel(AiSnapshot answer, IEnumerable<ExternalEvidence> used)
    {
        var kinds = used.Select(e => e.Hit.Kind).ToHashSet();
        foreach (var (kind, session) in sessions) if (kinds.Contains(kind)) session.RequireAnswerModel(answer);
    }

    public async Task<ExternalEvidenceBatch> RankAsync(EvidenceQuery query, float[]? vector, string? embeddingIdentity, int take, CancellationToken ct)
    {
        var all = new List<ExternalEvidence>();
        var degraded = false;
        foreach (var (kind, session) in sessions)
        {
            if (!withEvidence.Contains(kind)) continue;
            var batch = await session.RankAsync(query, vector, embeddingIdentity, take, ct);
            all.AddRange(batch.Evidence);
            degraded |= batch.Degraded;
        }
        return new(all, degraded);
    }

    /// <summary>Every reader re-confirms the caller, and its own items among <paramref name="evidence"/>.</summary>
    public async Task RequireCurrentAsync(EvidenceQuery query, IReadOnlyList<ExternalEvidence> evidence, CancellationToken ct)
    {
        foreach (var (kind, session) in sessions)
            await session.RequireCurrentAsync(query, [.. evidence.Where(e => e.Hit.Kind == kind)], ct);
    }

    /// <summary>One hit per owner: its best chunk.</summary>
    public static SearchHit[] Hits(IEnumerable<ExternalEvidence> evidence) => evidence
        .GroupBy(e => (e.Hit.Kind, e.OwnerId)).Select(g => g.OrderByDescending(e => e.Score).ThenBy(e => e.ChunkId).First().Hit).ToArray();

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, session) in sessions) await session.DisposeAsync();
        sessions.Clear();
    }
}
