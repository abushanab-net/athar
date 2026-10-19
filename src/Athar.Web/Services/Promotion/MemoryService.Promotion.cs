using System.Text.Json;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record PromoteInput(Guid PageId, string? AnchorId, string? Selection, string Scope, Guid? OwnerId, string Type, string Title, string OpId);
public sealed record PromoteResult(string Result, Guid? MemoryId = null, Guid? ProposalId = null);
public sealed record OpenConflictInput(Guid MemoryA, Guid MemoryB, string Note, string OpId);
public sealed record ConflictOpened(Guid ConflictId);
/// <param name="Resolution">keep_a, keep_b, merged or not_conflict. <c>merged</c> needs <paramref name="BodyMd"/>: A's new text.</param>
public sealed record ResolveConflictInput(Guid ConflictId, string Resolution, string OpId, string? Title = null, string? BodyMd = null);
/// <param name="Status"><c>resolved</c>, or <c>pending_approval</c> while a company change waits as a proposal.</param>
public sealed record ConflictResolved(string Status, Guid[]? ProposalIds = null);
public sealed record ConflictSide(Guid Id, string Title, string Scope, string Status, string BodyMd, int Revision);
public sealed record ConflictView(Guid Id, ConflictSide A, ConflictSide B, string Status, string? Resolution, string? Note, string? OpenedBy, DateTimeOffset CreatedAt);

