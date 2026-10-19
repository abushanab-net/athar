using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T011 (M-A2): workspaces, spaces, memberships, AI policy and classification (FR-WS-01..03, FR-CL-01).</summary>
[Collection(AtharCollection.Name)]
public class WorkspaceTests(AtharApp athar)
{
    private WorkspaceAdminService Service => new(new Factory(athar));
    private static readonly Guid Org = QaSeed.Org;

    [Fact]
    public async Task WS_01_02_org_admins_create_workspaces_and_workspace_admins_create_spaces()
    {
        var ct = AtharApp.Timeout();
        var s = Service;
        Assert.Equal(OrgDecision.Forbidden, await s.CreateWorkspaceAsync(Org, QaSeed.Member, "Nope", "nope", ct));
        Assert.Equal(OrgDecision.Allow, await s.CreateWorkspaceAsync(Org, QaSeed.Admin, "Research", "research", ct));
        Assert.Equal(OrgDecision.Invalid, await s.CreateWorkspaceAsync(Org, QaSeed.Admin, "Research again", "research", ct));
        var research = (await s.WorkspacesAsync(Org, QaSeed.Admin, ct)).Single(w => w.Slug == "research");
        Assert.Equal(WorkspaceRole.Admin, research.MyRole);

        // A space that holds personal data is confidential from the start; the creator is its admin; AI is read only.
        Assert.Equal(OrgDecision.Allow, await s.CreateSpaceAsync(Org, QaSeed.Admin, research.Id, "HR records", "hr", holdsPersonalData: true, ct));
        Assert.Equal(OrgDecision.Allow, await s.CreateSpaceAsync(Org, QaSeed.Admin, research.Id, "Notes", "notes", holdsPersonalData: false, ct));
        var spaces = (await s.WorkspacesAsync(Org, QaSeed.Admin, ct)).Single(w => w.Id == research.Id).Spaces;
        var hr = spaces.Single(x => x.Slug == "hr");
        Assert.True(hr.IsConfidential);
        Assert.False(spaces.Single(x => x.Slug == "notes").IsConfidential);
        Assert.Equal(new AiPolicy(Read: true, Write: false, DeleteToTrash: false), hr.AiPolicy);

        // A plain workspace member can't create spaces.
        Assert.Equal(OrgDecision.Allow, await s.SetWorkspaceMemberAsync(Org, QaSeed.Admin, research.Id, QaSeed.Member, WorkspaceRole.Member, ct));
        Assert.Equal(OrgDecision.Forbidden, await s.CreateSpaceAsync(Org, QaSeed.Member, research.Id, "Mine", "mine", false, ct));

        // FR-WS-01: an org owner who isn't a member sees the workspace (to manage it) but not its spaces.
        var asOwner = (await s.WorkspacesAsync(Org, QaSeed.Owner, ct)).Single(w => w.Id == research.Id);
        Assert.Null(asOwner.MyRole);
        Assert.Empty(asOwner.Spaces);
        // A plain workspace member sees the same spaces as in the wiki: none until a space role or share opens one (T085: it listed HR).
        var asMember = await s.WorkspacesAsync(Org, QaSeed.Member, ct);
        Assert.Empty(asMember.Single(w => w.Id == research.Id).Spaces);
        var engineering = asMember.SelectMany(w => w.Spaces).ToList();
        Assert.Contains(engineering, x => x.Id == QaSeed.EngWiki && !x.CanManage);
        Assert.DoesNotContain(engineering, x => x.Id == QaSeed.EngConfidential || x.Id == QaSeed.AccessLab);

        await using var db = athar.Db(Org);
        Assert.True(await db.SpaceMembers.AnyAsync(m => m.SpaceId == hr.Id && m.UserId == QaSeed.Admin && m.Role == SpaceRole.Admin, ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "workspace.create" && e.ResourceId == research.Id.ToString(), ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "space.create" && e.ResourceId == hr.Id.ToString(), ct));
    }

