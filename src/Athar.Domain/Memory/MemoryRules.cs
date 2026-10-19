using System.Linq.Expressions;
using Athar.Domain.Model;

namespace Athar.Domain.Memory;

public enum WriteDecision { Publish, Propose, Deny }

/// <summary>Who a memory rule is evaluated for. Memberships are loaded fresh per request.</summary>
public sealed record MemoryActor(Guid UserId, bool IsKnowledgeOwner, bool ExternalAgent,
    IReadOnlyCollection<Guid> TeamIds, IReadOnlyCollection<Guid> ProjectIds, IReadOnlyCollection<Guid> AdminProjectIds);

/// <summary>Second-brain rules (FR-MM-01..04). Plain C#, no I/O.</summary>
// ponytail: Visible moves into Athar.Domain.Access when T012 lands.
public static class MemoryRules
{
    /// <summary>FR-MM-02: personal is the owner's, team and project need membership, company publishes for knowledge owners and is proposed by anyone else.</summary>
    public static WriteDecision CanWrite(MemoryActor a, Scope scope, Guid? ownerId) => scope switch
    {
        Scope.Personal => ownerId is null || ownerId == a.UserId ? WriteDecision.Publish : WriteDecision.Deny,
        Scope.Team => ownerId is { } t && a.TeamIds.Contains(t) ? WriteDecision.Publish : WriteDecision.Deny,
        Scope.Project => ownerId is { } p && a.ProjectIds.Contains(p) ? WriteDecision.Publish : WriteDecision.Deny,
        Scope.Company => a.IsKnowledgeOwner ? WriteDecision.Publish : WriteDecision.Propose,
        _ => WriteDecision.Deny,
    };

    /// <summary>The owner column that <see cref="CanWrite"/> checks for an existing memory.</summary>
    public static Guid? OwnerOf(Model.Memory m) => m.Scope switch
    {
        Scope.Personal => m.OwnerUserId,
        Scope.Team => m.OwnerTeamId,
        Scope.Project => m.OwnerProjectId,
        _ => null,
    };

    /// <summary>FR-MM-03: policies default to non-overridable.</summary>
    public static bool DefaultOverridable(MemoryType type) => type != MemoryType.Policy;

    /// <summary>FR-MM-03: only a project admin shadows, and only an overridable company memory.</summary>
    public static bool CanShadow(MemoryActor a, Guid projectId) => a.AdminProjectIds.Contains(projectId);

    /// <summary>FR-MM-04: proposals are reviewed by knowledge owners; a rebase may also come from the proposer.</summary>
    public static bool CanReview(MemoryActor a) => a.IsKnowledgeOwner;
    public static bool CanRebase(MemoryActor a, MemoryProposal p) => a.IsKnowledgeOwner || p.ProposedBy == a.UserId;

    /// <summary>
    /// FR-MM-06: an extraction's suggested scope is untrusted. It stands only where the owner could publish (personal, a
    /// team or project they belong to) or propose (company); otherwise the candidate falls back to personal.
    /// </summary>
    public static (Scope Scope, Guid? Owner) EligibleSuggestion(MemoryActor owner, Scope scope, Guid? ownerId) =>
        scope == Scope.Personal || CanWrite(owner, scope, ownerId) == WriteDecision.Deny
            ? (Scope.Personal, owner.UserId)
            : (scope, scope == Scope.Company ? null : ownerId);

    /// <summary>FR-MM-07: "the same content" means equal after search normalization, case and whitespace folding.</summary>
    public static string ContentKey(string text) =>
        string.Join(' ', TextNormalizer.Normalize(text).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>FR-CL-01: a derived item is as confidential as the strictest of its inputs.</summary>
    public static bool Strictest(params bool[] inputs) => inputs.Any(confidential => confidential);

    /// <summary>
    /// FR-CL-01: whoever can publish to a memory may classify it (for company memories, a knowledge owner). Declassifying
    /// also needs a stated reason, which the caller checks.
    /// </summary>
    public static bool CanClassify(MemoryActor a, Model.Memory m) => CanWrite(a, m.Scope, OwnerOf(m)) == WriteDecision.Publish;

    /// <summary>Memories the actor may read: own personal, member teams and projects, company. External agents never see confidential ones (FR-AG-07).</summary>
    public static Expression<Func<Model.Memory, bool>> Visible(MemoryActor a)
    {
        Guid[] teams = [.. a.TeamIds], projects = [.. a.ProjectIds];
        var (uid, external) = (a.UserId, a.ExternalAgent);
        return m => (m.Scope == Scope.Company
                || (m.Scope == Scope.Personal && m.OwnerUserId == uid)
                || (m.Scope == Scope.Team && teams.Contains(m.OwnerTeamId!.Value))
                || (m.Scope == Scope.Project && projects.Contains(m.OwnerProjectId!.Value)))
            && (!external || !m.IsConfidential);
    }

    /// <summary>The same rule over memory chunks (scope keys are denormalized onto chunks).</summary>
    public static Expression<Func<Chunk, bool>> VisibleChunk(MemoryActor a)
    {
        Guid[] teams = [.. a.TeamIds], projects = [.. a.ProjectIds];
        var (uid, external) = (a.UserId, a.ExternalAgent);
        return c => c.OwnerKind == ChunkOwner.Memory
            && (c.Scope == Scope.Company
                || (c.Scope == Scope.Personal && c.OwnerUserId == uid)
                || (c.Scope == Scope.Team && teams.Contains(c.OwnerTeamId!.Value))
                || (c.Scope == Scope.Project && projects.Contains(c.OwnerProjectId!.Value)))
            && (!external || !c.IsConfidential);
    }
}
