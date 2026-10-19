using System.Text;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Worker.Jobs.Embed;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Athar.Worker.Jobs.Extract;

/// <summary>
/// <c>extract-session</c> (FR-MM-06..07): a committed session's new records become <b>candidates</b> in the owner's capture
/// inbox, never published memories.
/// <list type="bullet">
/// <item>The owner is re-checked first: still active, with a valid credential (FR-ID-08). Otherwise nothing is extracted.</item>
/// <item>Suggested scopes are untrusted: one the owner can neither publish nor propose falls back to personal.</item>
/// <item>No automatic merging: an exact match to the owner's own personal, team or project memory is attached to it as evidence;
/// an exact match to a company memory becomes an evidence proposal. Similar memories (cosine ≥ 0.85) are listed on the
/// candidate for the owner to decide.</item>
/// <item>Auto-accept (T137, FR-MM-06 as amended): a personal candidate is published at once unless the owner turned it off;
/// a team or project one only with the owner's separate switch on and only where the owner can publish. Company never.
/// The candidate keeps the link to its memory, so My sessions can undo it (archive).</item>
/// </list>
/// </summary>
public sealed class ExtractSessionJob(ICandidateExtractor? extractor, IEmbedder? embedder, AiOperationClients? clients = null) : IJob
{
    public ExtractSessionJob(AiOperationClients clients) : this(null, null, clients) { }
    public const double SimilarAt = 0.85;
    public const int ExcerptMax = 500;
    // ponytail: the latest ~24k characters of the new records; chunk long sessions into several calls if they matter.
    public const int TranscriptMax = 24_000;

    public string Kind => IndexJobs.ExtractSession;

    public async Task RunAsync(Job job, AtharDbContext db, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<IndexJobs.SessionPayload>(job.Payload, IndexJobs.Json)!;
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == p.SessionId, ct);
        if (session is null || session.ExtractedUpto >= p.AckedIndex) return; // done already, or by a later job
        if (await db.ActorAsync(session.UserId, session.CredentialId, ct) is null) return; // deactivated, or credential revoked

        var operation = clients is null ? (Embedder: embedder!, Extractor: extractor!) : await clients.ResolveAsync(job.OrgId, ct);
        var records = await db.SessionRecords.Where(r => r.SessionId == session.Id && r.RecordIndex > session.ExtractedUpto && r.RecordIndex <= p.AckedIndex)
            .OrderBy(r => r.RecordIndex).Select(r => r.RedactedRecord).ToListAsync(ct);
        var found = records.Count == 0 ? [] : await operation.Extractor.ExtractAsync(Transcript(records), ct);

