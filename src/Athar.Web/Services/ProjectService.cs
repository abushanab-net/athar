using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record ResolvedProject(Guid ProjectId, string Name, string Role);
/// <summary>MCP structured content must be an object, so the §6.2 list is wrapped.</summary>
public sealed record ResolvedProjects(IReadOnlyList<ResolvedProject> Projects);
public sealed record ProjectView(Guid Id, string Name, string Slug, string? Role, IReadOnlyList<string> RepoRemotes);
public sealed record ProjectLinkInput(string? RepoRemote, Guid? SpaceId, Guid? TeamId);
public sealed record ProjectMemberView(Guid UserId, string Name, string Role);
public sealed record ProjectDetail(ProjectView Project, IReadOnlyList<ProjectMemberView> Members, bool CanManage);

/// <summary>Projects, members and links (FR-PJ-01..02). Each write is one SaveChanges with its audit row.</summary>
public sealed class ProjectService(AtharDbContext db)
{
    /// <summary><c>resolve_project</c>: projects linked to the remote that the caller is a member of, never others (FR-PJ-02).</summary>
    public async Task<IReadOnlyList<ResolvedProject>> ResolveAsync(Actor actor, string repoRemote, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var ids = await ResolveIdsAsync(actor.UserId, repoRemote, ct);
        return await (from p in db.Projects
                      join m in db.ProjectMembers on p.Id equals m.ProjectId
                      where ids.Contains(p.Id) && m.UserId == actor.UserId
                      orderby p.Name
                      select new ResolvedProject(p.Id, p.Name, m.Role == ProjectRole.Admin ? "admin" : "member")).ToListAsync(ct);
    }

    /// <summary>Member projects linked to <paramref name="repoRemote"/>; also narrows recall (architecture §5 recall step 1).</summary>
    internal async Task<Guid[]> ResolveIdsAsync(Guid userId, string repoRemote, CancellationToken ct)
    {
        var remote = ProjectRules.NormalizeRemote(repoRemote) ?? throw new AtharError("invalid_input", "repo_remote isn't a repository remote");
        return await db.ProjectLinks.Where(l => l.RepoRemote == remote)
            .Where(l => db.ProjectMembers.Any(m => m.ProjectId == l.ProjectId && m.UserId == userId))
            .Select(l => l.ProjectId).Distinct().ToArrayAsync(ct);
    }

