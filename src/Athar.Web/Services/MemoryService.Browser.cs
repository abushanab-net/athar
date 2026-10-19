using System.Text.Json;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

/// <summary><paramref name="Sort"/>: title, scope, type, revision or updated (the default), applied before paging.</summary>
public sealed record MemoryFilter(string? Scope, string? Type, string? Status, Guid? ProjectId, bool? Stale, int Skip = 0, int Take = 50,
    string? Sort = null, bool Descending = true);
public sealed record MemoryListItem(Guid Id, string Title, string Scope, string Type, string Status, int Revision, DateTimeOffset UpdatedAt,
    Guid? OwnerId = null, Guid? ShadowOf = null, bool? Stale = null);
public sealed record ClassifyResult(Guid MemoryId, bool IsConfidential);
public sealed record MemoryPage(IReadOnlyList<MemoryListItem> Items, int Total);
public sealed record MemoryBrief(string Title, DateTimeOffset UpdatedAt);
public sealed record MemoryVersionView(int Revision, string Title, string BodyMd, DateTimeOffset At, Guid? ChangedBy = null, string? Reason = null);
public sealed record MemoryDetail(MemoryListItem Memory, string BodyMd, bool Overridable, IReadOnlyList<string> Links, IReadOnlyList<MemoryVersionView> History,
    bool IsConfidential);
public sealed record PendingEdit(Guid ProposalId, string Title, string BodyMd, DateTimeOffset At);

public sealed record ProposalText(string Title, string BodyMd, int? Revision = null);
/// <summary>A queue row with everything a diff needs: the base the proposer read, the target now, and the proposal.</summary>
public sealed record ProposalView(Guid Id, string Kind, string Status, Guid ProposedBy, DateTimeOffset CreatedAt, ProposalText Proposed,
    IReadOnlyList<EvidenceRef> Evidence, Guid? TargetMemoryId = null, int? BaseRevision = null, ProposalText? Base = null, ProposalText? Current = null,
    Guid? RebasedFrom = null, string? Note = null, string? ProposedByName = null);
public sealed record AcceptInput(string? Title = null, string? BodyMd = null, string? Type = null, string? Scope = null, Guid? OwnerId = null);
public sealed record CandidateDecision(string Status);
public sealed record CandidateView(Guid Id, string Title, string BodyMd, string Type, string SuggestedScope, DateTimeOffset CreatedAt,
    IReadOnlyList<Guid> SimilarIds, Guid? SuggestedOwner = null, string? EvidenceExcerpt = null);

// The memory browser, proposal queue and capture inbox (FR-MM-09). Reads go through the same visibility rule as recall.
public sealed partial class MemoryService
{
    public async Task<MemoryPage> ListAsync(Actor actor, MemoryFilter f, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var who = await LoadActor(actor, ct);
        var status = f.Status is null ? MemoryStatus.Active : ParseEnum<MemoryStatus>(f.Status, "status");
        var q = db.Memories.AsNoTracking().Where(MemoryRules.Visible(who)).Where(m => m.Status == status);
        if (f.Scope is not null) { var s = ParseEnum<Scope>(f.Scope, "scope"); q = q.Where(m => m.Scope == s); }
        if (f.Type is not null) { var t = ParseEnum<MemoryType>(f.Type, "type"); q = q.Where(m => m.Type == t); }
        if (f.ProjectId is { } p) q = q.Where(m => m.OwnerProjectId == p);

        var rows = from m in q
                   join s in db.MemoryShadows.Where(s => s.DeletedAt == null) on m.Id equals s.ShadowMemoryId into sj
                   from s in sj.DefaultIfEmpty()
                   select new { m, Shadow = s };
        if (f.Stale is { } stale) rows = rows.Where(r => r.Shadow != null && r.Shadow.Stale == stale);

        var total = await rows.CountAsync(ct);
        var ordered = (f.Sort, f.Descending) switch
        {
            ("title", false) => rows.OrderBy(r => r.m.Title),
            ("title", true) => rows.OrderByDescending(r => r.m.Title),
            ("scope", false) => rows.OrderBy(r => r.m.Scope),
            ("scope", true) => rows.OrderByDescending(r => r.m.Scope),
            ("type", false) => rows.OrderBy(r => r.m.Type),
            ("type", true) => rows.OrderByDescending(r => r.m.Type),
            ("revision", false) => rows.OrderBy(r => r.m.Revision),
            ("revision", true) => rows.OrderByDescending(r => r.m.Revision),
            (_, false) => rows.OrderBy(r => r.m.UpdatedAt),
            _ => rows.OrderByDescending(r => r.m.UpdatedAt),
        };
        var page = await ordered.ThenBy(r => r.m.Id)
            .Skip(Math.Max(f.Skip, 0)).Take(Math.Clamp(f.Take, 1, 100)).ToListAsync(ct);
        return new([.. page.Select(r => Item(r.m, r.Shadow))], total);
    }

