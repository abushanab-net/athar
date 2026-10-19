using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Auth;

/// <summary>
/// Workspaces, spaces, memberships, AI policy and classification (FR-WS-01..03, FR-CL-01) for the admin pages.
/// Same shape as <see cref="OrgAdminService"/>: one unit of work per call, roles re-read inside the transaction,
/// the rule from <see cref="WorkspaceRules"/>, the change plus its audit event, then commit.
/// </summary>
public sealed class WorkspaceAdminService(IDbContextFactory<AtharDbContext> dbs)
{
    public sealed record SpaceRow(Guid Id, string Name, string Slug, bool IsConfidential, AiPolicy AiPolicy, bool CanManage = true, string? Color = null, string? Icon = null);
    public sealed record WorkspaceRow(Guid Id, string Name, string Slug, WorkspaceRole? MyRole, bool CanManage, IReadOnlyList<SpaceRow> Spaces, string? Color = null, string? Icon = null);
    public sealed record Principal(Guid Id, string Name, bool IsTeam);
    public sealed record Grant(Principal Principal, string Role);
    public sealed record SpaceDetail(SpaceRow Space, Guid WorkspaceId, IReadOnlyList<Grant> Members);

    /// <summary>
    /// Workspaces the caller belongs to; org admins also see the others so they can manage them. Spaces are listed only to
    /// workspace members (FR-WS-01): all of them to whoever manages the workspace, otherwise only those the access rules
    /// show the caller (a space role or a share), so a confidential space's name doesn't leak.
    /// </summary>
    public async Task<List<WorkspaceRow>> WorkspacesAsync(Guid orgId, Guid actorId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var orgRole = await OrgAdminService.ActorRoleAsync(db, actorId, ct);
        if (orgRole is null) return [];
        var mine = await db.WorkspaceMembers.Where(m => m.UserId == actorId).ToDictionaryAsync(m => m.WorkspaceId, m => m.Role, ct);
        var isAdmin = OrgRules.IsAdmin(orgRole);
        var workspaces = await db.Workspaces.Where(w => isAdmin || mine.Keys.Contains(w.Id)).OrderBy(w => w.Name).ToListAsync(ct);
        var spaces = await db.Spaces.Where(s => mine.Keys.Contains(s.WorkspaceId)).OrderBy(s => s.Name).ToListAsync(ct);
        var visible = (await db.SpacesFor(new Caller(actorId)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        var teams = db.TeamMembers.Where(t => t.UserId == actorId).Select(t => (Guid?)t.TeamId);
        var spaceAdmin = (await db.SpaceMembers.Where(m => m.Role == SpaceRole.Admin && (m.UserId == actorId || teams.Contains(m.TeamId)))
            .Select(m => m.SpaceId).ToListAsync(ct)).ToHashSet();
        return workspaces.Select(w =>
        {
            WorkspaceRole? role = mine.TryGetValue(w.Id, out var r) ? r : null;
            var manages = WorkspaceRules.ManageWorkspace(orgRole, role) == OrgDecision.Allow;
            return new WorkspaceRow(w.Id, w.Name, w.Slug, role, manages,
                spaces.Where(s => s.WorkspaceId == w.Id && (manages || visible.Contains(s.Id)))
                    .Select(s => Row(s) with { CanManage = manages || spaceAdmin.Contains(s.Id) }).ToList(), w.Color, w.Icon);
        }).ToList();
    }
    /// <summary>Active people and teams of the org, for the member pickers.</summary>
    public async Task<List<Principal>> DirectoryAsync(Guid orgId, Guid actorId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        if (await OrgAdminService.ActorRoleAsync(db, actorId, ct) is null) return [];
        var users = await db.Users.Where(u => u.DeactivatedAt == null && db.OrgMembers.Any(m => m.UserId == u.Id))
            .OrderBy(u => u.DisplayName).Select(u => new Principal(u.Id, u.DisplayName, false)).ToListAsync(ct);
        var teams = await db.Teams.OrderBy(t => t.Name).Select(t => new Principal(t.Id, t.Name, true)).ToListAsync(ct);
        return [.. users, .. teams];
    }

    public async Task<List<Grant>> WorkspaceMembersAsync(Guid orgId, Guid actorId, Guid workspaceId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var (orgRole, wsRole) = await RolesAsync(db, actorId, workspaceId, ct);
        if (WorkspaceRules.ManageWorkspace(orgRole, wsRole) != OrgDecision.Allow) return [];
        return await (from m in db.WorkspaceMembers
                      join u in db.Users on m.UserId equals u.Id
                      where m.WorkspaceId == workspaceId
                      orderby u.DisplayName
                      select new Grant(new Principal(u.Id, u.DisplayName, false), m.Role.ToString())).ToListAsync(ct);
    }

    public Task<OrgDecision> CreateWorkspaceAsync(Guid orgId, Guid actorId, string name, string slug, CancellationToken ct = default) =>
        InTxAsync(orgId, ct, async db =>
        {
            var decision = WorkspaceRules.CreateWorkspace(await OrgAdminService.ActorRoleAsync(db, actorId, ct));
            if (decision != OrgDecision.Allow) return decision;
            if (!Valid(name, ref slug) || await db.Workspaces.AnyAsync(w => w.Slug == slug, ct)) return OrgDecision.Invalid;
            var workspace = new Workspace { Name = name.Trim(), Slug = slug };
            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = actorId, Role = WorkspaceRole.Admin });
            db.Audit(orgId, actorId, "workspace.create", "workspace", workspace.Id, after: new { workspace.Name, workspace.Slug });
            return decision;
        });

