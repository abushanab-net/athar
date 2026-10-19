using Athar.Domain.Model;

namespace Athar.Domain.Access;

/// <summary>
/// Workspace and space administration (FR-WS-01..03, docs/03 §5 "Manage" column). Pure rules over the caller's current
/// org role, workspace role and space role (null = none), loaded by the caller inside its transaction.
/// Org owners and admins manage workspaces without being members, but seeing a workspace's spaces still needs membership.
/// </summary>
public static class WorkspaceRules
{
    /// <summary>Org admins create workspaces (FR-WS-01).</summary>
    public static OrgDecision CreateWorkspace(OrgRole? org) => OrgRules.IsAdmin(org) ? OrgDecision.Allow : OrgDecision.Forbidden;

    /// <summary>Members, spaces: workspace admins and org admins.</summary>
    public static OrgDecision ManageWorkspace(OrgRole? org, WorkspaceRole? workspace) =>
        workspace == WorkspaceRole.Admin || OrgRules.IsAdmin(org) ? OrgDecision.Allow : OrgDecision.Forbidden;

    /// <summary>Roles, AI policy and classification: space admins who are still workspace members, plus whoever manages the workspace.</summary>
    public static OrgDecision ManageSpace(OrgRole? org, WorkspaceRole? workspace, SpaceRole? space) =>
        workspace is not null && space == SpaceRole.Admin ? OrgDecision.Allow : ManageWorkspace(org, workspace);

    /// <summary>FR-CL-01: declassifying a space needs a space manager and a stated reason. Classifying needs only the manager.</summary>
    public static OrgDecision SetConfidential(OrgDecision manage, bool confidential, string? reason) =>
        manage != OrgDecision.Allow ? manage
        : !confidential && string.IsNullOrWhiteSpace(reason) ? OrgDecision.Invalid
        : OrgDecision.Allow;
}
