using System.Text.Json;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record ProposeResult(Guid ProposalId, int? BaseRevision = null);
/// <summary><paramref name="SkillId"/>: set when an approved proposal published a company skill (T141).</summary>
public sealed record ProposalDecision(string Status, Guid? MemoryId = null, int? Revision = null, Guid? SkillId = null);

public sealed record ProposeInput(string Title, string BodyMd, string Type, Guid? TargetMemoryId, string[]? EvidenceLinks, string OpId);

/// <summary>The immutable proposal payload, jsonb <c>{title, body, type, links?}</c> (FR-MM-04, docs/04).</summary>
internal sealed record ProposalPayload(string Title, string Body, string Type, string[]? Links = null);
/// <summary>One piece of evidence: a URL, page, source document or session record.</summary>
public sealed record EvidenceRef(string Kind, string Id);

// Company-memory proposals (FR-MM-04): new, replace and evidence; approval is one conditional update, re-checked inside the transaction.
public sealed partial class MemoryService
{
    /// <summary><c>propose_company_memory</c>: anyone proposes a new company memory or a replacement of one.</summary>
    public Task<ProposeResult> ProposeAsync(Actor actor, ProposeInput input, CancellationToken ct)
    {
        Require(actor, "memory:write");
        NotBlank(input.Title, "title");
        ParseEnum<MemoryType>(input.Type, "type");

        return WriteOnce(actor, input.OpId, new { tool = "propose_company_memory", input = input with { OpId = "" } }, async who =>
        {
            Memory? target = null;
            if (input.TargetMemoryId is { } id)
            {
                target = await FindVisible(who, id, ct);
                if (target.Scope != Scope.Company) throw new AtharError("not_found", "No such company memory.");
            }
            var p = AddProposal(actor, target is null ? ProposalKind.New : ProposalKind.Replace, target?.Id, target?.Revision,
                input.Title, input.BodyMd, input.Type, null, input.EvidenceLinks?.Select(l => new EvidenceRef("url", l)).ToArray(),
                confidential: target?.IsConfidential == true);
            return (new ProposeResult(p.Id, p.BaseRevision), OperationTarget.Proposal, p.Id);
        }, ct);
    }