        var owner = await OwnerAsync(db, session.UserId, ct);
        // The session's project: as recorded, else the owner's one member project linked to its repo (FR-PJ-02).
        var remote = ProjectRules.NormalizeRemote(session.RepoRemote);
        var linked = remote is null ? [] : await db.ProjectLinks.Where(l => l.RepoRemote == remote && owner.ProjectIds.Contains(l.ProjectId))
            .Select(l => l.ProjectId).Distinct().ToListAsync(ct);
        var projectId = session.ProjectId ?? (linked.Count == 1 ? linked[0] : null);
        var readable = db.Memories.Where(MemoryRules.Visible(owner)).Where(m => m.Status == MemoryStatus.Active);
        Guid[] visible = [.. await readable.Select(m => m.Id).ToListAsync(ct)];
        var settings = await db.CaptureSettings.AsNoTracking().Where(s => s.UserId == session.UserId)
            .Select(s => new { s.AutoAcceptPersonal, s.AutoAcceptShared }).SingleOrDefaultAsync(ct);
        var (autoPersonal, autoShared) = settings is null ? (true, false) : (settings.AutoAcceptPersonal, settings.AutoAcceptShared);
        var now = DateTimeOffset.UtcNow;
        int candidates = 0, evidence = 0, autoAccepted = 0;
        foreach (var c in found)
        {
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.BodyMd) || !Enum.TryParse<MemoryType>(c.Type, true, out var type)) continue;
            var suggested = Enum.TryParse<Scope>(c.Scope, true, out var s) && Enum.IsDefined(s) ? s : Scope.Personal;
            // The model can't name owners: a project is the session's, a team only when the owner has exactly one.
            Guid? suggestedOwner = suggested switch
            {
                Scope.Project => projectId,
                Scope.Team => owner.TeamIds.Count == 1 ? owner.TeamIds.First() : null,
                _ => null,
            };
            var (scope, scopeOwner) = MemoryRules.EligibleSuggestion(owner, suggested, suggestedOwner);
            var excerpt = Excerpt(c.Evidence ?? c.BodyMd);

            // Exact match: evidence for what exists, never a duplicate and never a change (FR-MM-07).
            var key = MemoryRules.ContentKey(c.BodyMd);
            var same = (await readable.Where(m => m.Scope == Scope.Company
                    || (m.Scope == scope && (m.OwnerUserId == scopeOwner || m.OwnerTeamId == scopeOwner || m.OwnerProjectId == scopeOwner)))
                    .Select(m => new { m.Id, m.Scope, m.Revision, m.BodyMd }).ToListAsync(ct))
                .FirstOrDefault(m => MemoryRules.ContentKey(m.BodyMd) == key);
            if (same is { Scope: Scope.Company })
            {
                db.MemoryProposals.Add(new MemoryProposal
                {
                    Kind = ProposalKind.Evidence, TargetMemoryId = same.Id, BaseRevision = same.Revision, ProposedBy = session.UserId, Status = ProposalStatus.Pending,
                    Payload = JsonSerializer.Serialize(new { title = c.Title.Trim(), body = c.BodyMd, type = type.ToString().ToLowerInvariant() }),
                    Evidence = JsonSerializer.Serialize(new[] { new { kind = "session", id = session.Id.ToString() } }),
                });
                evidence++;
                continue;
            }
            if (same is not null)
            {
                db.MemoryLinks.Add(new MemoryLink { MemoryId = same.Id, TargetKind = "session", TargetId = session.Id.ToString(), Anchor = excerpt });
                evidence++;
                continue;
            }

            var candidate = new CaptureCandidate
            {
                SessionId = session.Id, OwnerUserId = session.UserId, SuggestedScope = scope, SuggestedOwner = scopeOwner,
                Type = type, Title = c.Title.Trim(), BodyMd = c.BodyMd, EvidenceExcerpt = excerpt, Status = CandidateStatus.New,
                SimilarIds = await SimilarAsync(db, session.OrgId, c.Title + "\n" + c.BodyMd, visible, operation.Embedder, ct),
            };
            db.CaptureCandidates.Add(candidate);
            candidates++;

            var memoryOwner = scope == Scope.Personal ? session.UserId : scopeOwner;
            var auto = scope switch { Scope.Personal => autoPersonal, Scope.Team or Scope.Project => autoShared, _ => false };
            if (!auto || MemoryRules.CanWrite(owner, scope, memoryOwner) != WriteDecision.Publish) continue;
            var n = MemoryFactory.Create(session.UserId, session.CredentialId, scope, memoryOwner, type, candidate.Title, candidate.BodyMd, "capture", candidate.IsConfidential, now);
            db.Memories.Add(n.Memory);
            db.MemoryVersions.Add(n.Version);
            db.Chunks.AddRange(n.Chunks);
            db.Jobs.Add(n.IndexJob);
            db.MemoryLinks.Add(new MemoryLink { MemoryId = n.Memory.Id, TargetKind = "session", TargetId = session.Id.ToString(), Anchor = excerpt });
            candidate.Status = CandidateStatus.AutoAccepted;
            candidate.MemoryId = n.Memory.Id;
            db.Audit(session.OrgId, session.UserId, "capture.auto_accept", "capture_candidate", candidate.Id,
                after: new { status = "auto_accepted", scope = scope.ToString().ToLowerInvariant(), memory_id = n.Memory.Id }, credentialId: session.CredentialId);
            autoAccepted++;
        }

        session.ExtractedUpto = p.AckedIndex;
        db.Audit(session.OrgId, session.UserId, "session.extract", "session", session.Id,
            after: new { upto = p.AckedIndex, candidates, evidence, auto_accepted = autoAccepted }, credentialId: session.CredentialId);
    }

    /// <summary>The owner's memories with a chunk at cosine ≥ <see cref="SimilarAt"/>, most similar first (shown, never merged).</summary>
    private async Task<Guid[]> SimilarAsync(AtharDbContext db, Guid org, string text, Guid[] visible, IEmbedder embedder, CancellationToken ct)
    {
        var v = new Vector((await embedder.EmbedAsync([text], ct))[0]);
        var rows = await db.Database.SqlQuery<Guid>($"""
            SELECT c.owner_id AS "Value"
            FROM chunk_embeddings e JOIN chunks c ON c.org_id = e.org_id AND c.id = e.chunk_id
            WHERE e.org_id = {org} AND e.model_id = {embedder.ModelId} AND c.owner_kind = 'memory' AND c.owner_id = ANY({visible})
            GROUP BY c.owner_id
            HAVING max(1 - (e.embedding <=> {v})) >= {SimilarAt}
            ORDER BY max(1 - (e.embedding <=> {v})) DESC
            LIMIT 5
            """).ToListAsync(ct);
        return [.. rows];
    }

    /// <summary>
    /// Records as "role: text" lines, the latest kept when the session is long. Records are the hook's redacted JSON, so the
    /// text is collected from any <c>text</c> or string <c>content</c> fields (Claude Code and Codex shapes alike).
    /// </summary>
    private static string Transcript(List<string> records)
    {
        var lines = records.Select(r =>
        {
            using var doc = JsonDocument.Parse(r);
            var texts = new List<string>();
            Collect(doc.RootElement, texts);
            var role = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("role", out var x) && x.ValueKind == JsonValueKind.String
                ? x.GetString() : "record";
            return texts.Count > 0 ? $"{role}: {string.Join(" ", texts)}" : r;
        }).ToList();
        var sb = new StringBuilder();
        for (var i = lines.Count - 1; i >= 0 && sb.Length + lines[i].Length < TranscriptMax; i--) sb.Insert(0, lines[i] + "\n");
        return sb.ToString();
    }

    private static void Collect(JsonElement e, List<string> texts)
    {
        if (e.ValueKind == JsonValueKind.Array) foreach (var i in e.EnumerateArray()) Collect(i, texts);
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
            if (p.Name is "text" or "content" && p.Value.ValueKind == JsonValueKind.String) texts.Add(p.Value.GetString()!);
            else Collect(p.Value, texts);
    }

    private static string Excerpt(string text) => text.Length <= ExcerptMax ? text : text[..ExcerptMax];

    // ponytail: the same loader as Web's BrainAccess.MemoryActorAsync; the owner reads as a person (not an external agent).
    private static async Task<MemoryActor> OwnerAsync(AtharDbContext db, Guid userId, CancellationToken ct) => new(
        userId,
        await db.OrgMembers.AnyAsync(m => m.UserId == userId && m.IsKnowledgeOwner, ct),
        false,
        await db.TeamMembers.Where(m => m.UserId == userId).Select(m => m.TeamId).ToListAsync(ct),
        await db.ProjectMembers.Where(m => m.UserId == userId).Select(m => m.ProjectId).ToListAsync(ct),
        await db.ProjectMembers.Where(m => m.UserId == userId && m.Role == ProjectRole.Admin).Select(m => m.ProjectId).ToListAsync(ct));
}