    /// <summary>Title and last update of the memories the actor can read, by id: names for ids the pages would otherwise show.</summary>
    public async Task<Dictionary<Guid, MemoryBrief>> BriefsAsync(Actor actor, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var who = await LoadActor(actor, ct);
        return await db.Memories.AsNoTracking().Where(MemoryRules.Visible(who)).Where(m => list.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => new MemoryBrief(m.Title, m.UpdatedAt), ct);
    }

    /// <summary>One memory with its links and full revision history (FR-MM-01). Archived and superseded ones are readable too.</summary>
    public async Task<MemoryDetail> GetAsync(Actor actor, Guid id, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var who = await LoadActor(actor, ct);
        var m = await db.Memories.AsNoTracking().Where(MemoryRules.Visible(who)).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AtharError("not_found", "No such memory.");
        var shadow = await db.MemoryShadows.AsNoTracking().FirstOrDefaultAsync(s => s.ShadowMemoryId == id && s.DeletedAt == null, ct);
        var links = (await LinksFor(actor.Caller, [id], ct))[id].ToList();
        var history = await db.MemoryVersions.Where(v => v.MemoryId == id).OrderByDescending(v => v.Revision)
            .Select(v => new MemoryVersionView(v.Revision, v.Title, v.BodyMd, v.CreatedAt, v.ChangedBy, v.Reason)).ToListAsync(ct);
        return new(Item(m, shadow), m.BodyMd, m.Overridable, links, history, m.IsConfidential);
    }

    /// <summary>
    /// The caller's own open edit of a memory they can read (a replace proposal, pending or needs_rereview), newest first:
    /// a member's edit of a company memory waits for a knowledge owner, so the memory page shows it (FR-MM-04).
    /// </summary>
    public async Task<PendingEdit?> PendingEditAsync(Actor actor, Guid id, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var mine = await db.MemoryProposals.AsNoTracking()
            .Where(p => p.TargetMemoryId == id && p.ProposedBy == actor.UserId && p.Kind == ProposalKind.Replace
                && (p.Status == ProposalStatus.Pending || p.Status == ProposalStatus.NeedsRereview))
            .OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        if (mine is null) return null;
        var payload = JsonSerializer.Deserialize<ProposalPayload>(mine.Payload, McpEndpoint.Json)!;
        return new(mine.Id, payload.Title, payload.Body, mine.CreatedAt);
    }