    /// <summary>Adds, changes or (with a null role) removes a workspace member.</summary>
    public Task<OrgDecision> SetWorkspaceMemberAsync(Guid orgId, Guid actorId, Guid workspaceId, Guid userId, WorkspaceRole? role, CancellationToken ct = default) =>
        InTxAsync(orgId, ct, async db =>
        {
            var (orgRole, wsRole) = await RolesAsync(db, actorId, workspaceId, ct);
            var decision = WorkspaceRules.ManageWorkspace(orgRole, wsRole);
            if (decision != OrgDecision.Allow) return decision;
            var member = await db.WorkspaceMembers.SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);
            if (member?.Role == role) return decision;
            if (role is null) db.WorkspaceMembers.Remove(member!);
            else if (member is null) db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, Role = role.Value });
            else member.Role = role.Value;
            db.Audit(orgId, actorId, "workspace.member", "workspace", workspaceId, new { user = userId, role = member?.Role.ToString() }, new { user = userId, role = role?.ToString() });
            return decision;
        });

    /// <summary>
    /// FR-WS-02. The creator becomes space admin. A space that holds personal data is confidential (docs/03 §6):
    /// its content is never returned to external-agent credentials (FR-CL-01).
    /// </summary>
    public Task<OrgDecision> CreateSpaceAsync(Guid orgId, Guid actorId, Guid workspaceId, string name, string slug, bool holdsPersonalData, CancellationToken ct = default) =>
        InTxAsync(orgId, ct, async db =>
        {
            var (orgRole, wsRole) = await RolesAsync(db, actorId, workspaceId, ct);
            var decision = WorkspaceRules.ManageWorkspace(orgRole, wsRole);
            if (decision != OrgDecision.Allow) return decision;
            if (!Valid(name, ref slug) || await db.Spaces.AnyAsync(s => s.WorkspaceId == workspaceId && s.Slug == slug, ct)) return OrgDecision.Invalid;
            var space = new Space { WorkspaceId = workspaceId, Name = name.Trim(), Slug = slug, IsConfidential = holdsPersonalData };
            db.Spaces.Add(space);
            db.SpaceMembers.Add(new SpaceMember { SpaceId = space.Id, UserId = actorId, Role = SpaceRole.Admin });
            db.Audit(orgId, actorId, "space.create", "space", space.Id, after: new { space.Name, space.Slug, space.IsConfidential, workspace = workspaceId });
            return decision;
        });

    /// <summary>The space and its grants, for the people who manage it; null for anyone else.</summary>
    public async Task<SpaceDetail?> SpaceAsync(Guid orgId, Guid actorId, Guid spaceId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var space = await db.Spaces.SingleOrDefaultAsync(s => s.Id == spaceId, ct);
        if (space is null || await ManageSpaceAsync(db, actorId, space, ct) != OrgDecision.Allow) return null;
        var grants = await (from m in db.SpaceMembers
                            where m.SpaceId == spaceId
                            join u in db.Users on m.UserId equals (Guid?)u.Id into users
                            from u in users.DefaultIfEmpty()
                            join t in db.Teams on m.TeamId equals (Guid?)t.Id into teams
                            from t in teams.DefaultIfEmpty()
                            select new { m.UserId, m.TeamId, Name = u != null ? u.DisplayName : t!.Name, m.Role }).ToListAsync(ct);
        return new SpaceDetail(Row(space), space.WorkspaceId, grants
            .Select(g => new Grant(new Principal((g.UserId ?? g.TeamId)!.Value, g.Name, g.TeamId is not null), g.Role.ToString()))
            .OrderBy(g => g.Principal.IsTeam).ThenBy(g => g.Principal.Name).ToList());
    }

    /// <summary>Grants, changes or (with a null role) revokes a space role for a user or a team (FR-WS-02).</summary>
    public Task<OrgDecision> SetSpaceMemberAsync(Guid orgId, Guid actorId, Guid spaceId, Principal principal, SpaceRole? role, CancellationToken ct = default) =>
        InSpaceAsync(orgId, actorId, spaceId, ct, async (db, space) =>
        {
            var member = await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == spaceId
                && (principal.IsTeam ? m.TeamId == principal.Id : m.UserId == principal.Id), ct);
            if (member?.Role == role) return OrgDecision.Allow;
            if (role is null) db.SpaceMembers.Remove(member!);
            else if (member is null)
                db.SpaceMembers.Add(new SpaceMember
                {
                    SpaceId = spaceId, Role = role.Value,
                    UserId = principal.IsTeam ? null : principal.Id, TeamId = principal.IsTeam ? principal.Id : null,
                });
            else member.Role = role.Value;
            var who = principal.IsTeam ? "team" : "user";
            db.Audit(orgId, actorId, "space.member", "space", spaceId,
                new { principal = principal.Id, kind = who, role = member?.Role.ToString() }, new { principal = principal.Id, kind = who, role = role?.ToString() });
            return OrgDecision.Allow;
        });

    /// <summary>FR-WS-03: read, write and delete-to-trash, each independent.</summary>
    public Task<OrgDecision> SetAiPolicyAsync(Guid orgId, Guid actorId, Guid spaceId, AiPolicy policy, CancellationToken ct = default) =>
        InSpaceAsync(orgId, actorId, spaceId, ct, (db, space) =>
        {
            if (space.AiPolicy == policy) return Task.FromResult(OrgDecision.Allow);
            db.Audit(orgId, actorId, "space.ai_policy", "space", spaceId, space.AiPolicy, policy);
            space.AiPolicy = policy;
            return Task.FromResult(OrgDecision.Allow);
        });

    /// <summary>
    /// T166 (owner, 2026-10-15): deletes a space, for the workspace's managers (not space admins: it takes the space from
    /// everyone). Its live pages go to the trash together (purged after 30 days, which removes their search index too), the
    /// space disappears from every list (query filter), and its slug is freed so a new space or an import can take it.
    /// </summary>
    public Task<OrgDecision> DeleteSpaceAsync(Guid orgId, Guid actorId, Guid spaceId, CancellationToken ct = default) =>
        InTxAsync(orgId, ct, async db =>
        {
            var space = await db.Spaces.SingleOrDefaultAsync(s => s.Id == spaceId, ct);
            if (space is null) return OrgDecision.Invalid;
            var (orgRole, wsRole) = await RolesAsync(db, actorId, space.WorkspaceId, ct);
            var decision = WorkspaceRules.ManageWorkspace(orgRole, wsRole);
            if (decision != OrgDecision.Allow) return decision;
            var now = DateTimeOffset.UtcNow;
            var pages = await db.Pages.Where(p => p.SpaceId == spaceId && p.DeletedAt == null).ToListAsync(ct);
            foreach (var p in pages) p.DeletedAt = now;
            db.Audit(orgId, actorId, "space.delete", "space", spaceId, new { space.Name, space.Slug }, new { pages = pages.Count });
            space.DeletedAt = now;
            space.Slug = $"{space.Slug}-deleted-{now:yyyyMMddHHmmss}";
            return decision;
        });

    /// <summary>T114: renames a space (whoever manages it); the slug stays, so links keep working. Audited.</summary>
    public Task<OrgDecision> RenameSpaceAsync(Guid orgId, Guid actorId, Guid spaceId, string name, CancellationToken ct = default) =>
        InSpaceAsync(orgId, actorId, spaceId, ct, (db, space) =>
        {
            name = name.Trim();
            if (name.Length is 0 or > 200) return Task.FromResult(OrgDecision.Invalid);
            if (name == space.Name) return Task.FromResult(OrgDecision.Allow);
            db.Audit(orgId, actorId, "space.rename", "space", spaceId, new { space.Name }, new { name });
            space.Name = name;
            return Task.FromResult(OrgDecision.Allow);
        });

    /// <summary>Whether the actor may change the space's settings (the wiki shows Space settings only to them).</summary>
    public async Task<bool> CanManageSpaceAsync(Guid orgId, Guid actorId, Guid spaceId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var space = await db.Spaces.SingleOrDefaultAsync(s => s.Id == spaceId, ct);
        return space is not null && await ManageSpaceAsync(db, actorId, space, ct) == OrgDecision.Allow;
    }

    /// <summary>A space's menu badge: a color and an icon from <see cref="Appearance"/> (null: derived color, initials).</summary>
    public Task<OrgDecision> SetSpaceAppearanceAsync(Guid orgId, Guid actorId, Guid spaceId, string? color, string? icon, CancellationToken ct = default) =>
        InSpaceAsync(orgId, actorId, spaceId, ct, (db, space) =>
        {
            if (!Appearance.Valid(color, icon)) return Task.FromResult(OrgDecision.Invalid);
            if ((space.Color, space.Icon) == (color, icon)) return Task.FromResult(OrgDecision.Allow);
            db.Audit(orgId, actorId, "space.appearance", "space", spaceId, new { space.Color, space.Icon }, new { color, icon });
            (space.Color, space.Icon) = (color, icon);
            return Task.FromResult(OrgDecision.Allow);
        });

    /// <summary>A workspace's menu badge; whoever manages the workspace sets it.</summary>
    public Task<OrgDecision> SetWorkspaceAppearanceAsync(Guid orgId, Guid actorId, Guid workspaceId, string? color, string? icon, CancellationToken ct = default) =>
        InTxAsync(orgId, ct, async db =>
        {
            var (orgRole, wsRole) = await RolesAsync(db, actorId, workspaceId, ct);
            var decision = WorkspaceRules.ManageWorkspace(orgRole, wsRole);
            if (decision != OrgDecision.Allow) return decision;
            if (!Appearance.Valid(color, icon)) return OrgDecision.Invalid;
            var ws = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, ct);
            if ((ws.Color, ws.Icon) == (color, icon)) return OrgDecision.Allow;
            db.Audit(orgId, actorId, "workspace.appearance", "workspace", workspaceId, new { ws.Color, ws.Icon }, new { color, icon });
            (ws.Color, ws.Icon) = (color, icon);
            return OrgDecision.Allow;
        });

    /// <summary>FR-CL-01: declassifying needs a stated reason, which is audited.</summary>
    public Task<OrgDecision> SetConfidentialAsync(Guid orgId, Guid actorId, Guid spaceId, bool confidential, string? reason, CancellationToken ct = default) =>
        InTxAsync(orgId, ct, async db =>
        {
            var space = await db.Spaces.SingleOrDefaultAsync(s => s.Id == spaceId, ct);
            if (space is null) return OrgDecision.Forbidden;
            var decision = WorkspaceRules.SetConfidential(await ManageSpaceAsync(db, actorId, space, ct), confidential, reason);
            if (decision != OrgDecision.Allow || space.IsConfidential == confidential) return decision;
            db.Audit(orgId, actorId, confidential ? "space.classify" : "space.declassify", "space", spaceId,
                new { space.IsConfidential }, new { IsConfidential = confidential, reason = reason?.Trim() });
            space.IsConfidential = confidential;
            return decision;
        });

    /// <summary>
    /// AT-06: a space admin or org admin who sees a restricted page as a placeholder adds themselves (edit level) to every
    /// restriction on it and its ancestors that doesn't admit them yet. Audited: admins never see restricted pages silently.
    /// </summary>
    public Task<OrgDecision> TakeOwnershipAsync(Guid orgId, Guid actorId, Guid pageId, CancellationToken ct = default) =>
        InTxAsync(orgId, ct, async db =>
        {
            var caller = new Caller(actorId);
            if (!await db.SeesPlaceholderAsync(caller, pageId, ct)) return OrgDecision.Forbidden;
            var page = await db.Pages.SingleAsync(p => p.Id == pageId, ct);
            var blocking = await PageAccess.BlockingRestrictions(db.AccessSource(), caller, page).ToListAsync(ct);
            foreach (var restricted in blocking)
                db.PageRestrictions.Add(new PageRestriction { PageId = restricted, UserId = actorId, Level = RestrictionLevel.Edit });
            db.Audit(orgId, actorId, "page.take_ownership", "page", pageId, after: new { joined_restrictions_on = blocking });
            return OrgDecision.Allow;
        });

    private static SpaceRow Row(Space s) => new(s.Id, s.Name, s.Slug, s.IsConfidential, s.AiPolicy, Color: s.Color, Icon: s.Icon);

    // Lowercase letters, digits and dashes.
    private static bool Valid(string name, ref string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        return name.Trim().Length > 0 && slug.Length > 0 && slug.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    private async Task<AtharDbContext> OpenAsync(Guid orgId, CancellationToken ct)
    {
        var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = orgId;
        return db;
    }

    private async Task<OrgDecision> InTxAsync(Guid orgId, CancellationToken ct, Func<AtharDbContext, Task<OrgDecision>> change)
    {
        await using var db = await OpenAsync(orgId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var decision = await change(db);
        if (decision != OrgDecision.Allow) return decision;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return decision;
    }

    private Task<OrgDecision> InSpaceAsync(Guid orgId, Guid actorId, Guid spaceId, CancellationToken ct, Func<AtharDbContext, Space, Task<OrgDecision>> change) =>
        InTxAsync(orgId, ct, async db =>
        {
            var space = await db.Spaces.SingleOrDefaultAsync(s => s.Id == spaceId, ct);
            if (space is null) return OrgDecision.Forbidden;
            var decision = await ManageSpaceAsync(db, actorId, space, ct);
            return decision != OrgDecision.Allow ? decision : await change(db, space);
        });

    private static async Task<(OrgRole?, WorkspaceRole?)> RolesAsync(AtharDbContext db, Guid actorId, Guid workspaceId, CancellationToken ct)
    {
        var orgRole = await OrgAdminService.ActorRoleAsync(db, actorId, ct);
        var wsRole = orgRole is null ? null : await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId && m.UserId == actorId)
            .Select(m => (WorkspaceRole?)m.Role).SingleOrDefaultAsync(ct);
        return (orgRole, wsRole);
    }

    private static async Task<OrgDecision> ManageSpaceAsync(AtharDbContext db, Guid actorId, Space space, CancellationToken ct)
    {
        var (orgRole, wsRole) = await RolesAsync(db, actorId, space.WorkspaceId, ct);
        return WorkspaceRules.ManageSpace(orgRole, wsRole, await db.SpaceRoleAsync(actorId, space.Id, ct));
    }
}