    [Fact]
    public async Task WS_02_03_space_roles_ai_policy_and_declassification()
    {
        var ct = AtharApp.Timeout();
        var s = Service;
        Assert.Equal(OrgDecision.Allow, await s.CreateWorkspaceAsync(Org, QaSeed.Admin, "Legal", "legal", ct));
        var legal = (await s.WorkspacesAsync(Org, QaSeed.Admin, ct)).Single(w => w.Slug == "legal");
        Assert.Equal(OrgDecision.Allow, await s.CreateSpaceAsync(Org, QaSeed.Admin, legal.Id, "Contracts", "contracts", true, ct));
        var space = (await s.WorkspacesAsync(Org, QaSeed.Admin, ct)).Single(w => w.Id == legal.Id).Spaces.Single();
        Assert.Equal(OrgDecision.Allow, await s.SetWorkspaceMemberAsync(Org, QaSeed.Admin, legal.Id, QaSeed.Member, WorkspaceRole.Member, ct));

        // FR-WS-02: roles for users and teams.
        var platform = new WorkspaceAdminService.Principal(QaSeed.PlatformTeam, "Platform", IsTeam: true);
        var member = new WorkspaceAdminService.Principal(QaSeed.Member, "Qa Member", IsTeam: false);
        Assert.Equal(OrgDecision.Allow, await s.SetSpaceMemberAsync(Org, QaSeed.Admin, space.Id, platform, SpaceRole.Editor, ct));
        Assert.Equal(OrgDecision.Allow, await s.SetSpaceMemberAsync(Org, QaSeed.Admin, space.Id, member, SpaceRole.Viewer, ct));
        var grants = (await s.SpaceAsync(Org, QaSeed.Admin, space.Id, ct))!.Members;
        Assert.Contains(grants, g => g.Principal.Id == QaSeed.PlatformTeam && g.Principal.IsTeam && g.Role == "Editor");

        // Only space managers change the AI policy (FR-WS-03); a viewer can't, and sees nothing to manage.
        Assert.Null(await s.SpaceAsync(Org, QaSeed.Member, space.Id, ct));
        Assert.Equal(OrgDecision.Forbidden, await s.SetAiPolicyAsync(Org, QaSeed.Member, space.Id, new AiPolicy(true, true, true), ct));
        Assert.Equal(OrgDecision.Allow, await s.SetAiPolicyAsync(Org, QaSeed.Admin, space.Id, new AiPolicy(Read: true, Write: true, DeleteToTrash: false), ct));

        // FR-CL-01: declassifying needs a reason, and the reason is audited.
        Assert.Equal(OrgDecision.Invalid, await s.SetConfidentialAsync(Org, QaSeed.Admin, space.Id, false, "  ", ct));
        Assert.Equal(OrgDecision.Forbidden, await s.SetConfidentialAsync(Org, QaSeed.Member, space.Id, false, "because", ct));
        Assert.Equal(OrgDecision.Allow, await s.SetConfidentialAsync(Org, QaSeed.Admin, space.Id, false, "Contracts moved to the DMS; no personal data left", ct));

        await using var db = athar.Db(Org);
        var stored = await db.Spaces.SingleAsync(x => x.Id == space.Id, ct);
        Assert.Equal(new AiPolicy(true, true, false), stored.AiPolicy);
        Assert.False(stored.IsConfidential);
        Assert.Equal("""{"read": true, "write": true, "delete_to_trash": false}""", // jsonb's canonical text
            await db.Database.SqlQuery<string>($"SELECT ai_policy::text AS \"Value\" FROM spaces WHERE org_id = {Org} AND id = {space.Id}").SingleAsync(ct));
        var declassify = await db.AuditEvents.SingleAsync(e => e.Action == "space.declassify" && e.ResourceId == space.Id.ToString(), ct);
        Assert.Contains("Contracts moved to the DMS", declassify.After);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "space.ai_policy" && e.ResourceId == space.Id.ToString(), ct));
    }
}