    /// <summary>
    /// Approves a pending proposal. The claim (pending → approved) is one conditional update, so of two racing decisions
    /// exactly one wins. A replacement whose target has moved past its base goes to <c>needs_rereview</c> instead.
    /// </summary>
    public async Task<ProposalDecision> ApproveAsync(Actor actor, Guid proposalId, int? baseRevision, CancellationToken ct)
    {
        Require(actor, "memory:write");
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireReviewer(actor, ct);
        var p = await ProposalFor(actor, proposalId, ct);
        if (baseRevision is { } b && b != p.BaseRevision)
            throw new AtharError("conflict_revision", $"The proposal is against revision {p.BaseRevision}.");
        await Claim(actor, proposalId, ProposalStatus.Approved, null, ct);

        var payload = JsonSerializer.Deserialize<ProposalPayload>(p.Payload, McpEndpoint.Json)!;
        ProposalDecision decision;
        switch (p.Kind)
        {
            case ProposalKind.New:
                var m = AddMemory(p.ProposedBy, null, Scope.Company, null, ParseEnum<MemoryType>(payload.Type, "type"), payload.Title, payload.Body,
                    "proposal", payload.Links, p.IsConfidential);
                Audit(actor, "memory.create", "memory", m.Id, null, new { m.Title, scope = "company", proposal_id = p.Id });
                decision = new("approved", m.Id, 1);
                break;
            case ProposalKind.Replace:
                var target = await db.Memories.AsNoTracking().FirstAsync(x => x.Id == p.TargetMemoryId, ct);
                if (await ReviseAsync(actor, target, p.BaseRevision!.Value, payload.Title, payload.Body, $"proposal {p.Id}", ct))
                {
                    decision = new("approved", target.Id, p.BaseRevision + 1);
                    break;
                }
                await db.MemoryProposals.Where(x => x.Id == p.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProposalStatus.NeedsRereview), ct);
                decision = new("needs_rereview", target.Id, target.Revision);
                break;
            case ProposalKind.Skill: // T141 (FR-SK-02): publish the proposed package as the proposer, in this transaction.
                var pkg = JsonSerializer.Deserialize<SkillService.SkillProposal>(p.Payload, McpEndpoint.Json)!;
                var skill = await SkillService.UpsertAsync(db, actor.OrgId, actor.UserId, p.ProposedBy, actor.CredentialId, Scope.Company, null,
                    new(pkg.Name, pkg.Description, pkg.BodyMd, "company", null, pkg.Files), p.Id, ct);
                decision = new("approved", Revision: skill.Revision, SkillId: skill.Id);
                break;
            default: // Evidence: attach the links to the target, no new revision.
                foreach (var e in Evidence(p))
                    db.MemoryLinks.Add(new MemoryLink { MemoryId = p.TargetMemoryId!.Value, TargetKind = e.Kind, TargetId = e.Id });
                decision = new("approved", p.TargetMemoryId);
                break;
        }
        if (decision.Status == "approved") await SettleConflictsAsync(actor, p, ct); // FR-MM-08 (T062): a proposed conflict resolution closes it
        Audit(actor, "proposal." + (decision.Status == "approved" ? "approve" : "needs_rereview"), "memory_proposal", p.Id,
            new { status = "pending" }, new { decision.Status, memory_id = decision.MemoryId, decision.Revision });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return decision;
    }

    public async Task<ProposalDecision> RejectAsync(Actor actor, Guid proposalId, string note, CancellationToken ct)
    {
        Require(actor, "memory:write");
        NotBlank(note, "note");
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireReviewer(actor, ct);
        await ProposalFor(actor, proposalId, ct);
        await Claim(actor, proposalId, ProposalStatus.Rejected, note, ct);
        Audit(actor, "proposal.reject", "memory_proposal", proposalId, new { status = "pending" }, new { status = "rejected", note });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new("rejected");
    }

    /// <summary>A new pending proposal from a <c>needs_rereview</c> one, against the target's current revision, linked by <c>rebased_from</c>.</summary>
    public async Task<ProposeResult> RebaseAsync(Actor actor, Guid proposalId, string? title, string? bodyMd, CancellationToken ct)
    {
        Require(actor, "memory:write");
        db.CurrentOrgId = actor.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var who = await LoadActor(actor, ct);
        var old = await ProposalFor(actor, proposalId, ct);
        if (!MemoryRules.CanRebase(who, old)) throw new AtharError("not_found", "No such proposal.");
        if (old.Status != ProposalStatus.NeedsRereview || old.TargetMemoryId is null) throw new AtharError("conflict_revision", "Only a needs_rereview proposal can be rebased.");

        var payload = JsonSerializer.Deserialize<ProposalPayload>(old.Payload, McpEndpoint.Json)!;
        var target = await db.Memories.AsNoTracking().FirstAsync(x => x.Id == old.TargetMemoryId, ct);
        var p = AddProposal(actor, old.Kind, target.Id, target.Revision, title ?? payload.Title, bodyMd ?? payload.Body, payload.Type,
            payload.Links, Evidence(old), MemoryRules.Strictest(old.IsConfidential, target.IsConfidential));
        p.RebasedFrom = old.Id;
        p.ProposedBy = old.ProposedBy;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(p.Id, p.BaseRevision);
    }

    /// <summary>Adds a pending proposal with its audit row (the caller saves).</summary>
    private MemoryProposal AddProposal(Actor actor, ProposalKind kind, Guid? target, int? baseRevision, string title, string body, string type,
        string[]? links, EvidenceRef[]? evidence = null, bool confidential = false)
    {
        var p = new MemoryProposal
        {
            Kind = kind, TargetMemoryId = target, BaseRevision = baseRevision, ProposedBy = actor.UserId, Status = ProposalStatus.Pending,
            Payload = JsonSerializer.Serialize(new ProposalPayload(title.Trim(), body, type, links), McpEndpoint.Json),
            Evidence = evidence is null ? null : JsonSerializer.Serialize(evidence, McpEndpoint.Json),
            IsConfidential = confidential,
        };
        db.MemoryProposals.Add(p);
        Audit(actor, "proposal.create", "memory_proposal", p.Id, null, new { kind = Snake(kind), target_memory_id = target, base_revision = baseRevision });
        return p;
    }

    /// <summary>A proposal the caller may know about: external agents never learn of confidential ones (FR-CL-01).</summary>
    private async Task<MemoryProposal> ProposalFor(Actor actor, Guid id, CancellationToken ct) =>
        await db.MemoryProposals.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == id && (actor.Disclosure != DisclosureClass.ExternalAgent || !x.IsConfidential), ct)
        ?? throw new AtharError("not_found", "No such proposal.");

    private static EvidenceRef[] Evidence(MemoryProposal p) => p.Evidence is null ? [] : JsonSerializer.Deserialize<EvidenceRef[]>(p.Evidence, McpEndpoint.Json) ?? [];

    /// <summary>Knowledge-owner status and an active account, re-checked inside the decision's transaction (FR-MM-04, AT-24).</summary>
    private async Task RequireReviewer(Actor actor, CancellationToken ct)
    {
        var active = await db.Users.AnyAsync(u => u.Id == actor.UserId && u.DeactivatedAt == null, ct);
        if (!active || !MemoryRules.CanReview(await LoadActor(actor, ct)))
            throw new AtharError("forbidden", "Only an active knowledge owner can decide proposals.");
    }

    /// <summary>pending → <paramref name="status"/> in one conditional update. Losing a race, or a decided proposal, is a conflict.</summary>
    private async Task Claim(Actor actor, Guid proposalId, ProposalStatus status, string? note, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var won = await db.MemoryProposals.Where(x => x.Id == proposalId && x.Status == ProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.ReviewerId, actor.UserId)
                .SetProperty(x => x.DecidedAt, now).SetProperty(x => x.Note, note).SetProperty(x => x.UpdatedAt, now), ct);
        if (won == 0)
            throw await db.MemoryProposals.AnyAsync(x => x.Id == proposalId, ct)
                ? new AtharError("conflict_revision", "The proposal was already decided.")
                : new AtharError("not_found", "No such proposal.");
    }
}
