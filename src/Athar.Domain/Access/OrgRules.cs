using Athar.Domain.Model;

namespace Athar.Domain.Access;

/// <summary>The outcome of an admin action. Invalid: bad input, such as an empty name or a slug that's taken.</summary>
public enum OrgDecision { Allow, Forbidden, LastOwner, Invalid }

/// <summary>
/// Org administration (FR-ID-02..04). Pure rules: the caller loads the facts inside a transaction that holds the
/// organization row lock, so concurrent changes see each other (AT-03). <paramref name="actor"/> is the caller's
/// current role, or null when they aren't an active member.
/// </summary>
public static class OrgRules
{
    public static bool IsAdmin(OrgRole? role) => role is OrgRole.Owner or OrgRole.Admin;

    public static bool ManageAi(OrgRole? role, Guid? credentialId) => credentialId is null && IsAdmin(role);

    /// <summary>Admins assign roles, but only owners make or unmake owners. The last active owner stays an owner.</summary>
    public static OrgDecision ChangeRole(OrgRole? actor, OrgRole current, OrgRole next, int activeOwners) =>
        !IsAdmin(actor) || (current == OrgRole.Owner || next == OrgRole.Owner) && actor != OrgRole.Owner ? OrgDecision.Forbidden
        : current == OrgRole.Owner && next != OrgRole.Owner && activeOwners <= 1 ? OrgDecision.LastOwner
        : OrgDecision.Allow;

    /// <summary>Admins (and owners) set the knowledge-owner flag.</summary>
    public static OrgDecision SetKnowledgeOwner(OrgRole? actor) => IsAdmin(actor) ? OrgDecision.Allow : OrgDecision.Forbidden;

    /// <summary>Admins deactivate users; only owners deactivate owners, and never the last active one.</summary>
    public static OrgDecision Deactivate(OrgRole? actor, OrgRole target, int activeOwners) =>
        !IsAdmin(actor) || target == OrgRole.Owner && actor != OrgRole.Owner ? OrgDecision.Forbidden
        : target == OrgRole.Owner && activeOwners <= 1 ? OrgDecision.LastOwner
        : OrgDecision.Allow;

    public static OrgDecision Reactivate(OrgRole? actor, OrgRole target) =>
        !IsAdmin(actor) || target == OrgRole.Owner && actor != OrgRole.Owner ? OrgDecision.Forbidden : OrgDecision.Allow;

    /// <summary>Admins invite; only owners invite owners.</summary>
    public static OrgDecision Invite(OrgRole? actor, OrgRole role) =>
        !IsAdmin(actor) || role == OrgRole.Owner && actor != OrgRole.Owner ? OrgDecision.Forbidden : OrgDecision.Allow;

    /// <summary>FR-ID-06: only org owners issue or reclassify trusted-local credentials, and they state why.</summary>
    public static OrgDecision ClassifyToken(OrgRole? actor, string? reason) =>
        actor != OrgRole.Owner ? OrgDecision.Forbidden : string.IsNullOrWhiteSpace(reason) ? OrgDecision.Invalid : OrgDecision.Allow;

    /// <summary>Teams are managed by admins (FR-ID-04).</summary>
    public static OrgDecision ManageTeams(OrgRole? actor) => IsAdmin(actor) ? OrgDecision.Allow : OrgDecision.Forbidden;
}
