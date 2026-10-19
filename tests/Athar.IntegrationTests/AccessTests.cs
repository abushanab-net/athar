using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T012 (M-A3): Athar.Domain.Access on the qa access lab (QaSeed.Access.cs) and the whole qa permission matrix.</summary>
[Collection(AtharCollection.Name)]
public class AccessTests(AtharApp athar)
{
    private static readonly Guid Org = QaSeed.Org;

    private static async Task<(bool Read, bool Comment, bool Edit)> Decide(AtharDbContext db, Guid user, Guid page, CancellationToken ct, bool external = false)
    {
        var c = new Caller(user, external);
        return (await db.CanAsync(c, page, PageAction.Read, ct), await db.CanAsync(c, page, PageAction.Comment, ct), await db.CanAsync(c, page, PageAction.Edit, ct));
    }

    [Fact]
    public async Task AT_04_space_role_matrix_for_direct_users_and_teams()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(Org);
        var expected = new Dictionary<Guid, (bool, bool, bool)>
        {
            [QaSeed.LabViewer] = (true, false, false),
            [QaSeed.LabCommenter] = (true, true, false),
            [QaSeed.LabEditor] = (true, true, true),
            [QaSeed.LabAdmin] = (true, true, true),
            [QaSeed.LabTeamViewer] = (true, false, false), // through Lab viewers
            [QaSeed.LabTeamEditor] = (true, true, true),   // through Lab editors
            [QaSeed.LabOutsider] = (false, false, false),  // editor role, but not a workspace member
            [QaSeed.LabShareUser] = (false, false, false), // no role; its share is of another page
        };
        foreach (var (user, access) in expected)
        {
            Assert.Equal(access, await Decide(db, user, QaSeed.LabRoot, ct));
            // The list filter agrees.
            Assert.Equal(access.Item3, await db.PagesFor(new Caller(user), PageAction.Edit).AnyAsync(p => p.Id == QaSeed.LabRoot, ct));
            // Manage: only the space admin (all of them are plain org and workspace members).
            var manage = WorkspaceRules.ManageSpace(OrgRole.Member, WorkspaceRole.Member, await db.SpaceRoleAsync(user, QaSeed.AccessLab, ct));
            Assert.Equal(user == QaSeed.LabAdmin ? OrgDecision.Allow : OrgDecision.Forbidden, manage);
        }
    }

    [Fact]
    public async Task AT_05_a_restricted_ancestor_hides_the_subtree()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(Org);

        // Inherited from LabRestricted: LabEditor's view-level restriction allows read and comment but not edit.
        Assert.Equal((true, true, false), await Decide(db, QaSeed.LabEditor, QaSeed.LabRestrictedChild, ct));
        Assert.Equal((true, true, true), await Decide(db, QaSeed.LabTeamEditor, QaSeed.LabRestrictedChild, ct));
        foreach (var outside in new[] { QaSeed.LabViewer, QaSeed.LabCommenter, QaSeed.LabTeamViewer })
            Assert.Equal((false, false, false), await Decide(db, outside, QaSeed.LabRestrictedChild, ct));

        // The tree a viewer gets has no restricted pages.
        var tree = await db.PagesFor(new Caller(QaSeed.LabViewer)).Where(p => p.SpaceId == QaSeed.AccessLab).Select(p => p.Id).ToListAsync(ct);
        // Other tests may add pages to the lab, so check the seeded ones rather than the exact set.
        Assert.Subset(tree.ToHashSet(), new[] { QaSeed.LabRoot, QaSeed.LabShared, QaSeed.LabSharedChild }.ToHashSet());
        Assert.DoesNotContain(QaSeed.LabRestricted, tree);
        Assert.DoesNotContain(QaSeed.LabRestrictedChild, tree);

        // Moving a page under the restricted page hides it too: ancestry follows tracked moves, and back again.
        var page = await db.Pages.SingleAsync(p => p.Id == QaSeed.LabSharedChild, ct);
        page.ParentId = QaSeed.LabRestricted;
        await db.SaveChangesAsync(ct);
        Assert.Equal([QaSeed.LabRoot, QaSeed.LabRestricted], page.AncestorIds);
        Assert.False(await db.CanAsync(new Caller(QaSeed.LabViewer), QaSeed.LabSharedChild, PageAction.Read, ct));
        page.ParentId = QaSeed.LabShared;
        await db.SaveChangesAsync(ct);
        Assert.True(await db.CanAsync(new Caller(QaSeed.LabViewer), QaSeed.LabSharedChild, PageAction.Read, ct));
    }

    [Fact]
    public async Task AT_06_admin_outside_a_restriction_sees_a_placeholder_and_taking_ownership_is_audited()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(Org);
        var admin = new Caller(QaSeed.LabAdmin);

        Assert.False(await db.CanAsync(admin, QaSeed.LabRestrictedChild, PageAction.Read, ct));
        Assert.True(await db.SeesPlaceholderAsync(admin, QaSeed.LabRestrictedChild, ct));
        Assert.Equal(new[] { QaSeed.LabRestricted, QaSeed.LabRestrictedChild }.Order(),
            (await db.PlaceholdersFor(admin).Where(p => p.SpaceId == QaSeed.AccessLab).Select(p => p.Id).ToListAsync(ct)).Order());
        // Org owners see it too; members and external agents never do.
        Assert.True(await db.SeesPlaceholderAsync(new Caller(QaSeed.Owner), QaSeed.LabRestricted, ct));
        Assert.False(await db.SeesPlaceholderAsync(new Caller(QaSeed.LabViewer), QaSeed.LabRestricted, ct));
        Assert.False(await db.SeesPlaceholderAsync(new Caller(QaSeed.LabAdmin, External: true), QaSeed.LabRestricted, ct));

        var spaces = new WorkspaceAdminService(new Factory(athar));
        Assert.Equal(OrgDecision.Forbidden, await spaces.TakeOwnershipAsync(Org, QaSeed.LabViewer, QaSeed.LabRestrictedChild, ct));
        Assert.Equal(OrgDecision.Allow, await spaces.TakeOwnershipAsync(Org, QaSeed.LabAdmin, QaSeed.LabRestrictedChild, ct));

        Assert.Equal((true, true, true), await Decide(db, QaSeed.LabAdmin, QaSeed.LabRestrictedChild, ct));
        Assert.False(await db.SeesPlaceholderAsync(admin, QaSeed.LabRestrictedChild, ct));
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "page.take_ownership" && e.ActorUserId == QaSeed.LabAdmin, ct);
        Assert.Contains(QaSeed.LabRestricted.ToString(), audit.After);
    }

    [Fact]
    public async Task AT_49_a_direct_share_grants_that_page_only_and_needs_workspace_membership()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(Org);
        Assert.Equal((true, true, false), await Decide(db, QaSeed.LabShareUser, QaSeed.LabShared, ct));
        Assert.Equal((false, false, false), await Decide(db, QaSeed.LabShareUser, QaSeed.LabSharedChild, ct));
        Assert.Equal((false, false, false), await Decide(db, QaSeed.LabShareUser, QaSeed.LabRoot, ct));
        Assert.Equal((false, false, false), await Decide(db, QaSeed.LabOutsider, QaSeed.LabShared, ct));

        Assert.True(await db.SpacesFor(new Caller(QaSeed.LabShareUser)).AnyAsync(s => s.Id == QaSeed.AccessLab, ct));
        Assert.False(await db.SpacesFor(new Caller(QaSeed.LabOutsider)).AnyAsync(s => s.Id == QaSeed.AccessLab, ct));
    }

    [Fact]
    public async Task AT_54_decisions_and_query_filters_agree_on_the_whole_qa_matrix()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        await using var db = athar.Db(Org);

        // The whole org in memory: the same predicates, evaluated by LINQ to Objects.
        var memory = new AccessSource(
            (await db.Users.AsNoTracking().ToListAsync(ct)).AsQueryable(), (await db.OrgMembers.AsNoTracking().ToListAsync(ct)).AsQueryable(),
            (await db.TeamMembers.AsNoTracking().ToListAsync(ct)).AsQueryable(), (await db.WorkspaceMembers.AsNoTracking().ToListAsync(ct)).AsQueryable(),
            (await db.Spaces.AsNoTracking().ToListAsync(ct)).AsQueryable(), (await db.SpaceMembers.AsNoTracking().ToListAsync(ct)).AsQueryable(),
            (await db.Pages.AsNoTracking().ToListAsync(ct)).AsQueryable(), (await db.PageRestrictions.AsNoTracking().ToListAsync(ct)).AsQueryable(),
            (await db.PageShares.AsNoTracking().ToListAsync(ct)).AsQueryable());
        var pages = memory.Pages.ToList();
        var users = memory.Users.Select(u => u.Id).ToList();

        var allowed = 0;
        var checks = 0;
        foreach (var external in new[] { false, true })
            foreach (var user in users)
            {
                var caller = new Caller(user, external);
                var filters = new Dictionary<PageAction, HashSet<Guid>>();
                foreach (var action in Enum.GetValues<PageAction>())
                {
                    filters[action] = (await db.PagesFor(caller, action).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
                    var inMemory = pages.Where(p => PageAccess.Decide(memory, caller, action, p)).Select(p => p.Id);
                    Assert.True(filters[action].SetEquals(inMemory), $"{user} {action} external={external}: filter and in-memory predicate disagree");
                    allowed += filters[action].Count;
                }
                var placeholders = (await db.PlaceholdersFor(caller).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
                Assert.True(placeholders.SetEquals(pages.Where(p => PageAccess.DecidePlaceholder(memory, caller, p)).Select(p => p.Id)));

                // The decision method: facts loaded for one page, the same rules.
                foreach (var page in pages)
                {
                    var rights = await db.RightsAsync(caller, page.Id, ct);
                    var expected = new PageRights(filters[PageAction.Read].Contains(page.Id), filters[PageAction.Comment].Contains(page.Id),
                        filters[PageAction.Edit].Contains(page.Id), placeholders.Contains(page.Id));
                    Assert.True(expected == rights, $"{user} {page.Id} external={external}: decision {rights} but filters {expected}");
                    checks += 3;
                }
            }
        // A real matrix: some of everything allowed, most of it not.
        Assert.InRange(allowed, 1, checks - 1);
        // Disclosure (FR-CL-01): the confidential space's page is readable internally, never by an external agent.
        Assert.True(await db.CanAsync(new Caller(QaSeed.Owner), QaSeed.CompensationPage, PageAction.Read, ct));
        Assert.False(await db.CanAsync(new Caller(QaSeed.Owner, External: true), QaSeed.CompensationPage, PageAction.Read, ct));
    }
}