/// <summary>
/// T062, Lane D: promote a page section to a memory (FR-MM-05) and manual conflicts (FR-MM-08), on the memory domain's own
/// write rules, proposals and durable op_ids.
/// </summary>
public sealed partial class MemoryService
{
    /// <summary>
    /// <c>promote_to_memory</c>: a selection from the page (or the text an inline comment anchors) becomes a memory linked to
    /// the page, as confidential as the page's space (FR-CL-01). A company promotion by a non-owner is a proposal.
    /// </summary>
    public Task<PromoteResult> PromoteAsync(Actor actor, PromoteInput input, CancellationToken ct)
    {
        Require(actor, "memory:write");
        NotBlank(input.Title, "title");
        var type = ParseEnum<MemoryType>(input.Type, "type");
        var scope = ParseEnum<Scope>(input.Scope, "scope");
        if (string.IsNullOrWhiteSpace(input.AnchorId) == string.IsNullOrWhiteSpace(input.Selection))
            throw new AtharError("invalid_input", "Pass exactly one of anchor_id or selection.");
        if (scope is Scope.Team or Scope.Project && input.OwnerId is null)
            throw new AtharError("invalid_input", $"owner_id is required for {input.Scope} scope.");

        return WriteOnce(actor, input.OpId, new { tool = "promote_to_memory", input = input with { OpId = "" } }, async who =>
        {
            var caller = await db.ActorAsync(actor.UserId, actor.CredentialId, ct) ?? throw new AtharError("unauthorized", "The user is inactive or the credential is revoked.");
            var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == input.PageId && p.DeletedAt == null, ct);
            if (page is null || !await db.CanAsync(caller, page.Id, PageAction.Read, ct)) throw new AtharError("not_found", "Page not found.");
            var confidential = await db.Spaces.Where(s => s.Id == page.SpaceId).Select(s => s.IsConfidential).SingleAsync(ct);

            string body;
            if (!string.IsNullOrWhiteSpace(input.Selection))
            {
                body = input.Selection.Trim();
                if (!Squash(page.ContentMd ?? "").Contains(Squash(body), StringComparison.Ordinal))
                    throw new AtharError("invalid_input", "The selection isn't on the page (it may have changed).");
            }
            else
                body = await db.CommentThreads.Where(t => t.PageId == page.Id && t.AnchorId == input.AnchorId).Select(t => t.Quote).FirstOrDefaultAsync(ct)
                    ?? throw new AtharError("not_found", "No such anchor on the page.");

            var owner = scope == Scope.Personal ? actor.UserId : input.OwnerId;
            var pageUrl = $"/wiki/p/{page.Id}";
            switch (MemoryRules.CanWrite(who, scope, owner))
            {
                case WriteDecision.Publish:
                    var m = AddMemory(actor.UserId, actor.CredentialId, scope, owner, type, input.Title, body, "promote", null, confidential);
                    db.MemoryLinks.Add(new MemoryLink { MemoryId = m.Id, TargetKind = "page", TargetId = page.Id.ToString(), Anchor = input.AnchorId });
                    Audit(actor, "memory.create", "memory", m.Id, null, new { m.Title, scope = Snake(scope), page_id = page.Id, origin = "promote" });
                    return (new PromoteResult("published", MemoryId: m.Id), OperationTarget.Memory, m.Id);
                case WriteDecision.Propose:
                    var p = AddProposal(actor, ProposalKind.New, null, null, input.Title, body, Snake(type), [pageUrl],
                        [new EvidenceRef("page", page.Id.ToString())], confidential);
                    return (new PromoteResult("proposed", ProposalId: p.Id), OperationTarget.Proposal, p.Id);
                default:
                    throw new AtharError("forbidden", $"You can't write {input.Scope} memories here.");
            }
        }, ct);
    }

    /// <summary><c>open_conflict</c>: anyone who can read both memories. An open conflict on the same pair is returned as is.</summary>
    public Task<ConflictOpened> OpenConflictAsync(Actor actor, OpenConflictInput input, CancellationToken ct)
    {
        Require(actor, "memory:write");
        NotBlank(input.Note, "note");
        if (input.MemoryA == input.MemoryB) throw new AtharError("invalid_input", "A memory can't conflict with itself.");

        return WriteOnce(actor, input.OpId, new { tool = "open_conflict", input = input with { OpId = "" } }, async who =>
        {
            await FindVisible(who, input.MemoryA, ct);
            await FindVisible(who, input.MemoryB, ct);
            var existing = await db.MemoryConflicts.Where(c => c.Status == ConflictStatus.Open
                    && ((c.MemoryA == input.MemoryA && c.MemoryB == input.MemoryB) || (c.MemoryA == input.MemoryB && c.MemoryB == input.MemoryA)))
                .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (existing is { } id) return (new ConflictOpened(id), OperationTarget.Conflict, id);
            var conflict = new MemoryConflict { MemoryA = input.MemoryA, MemoryB = input.MemoryB, OpenedBy = actor.UserId, Status = ConflictStatus.Open, Note = input.Note.Trim() };
            db.MemoryConflicts.Add(conflict);
            Audit(actor, "conflict.open", "memory_conflict", conflict.Id, null, new { memory_a = input.MemoryA, memory_b = input.MemoryB });
            return (new ConflictOpened(conflict.Id), OperationTarget.Conflict, conflict.Id);
        }, ct);
    }

    /// <summary>
    /// <c>resolve_conflict</c> (FR-MM-08). The losing memory is superseded; <c>merged</c> also gives A a new revision. Every
    /// changed memory needs publish rights. A company memory the resolver may only propose to gets a replace proposal (the
    /// kept or merged text) tagged with the conflict, and the conflict stays open until it's approved; the rest applies now.
    /// </summary>
    public Task<ConflictResolved> ResolveConflictAsync(Actor actor, ResolveConflictInput input, CancellationToken ct)
    {
        Require(actor, "memory:write");
        var resolution = input.Resolution switch
        {
            "keep_a" => ConflictResolution.KeepA,
            "keep_b" => ConflictResolution.KeepB,
            "merged" => ConflictResolution.Merged,
            "not_conflict" => ConflictResolution.NotConflict,
            _ => throw new AtharError("invalid_input", "resolution must be keep_a, keep_b, merged or not_conflict."),
        };
        if (resolution == ConflictResolution.Merged) NotBlank(input.BodyMd, "body_md");

        return WriteOnce(actor, input.OpId, new { tool = "resolve_conflict", input = input with { OpId = "" } }, async who =>
        {
            var conflict = await db.MemoryConflicts.FirstOrDefaultAsync(c => c.Id == input.ConflictId, ct) ?? throw new AtharError("not_found", "No such conflict.");
            var a = await FindVisible(who, conflict.MemoryA, ct);
            var b = await FindVisible(who, conflict.MemoryB, ct);
            if (conflict.Status != ConflictStatus.Open) throw new AtharError("invalid_input", "The conflict is already resolved.");

            // What happens to each memory: superseded (null text) or revised to (title, body).
            List<(Memory M, string? Title, string? Body, Memory? Kept)> changes = resolution switch
            {
                ConflictResolution.KeepA => [(b, null, null, a)],
                ConflictResolution.KeepB => [(a, null, null, b)],
                ConflictResolution.Merged => [(a, input.Title ?? a.Title, input.BodyMd, null), (b, null, null, a)],
                _ => [],
            };
            var decisions = changes.Select(c => MemoryRules.CanWrite(who, c.M.Scope, MemoryRules.OwnerOf(c.M))).ToList();
            if (decisions.Contains(WriteDecision.Deny)) throw new AtharError("forbidden", "Resolving this needs publish rights on every memory it changes.");

            var proposals = new List<Guid>();
            foreach (var ((m, title, body, kept), decision) in changes.Zip(decisions))
            {
                if (decision == WriteDecision.Propose)
                {
                    // The company memory takes the merged text, or the kept memory's text; a knowledge owner decides.
                    var (newTitle, newBody) = body is not null ? (title!, body) : resolution == ConflictResolution.Merged ? (input.Title ?? a.Title, input.BodyMd!) : (kept!.Title, kept.BodyMd);
                    proposals.Add(AddProposal(actor, ProposalKind.Replace, m.Id, m.Revision, newTitle, newBody, Snake(m.Type), null,
                        [new EvidenceRef("conflict", conflict.Id.ToString())], m.IsConfidential).Id);
                }
                else if (body is not null)
                {
                    if (!await ReviseAsync(actor, m, m.Revision, title, body, $"conflict {conflict.Id}", ct)) throw RevisionMoved(m);
                }
                else
                {
                    var now = DateTimeOffset.UtcNow;
                    if (await db.Memories.Where(x => x.Id == m.Id && x.Status == MemoryStatus.Active)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MemoryStatus.Superseded).SetProperty(x => x.UpdatedAt, now), ct) == 0)
                        throw RevisionMoved(m);
                    Audit(actor, "memory.supersede", "memory", m.Id, new { status = "active" }, new { status = "superseded", conflict_id = conflict.Id });
                }
            }

            conflict.Resolution = resolution;
            if (proposals.Count == 0) conflict.Status = ConflictStatus.Resolved;
            Audit(actor, "conflict.resolve", "memory_conflict", conflict.Id, new { status = "open" },
                new { resolution = input.Resolution, status = proposals.Count == 0 ? "resolved" : "pending_approval", proposals });
            return (proposals.Count == 0 ? new ConflictResolved("resolved") : new ConflictResolved("pending_approval", [.. proposals]),
                OperationTarget.Conflict, conflict.Id);
        }, ct);
    }

    /// <summary>
    /// The Review › Conflicts list (T151, FR-MM-08): conflicts on memories the actor can read both of, the open ones first.
    /// A conflict waiting on a company proposal stays open, so it stays here until the knowledge owner decides.
    /// </summary>
    public async Task<IReadOnlyList<ConflictView>> ConflictsAsync(Actor actor, bool includeResolved, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var who = await LoadActor(actor, ct);
        var visible = db.Memories.AsNoTracking().Where(MemoryRules.Visible(who));
        var rows = await (from c in db.MemoryConflicts.AsNoTracking()
                          where includeResolved || c.Status == ConflictStatus.Open
                          join a in visible on c.MemoryA equals a.Id
                          join b in visible on c.MemoryB equals b.Id
                          orderby c.Status, c.CreatedAt descending
                          select new { c, a, b }).Take(200).ToListAsync(ct);
        var openers = rows.Select(r => r.c.OpenedBy).Distinct().ToList();
        var names = await db.Users.Where(u => openers.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        static ConflictSide Side(Memory m) => new(m.Id, m.Title, Snake(m.Scope), Snake(m.Status), m.BodyMd, m.Revision);
        return [.. rows.Select(r => new ConflictView(r.c.Id, Side(r.a), Side(r.b), Snake(r.c.Status), r.c.Resolution switch { ConflictResolution.KeepA => "keep_a", ConflictResolution.KeepB => "keep_b", ConflictResolution.Merged => "merged", ConflictResolution.NotConflict => "not_conflict", null => null },
            r.c.Note, names.GetValueOrDefault(r.c.OpenedBy), r.c.CreatedAt))];
    }

    /// <summary>
    /// Called from <see cref="ApproveAsync"/> in its transaction: a conflict whose company change was proposed closes once no
    /// proposal tagged with it is still pending (FR-MM-08).
    /// </summary>
    private async Task SettleConflictsAsync(Actor actor, MemoryProposal approved, CancellationToken ct)
    {
        foreach (var e in Evidence(approved).Where(e => e.Kind == "conflict"))
        {
            var tag = JsonSerializer.Serialize(new[] { new EvidenceRef("conflict", e.Id) }, McpEndpoint.Json);
            var stillPending = await db.MemoryProposals.AnyAsync(p => p.Id != approved.Id && p.Status == ProposalStatus.Pending
                && p.Evidence != null && EF.Functions.JsonContains(p.Evidence, tag), ct);
            var conflict = await db.MemoryConflicts.FirstOrDefaultAsync(c => c.Id == Guid.Parse(e.Id) && c.Status == ConflictStatus.Open, ct);
            if (stillPending || conflict is null) continue;
            conflict.Status = ConflictStatus.Resolved;
            Audit(actor, "conflict.resolve", "memory_conflict", conflict.Id, new { status = "open" }, new { status = "resolved", proposal_id = approved.Id });
        }
    }

    /// <summary>Whitespace-insensitive comparison text, so a selection matches across Markdown line breaks.</summary>
    private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