    /// <summary>The caller's projects; <paramref name="allForManagers"/> adds every project for org owners and admins (the portal's list, T151).</summary>
    public async Task<IReadOnlyList<ProjectView>> ListAsync(Actor actor, CancellationToken ct, bool allForManagers = false)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var all = allForManagers && ProjectRules.CanCreate(await OrgRoleOf(actor.UserId, ct));
        var rows = await (from p in db.Projects
                          join m in db.ProjectMembers.Where(m => m.UserId == actor.UserId) on p.Id equals m.ProjectId into mj
                          from m in mj.DefaultIfEmpty()
                          where all || m != null
                          orderby p.Name
                          select new { p, Role = m == null ? (ProjectRole?)null : m.Role }).ToListAsync(ct);
        var ids = rows.Select(r => r.p.Id).ToArray();
        var remotes = (await db.ProjectLinks.Where(l => ids.Contains(l.ProjectId) && l.RepoRemote != null).ToListAsync(ct))
            .ToLookup(l => l.ProjectId, l => l.RepoRemote!);
        return [.. rows.Select(r => new ProjectView(r.p.Id, r.p.Name, r.p.Slug, RoleName(r.Role), [.. remotes[r.p.Id]]))];
    }

    /// <summary>One project with its members, for its members and for org owners and admins (T151).</summary>
    public async Task<ProjectDetail> GetAsync(Actor actor, Guid projectId, CancellationToken ct)
    {
        Require(actor, "read");
        db.CurrentOrgId = actor.OrgId;
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct);
        var mine = await db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == actor.UserId).Select(m => (ProjectRole?)m.Role).FirstOrDefaultAsync(ct);
        var orgRole = await OrgRoleOf(actor.UserId, ct);
        // Non-members who can't manage it don't learn the project exists.
        if (p is null || (mine is null && !ProjectRules.CanCreate(orgRole))) throw new AtharError("not_found", "No such project.");
        var members = await (from m in db.ProjectMembers
                             where m.ProjectId == projectId
                             join u in db.Users on m.UserId equals u.Id
                             orderby u.DisplayName
                             select new ProjectMemberView(u.Id, u.DisplayName, m.Role == ProjectRole.Admin ? "admin" : "member")).ToListAsync(ct);
        var remotes = await db.ProjectLinks.Where(l => l.ProjectId == projectId && l.RepoRemote != null).OrderBy(l => l.RepoRemote).Select(l => l.RepoRemote!).ToListAsync(ct);
        return new(new(p.Id, p.Name, p.Slug, RoleName(mine), remotes), members, ProjectRules.CanManage(orgRole, mine));
    }

    private static string? RoleName(ProjectRole? r) => r switch { ProjectRole.Admin => "admin", ProjectRole.Member => "member", _ => null };

    /// <summary>Org owners and admins create projects; the creator becomes its first admin.</summary>
    public async Task<ProjectView> CreateAsync(Actor actor, string name, string slug, CancellationToken ct)
    {
        Require(actor, "write");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug)) throw new AtharError("invalid_input", "name and slug are required");
        db.CurrentOrgId = actor.OrgId;
        if (!ProjectRules.CanCreate(await OrgRoleOf(actor.UserId, ct))) throw new AtharError("forbidden", "Only org owners and admins create projects.");
        if (await db.Projects.AnyAsync(p => p.Slug == slug, ct)) throw new AtharError("conflict_revision", $"A project with slug '{slug}' exists.");

        var p = new Project { Name = name.Trim(), Slug = slug.Trim().ToLowerInvariant() };
        db.Projects.Add(p);
        db.ProjectMembers.Add(new ProjectMember { ProjectId = p.Id, UserId = actor.UserId, Role = ProjectRole.Admin });
        Audit(actor, "project.create", p.Id, new { p.Name, p.Slug });
        await db.SaveChangesAsync(ct);
        return new(p.Id, p.Name, p.Slug, "admin", []);
    }

    /// <summary>Adds an org member to the project, or changes their role.</summary>
    public async Task SetMemberAsync(Actor actor, Guid projectId, Guid userId, string role, CancellationToken ct)
    {
        var r = role switch { "admin" => ProjectRole.Admin, "member" => ProjectRole.Member, _ => throw new AtharError("invalid_input", "role must be admin or member") };
        await RequireManager(actor, projectId, ct);
        if (!await db.OrgMembers.AnyAsync(m => m.UserId == userId, ct)) throw new AtharError("not_found", "No such user.");

        var existing = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (existing is null) db.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = userId, Role = r });
        else
        {
            if (existing.Role == ProjectRole.Admin && r != ProjectRole.Admin) await KeepAnAdmin(projectId, ct);
            existing.Role = r;
        }
        Audit(actor, "project.member_set", projectId, new { user_id = userId, role });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveMemberAsync(Actor actor, Guid projectId, Guid userId, CancellationToken ct)
    {
        await RequireManager(actor, projectId, ct);
        var m = await db.ProjectMembers.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.UserId == userId, ct)
            ?? throw new AtharError("not_found", "Not a member.");
        if (m.Role == ProjectRole.Admin) await KeepAnAdmin(projectId, ct);
        db.ProjectMembers.Remove(m);
        Audit(actor, "project.member_remove", projectId, new { user_id = userId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Links a repo remote (normalized), a space or a team. Exactly one target.</summary>
    public async Task LinkAsync(Actor actor, Guid projectId, ProjectLinkInput input, CancellationToken ct)
    {
        await RequireManager(actor, projectId, ct);
        var remote = input.RepoRemote is null ? null
            : ProjectRules.NormalizeRemote(input.RepoRemote) ?? throw new AtharError("invalid_input", "repo_remote isn't a repository remote");
        if (new object?[] { remote, input.SpaceId, input.TeamId }.Count(x => x is not null) != 1)
            throw new AtharError("invalid_input", "Give exactly one of repo_remote, space_id or team_id.");
        if (input.SpaceId is { } s && !await db.Spaces.AnyAsync(x => x.Id == s, ct)) throw new AtharError("not_found", "No such space.");
        if (input.TeamId is { } t && !await db.Teams.AnyAsync(x => x.Id == t, ct)) throw new AtharError("not_found", "No such team.");

        if (!await db.ProjectLinks.AnyAsync(l => l.ProjectId == projectId && l.RepoRemote == remote && l.SpaceId == input.SpaceId && l.TeamId == input.TeamId, ct))
        {
            db.ProjectLinks.Add(new ProjectLink { ProjectId = projectId, RepoRemote = remote, SpaceId = input.SpaceId, TeamId = input.TeamId });
            Audit(actor, "project.link", projectId, new { repo_remote = remote, space_id = input.SpaceId, team_id = input.TeamId });
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task RequireManager(Actor actor, Guid projectId, CancellationToken ct)
    {
        Require(actor, "write");
        db.CurrentOrgId = actor.OrgId;
        var projectRole = await db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == actor.UserId)
            .Select(m => (ProjectRole?)m.Role).FirstOrDefaultAsync(ct);
        var orgRole = await OrgRoleOf(actor.UserId, ct);
        // Non-members who can't manage it don't learn the project exists.
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct) || (projectRole is null && !ProjectRules.CanCreate(orgRole)))
            throw new AtharError("not_found", "No such project.");
        if (!ProjectRules.CanManage(orgRole, projectRole)) throw new AtharError("forbidden", "Only project admins manage the project.");
    }

    private async Task KeepAnAdmin(Guid projectId, CancellationToken ct)
    {
        if (await db.ProjectMembers.CountAsync(m => m.ProjectId == projectId && m.Role == ProjectRole.Admin, ct) <= 1)
            throw new AtharError("conflict_revision", "A project keeps at least one admin.");
    }

    private Task<OrgRole> OrgRoleOf(Guid userId, CancellationToken ct) =>
        db.OrgMembers.Where(m => m.UserId == userId).Select(m => m.Role).FirstAsync(ct);

    private void Audit(Actor a, string action, Guid projectId, object after) => db.AuditEvents.Add(new AuditEvent
    {
        ActorUserId = a.UserId, CredentialId = a.CredentialId, ClaimedClient = a.ClaimedClient,
        Action = action, ResourceType = "project", ResourceId = projectId.ToString(), After = JsonSerializer.Serialize(after),
    });

    private static void Require(Actor a, string scope)
    {
        if (!a.Scopes.Contains(scope)) throw new AtharError("forbidden", $"The token lacks the '{scope}' scope.");
    }
}