    /// <summary>
    /// Archives a memory the caller can publish to. Archiving a company memory ends its shadows' link to it, so each shadow
    /// stays as a plain project memory (FR-MM-03).
    /// </summary>
    public async Task<MemoryDetail> ArchiveAsync(Actor actor, Guid id, string? reason, CancellationToken ct)
    {
        Require(actor, "memory:write");
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var who = await LoadActor(actor, ct);
        var m = await FindVisible(who, id, ct);
        if (MemoryRules.CanWrite(who, m.Scope, MemoryRules.OwnerOf(m)) != WriteDecision.Publish)
            throw new AtharError("forbidden", "You can't archive this memory.");
        var now = DateTimeOffset.UtcNow;
        if (await db.Memories.Where(x => x.Id == id && x.Status == MemoryStatus.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MemoryStatus.Archived).SetProperty(x => x.UpdatedAt, now), ct) == 0)
            throw new AtharError("conflict_revision", "The memory changed; reload and retry.");
        if (m.Scope == Scope.Company)
            await db.MemoryShadows.Where(s => s.CompanyMemoryId == id && s.DeletedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, now), ct);
        Audit(actor, "memory.archive", "memory", id, new { status = "active" }, new { status = "archived", reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// Marks a memory confidential, or declassifies it with a stated reason (FR-CL-01). Only someone who can publish to it
    /// may (for company memories, a knowledge owner), and it's audited. Raising the classification also raises what was
    /// derived from the memory: its chunks, its project shadows and their chunks, and its open proposals. Declassifying
    /// lowers only the memory and its chunks; derived items are declassified on their own.
    /// </summary>
    public async Task<ClassifyResult> ClassifyAsync(Actor actor, Guid id, bool confidential, string? reason, CancellationToken ct)
    {
        Require(actor, "memory:write");
        if (!confidential) NotBlank(reason, "reason");
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var who = await LoadActor(actor, ct);
        var m = await db.Memories.AsNoTracking().Where(MemoryRules.Visible(who)).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AtharError("not_found", "No such memory.");
        if (!MemoryRules.CanClassify(who, m)) throw new AtharError("forbidden", "Only someone who can publish to this memory can classify it.");
        if (m.IsConfidential != confidential)
        {
            Guid[] ids = [id];
            if (confidential)
                ids = [id, .. await db.MemoryShadows.Where(s => s.CompanyMemoryId == id && s.DeletedAt == null).Select(s => s.ShadowMemoryId).ToListAsync(ct)];
            var now = DateTimeOffset.UtcNow;
            await db.Memories.Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsConfidential, confidential).SetProperty(x => x.UpdatedAt, now), ct);
            await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.Memory && ids.Contains(c.OwnerId))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsConfidential, confidential), ct);
            if (confidential)
                await db.MemoryProposals.Where(p => p.TargetMemoryId != null && ids.Contains(p.TargetMemoryId.Value)
                        && (p.Status == ProposalStatus.Pending || p.Status == ProposalStatus.NeedsRereview))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsConfidential, true), ct);
            Audit(actor, confidential ? "memory.classify" : "memory.declassify", "memory", id,
                new { is_confidential = m.IsConfidential }, new { is_confidential = confidential, reason, derived = ids.Length - 1 });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        // Not the detail: an external caller that just raised the classification may no longer read it.
        return new(id, confidential);
    }

    /// <summary>
    /// Links per memory, as the caller may see them: a page link only if the caller can read that page, so external agents
    /// never get the IDs of confidential (or restricted) pages (FR-CL-01, AT-05).
    /// </summary>
    // ponytail: page links are checked; source-document and session links get the same treatment when T070/T060 create them.
    private async Task<ILookup<Guid, string>> LinksFor(Caller caller, Guid[] memoryIds, CancellationToken ct)
    {
        var links = await db.MemoryLinks.AsNoTracking().Where(l => memoryIds.Contains(l.MemoryId)).OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var pageIds = links.Where(l => l.TargetKind == "page").Select(l => Guid.TryParse(l.TargetId, out var g) ? g : Guid.Empty).Distinct().ToArray();
        var readable = pageIds.Length == 0 ? [] : (await db.PagesFor(caller)
            .Where(p => pageIds.Contains(p.Id) && p.Status != PageStatus.Draft).Select(p => p.Id).ToListAsync(ct)).ToHashSet(); // U-1: no draft links
        return links.Where(l => l.TargetKind != "page" || (Guid.TryParse(l.TargetId, out var g) && readable.Contains(g)))
            .ToLookup(l => l.MemoryId, l => l.TargetId);
    }

    /// <summary>Adds a link (URL, page or source reference) to a memory the caller can publish to.</summary>
    public async Task<MemoryDetail> LinkAsync(Actor actor, Guid id, string target, CancellationToken ct)
    {
        Require(actor, "memory:write");
        NotBlank(target, "target");
        db.CurrentOrgId = actor.OrgId;
        var who = await LoadActor(actor, ct);
        var m = await FindVisible(who, id, ct);
        if (MemoryRules.CanWrite(who, m.Scope, MemoryRules.OwnerOf(m)) != WriteDecision.Publish)
            throw new AtharError("forbidden", "You can't edit this memory.");
        db.MemoryLinks.Add(new MemoryLink { MemoryId = id, TargetKind = "url", TargetId = target.Trim() });
        Audit(actor, "memory.link", "memory", id, null, new { target });
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// The proposal queue: knowledge owners see every open proposal (pending and needs_rereview); anyone else sees their own,
    /// in any status (FR-MM-04, FR-MM-09).
    /// </summary>
    public async Task<IReadOnlyList<ProposalView>> ProposalsAsync(Actor actor, string? status, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var who = await LoadActor(actor, ct);
        var q = db.MemoryProposals.AsNoTracking();
        if (who.ExternalAgent) q = q.Where(p => !p.IsConfidential); // FR-CL-01: not even that they exist
        if (!MemoryRules.CanReview(who)) q = q.Where(p => p.ProposedBy == actor.UserId);
        if (status is not null) { var s = ParseEnum<ProposalStatus>(status.Replace("_", ""), "status"); q = q.Where(p => p.Status == s); }
        else if (MemoryRules.CanReview(who)) q = q.Where(p => p.Status == ProposalStatus.Pending || p.Status == ProposalStatus.NeedsRereview);
        var proposals = await q.OrderBy(p => p.CreatedAt).Take(200).ToListAsync(ct);

        var targets = proposals.Where(p => p.TargetMemoryId != null).Select(p => p.TargetMemoryId!.Value).Distinct().ToArray();
        var current = await db.Memories.AsNoTracking().Where(m => targets.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var proposers = proposals.Select(p => p.ProposedBy).Distinct().ToArray();
        var names = await db.Users.Where(u => proposers.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var versions = (await db.MemoryVersions.AsNoTracking().Where(v => targets.Contains(v.MemoryId)).ToListAsync(ct))
            .ToDictionary(v => (v.MemoryId, v.Revision));

        return [.. proposals.Select(p =>
        {
            if (p.Kind == ProposalKind.Skill) // T141: a company skill package; reviewers read its SKILL.md and file list
            {
                var s = JsonSerializer.Deserialize<SkillService.SkillProposal>(p.Payload, McpEndpoint.Json)!;
                var text = SkillPackage.SkillMd(s.Name, s.Description, s.BodyMd)
                    + string.Concat(s.Files.Select(f => $"\n\n## {f.Path}\n\n```\n{f.Content}\n```"));
                return new ProposalView(p.Id, "skill", SnakeStatus(p.Status), p.ProposedBy, p.CreatedAt, new(s.Name, text), [],
                    Note: p.Note, ProposedByName: names.GetValueOrDefault(p.ProposedBy));
            }
            var payload = JsonSerializer.Deserialize<ProposalPayload>(p.Payload, McpEndpoint.Json)!;
            ProposalText? baseText = p.TargetMemoryId is { } t && p.BaseRevision is { } b && versions.TryGetValue((t, b), out var v)
                ? new(v.Title, v.BodyMd, v.Revision) : null;
            ProposalText? now = p.TargetMemoryId is { } t2 && current.TryGetValue(t2, out var m) ? new(m.Title, m.BodyMd, m.Revision) : null;
            return new ProposalView(p.Id, Snake(p.Kind), SnakeStatus(p.Status), p.ProposedBy, p.CreatedAt,
                new(payload.Title, payload.Body), Evidence(p), p.TargetMemoryId, p.BaseRevision, baseText, now, p.RebasedFrom, p.Note,
                names.GetValueOrDefault(p.ProposedBy));
        })];
    }

    /// <summary>Whether the actor reviews proposals (a knowledge owner), so the queue can offer approve and reject.</summary>
    public async Task<bool> CanReviewAsync(Actor actor, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        return MemoryRules.CanReview(await LoadActor(actor, ct));
    }

    /// <summary>
    /// Accepts one of the caller's candidates, optionally edited (FR-MM-06). Eligibility is re-checked now: a personal, team or
    /// project memory is published where the caller may publish; a company candidate always becomes a proposal (extraction
    /// always proposes for company, FR-MM-02). The claim New → Accepted/Edited is one conditional update.
    /// </summary>
    public async Task<RememberResult> AcceptAsync(Actor actor, Guid id, AcceptInput edits, CancellationToken ct)
    {
        Require(actor, "memory:write");
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await db.CaptureCandidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == actor.UserId, ct)
            ?? throw new AtharError("not_found", "No such candidate.");
        var who = await LoadActor(actor, ct);
        var title = (edits.Title ?? c.Title).Trim();
        var body = edits.BodyMd ?? c.BodyMd;
        NotBlank(title, "title");
        var type = edits.Type is null ? c.Type : ParseEnum<MemoryType>(edits.Type, "type");
        var scope = edits.Scope is null ? c.SuggestedScope : ParseEnum<Scope>(edits.Scope, "scope");
        Guid? owner = scope switch { Scope.Personal => actor.UserId, Scope.Company => null, _ => edits.OwnerId ?? c.SuggestedOwner };
        var edited = title != c.Title || body != c.BodyMd || type != c.Type || scope != c.SuggestedScope || (scope != Scope.Personal && owner != c.SuggestedOwner);

        await ClaimCandidate(id, edited ? CandidateStatus.Edited : CandidateStatus.Accepted, ct);
        RememberResult result;
        if (scope == Scope.Company)
        {
            var p = AddProposal(actor, ProposalKind.New, null, null, title, body, Snake(type), null, [new EvidenceRef("session", c.SessionId.ToString())], c.IsConfidential);
            result = new("proposed", ProposalId: p.Id);
        }
        else if (MemoryRules.CanWrite(who, scope, owner) == WriteDecision.Publish)
        {
            var m = AddMemory(actor.UserId, actor.CredentialId, scope, owner, type, title, body, "capture", null, c.IsConfidential);
            db.MemoryLinks.Add(new MemoryLink { MemoryId = m.Id, TargetKind = "session", TargetId = c.SessionId.ToString(), Anchor = c.EvidenceExcerpt });
            result = new("published", MemoryId: m.Id);
        }
        else throw new AtharError("forbidden", "You can't publish to that scope.");

        Audit(actor, "capture.accept", "capture_candidate", id, new { status = "new" },
            new { status = edited ? "edited" : "accepted", scope = Snake(scope), result.Result, memory_id = result.MemoryId, proposal_id = result.ProposalId });
        await db.SaveChangesAsync(ct);
        // What the candidate became, for My sessions (T138); after the save, so the memory or proposal exists.
        await db.CaptureCandidates.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryId, result.MemoryId).SetProperty(x => x.ProposalId, result.ProposalId), ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Undo of an auto-accepted candidate (T137): archives the memory it became and marks the candidate discarded, in one
    /// transaction and audited. Only the candidate's owner, and only while they can still publish to that memory.
    /// </summary>
    public async Task<CandidateDecision> UndoAutoAcceptAsync(Actor actor, Guid id, CancellationToken ct)
    {
        Require(actor, "memory:write");
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await db.CaptureCandidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == actor.UserId, ct)
            ?? throw new AtharError("not_found", "No such candidate.");
        if (c.Status != CandidateStatus.AutoAccepted || c.MemoryId is not { } memoryId) throw new AtharError("invalid_input", "Only an auto-accepted candidate can be undone.");
        var who = await LoadActor(actor, ct);
        var m = await db.Memories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == memoryId, ct) ?? throw new AtharError("not_found", "No such memory.");
        if (MemoryRules.CanWrite(who, m.Scope, MemoryRules.OwnerOf(m)) != WriteDecision.Publish) throw new AtharError("forbidden", "You can't archive this memory.");
        var now = DateTimeOffset.UtcNow;
        if (await db.CaptureCandidates.Where(x => x.Id == id && x.Status == CandidateStatus.AutoAccepted)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CandidateStatus.Discarded).SetProperty(x => x.UpdatedAt, now), ct) == 0)
            throw new AtharError("conflict_revision", "The candidate was already undone.");
        // Archived already (by hand) is fine: the undo still lands.
        var archived = await db.Memories.Where(x => x.Id == memoryId && x.Status == MemoryStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MemoryStatus.Archived).SetProperty(x => x.UpdatedAt, now), ct) > 0;
        if (archived) Audit(actor, "memory.archive", "memory", memoryId, new { status = "active" }, new { status = "archived", reason = "undo auto-accept" });
        Audit(actor, "capture.undo", "capture_candidate", id, new { status = "auto_accepted" }, new { status = "discarded", memory_id = memoryId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new("undone");
    }

    public async Task<CandidateDecision> DiscardAsync(Actor actor, Guid id, CancellationToken ct)
    {
        Require(actor, "memory:write");
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.CaptureCandidates.AnyAsync(x => x.Id == id && x.OwnerUserId == actor.UserId, ct)) throw new AtharError("not_found", "No such candidate.");
        await ClaimCandidate(id, CandidateStatus.Discarded, ct);
        Audit(actor, "capture.discard", "capture_candidate", id, new { status = "new" }, new { status = "discarded" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new("discarded");
    }

    private async Task ClaimCandidate(Guid id, CandidateStatus status, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (await db.CaptureCandidates.Where(x => x.Id == id && x.Status == CandidateStatus.New)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.UpdatedAt, now), ct) == 0)
            throw new AtharError("conflict_revision", "The candidate was already decided.");
    }

    /// <summary>The capture inbox: the caller's own new candidates (FR-MM-06).</summary>
    public async Task<IReadOnlyList<CandidateView>> InboxAsync(Actor actor, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var rows = await db.CaptureCandidates.AsNoTracking()
            .Where(c => c.OwnerUserId == actor.UserId && c.Status == CandidateStatus.New)
            .OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ct);
        return [.. rows.Select(c => new CandidateView(c.Id, c.Title, c.BodyMd, Snake(c.Type), Snake(c.SuggestedScope), c.CreatedAt,
            c.SimilarIds, c.SuggestedOwner, c.EvidenceExcerpt))];
    }

    private static MemoryListItem Item(Memory m, MemoryShadow? s) => new(m.Id, m.Title, Snake(m.Scope), Snake(m.Type), Snake(m.Status),
        m.Revision, m.UpdatedAt, MemoryRules.OwnerOf(m), s?.CompanyMemoryId, s?.Stale);

    private static string SnakeStatus(ProposalStatus s) => s == ProposalStatus.NeedsRereview ? "needs_rereview" : Snake(s);
}
