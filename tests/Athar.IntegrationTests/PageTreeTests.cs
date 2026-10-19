using Athar.Data.Seed;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>AT-14 (FR-PG-01): page tree rules through <see cref="PageService"/> on real Postgres.</summary>
[Collection(AtharCollection.Name)]
public class PageTreeTests(AtharApp athar)
{
    private static readonly WikiActor Admin = new(QaSeed.Admin, QaSeed.Org);
    private static readonly WikiActor Guest = new(QaSeed.Guest, QaSeed.Org);

    /// <summary>A clock the test moves.</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private PageService Pages(Clock? clock = null) => new(athar.Db(QaSeed.Org), clock ?? (TimeProvider)TimeProvider.System);

    [Fact]
    public async Task AT_14_move_into_own_descendant_is_rejected()
    {
        var ct = AtharApp.Timeout();
        var a = await Pages().CreateAsync(Admin, QaSeed.EngWiki, null, "AT-14 A", ct);
        var b = await Pages().CreateAsync(Admin, QaSeed.EngWiki, a, "AT-14 B", ct);
        var c = await Pages().CreateAsync(Admin, QaSeed.EngWiki, b, "AT-14 C", ct);

        foreach (var target in new[] { a, c })
        {
            var e = await Assert.ThrowsAsync<AtharError>(() => Pages().MoveAsync(Admin, a, QaSeed.EngWiki, target, ct));
            Assert.Equal("invalid_input", e.Code);
        }

        await Pages().MoveAsync(Admin, c, QaSeed.EngWiki, null, ct); // a legal move still works
        var tree = await Pages().TreeAsync(Admin, QaSeed.EngWiki, ct: ct);
        Assert.Null(tree.Single(p => p.Id == c).ParentId);

        await using var db = athar.Db(QaSeed.Org);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.move" && e.ResourceId == c.ToString() && e.ActorUserId == QaSeed.Admin, ct));
    }

    [Fact]
    public async Task AT_14_move_across_spaces_carries_the_subtree_within_the_workspace_only()
    {
        var ct = AtharApp.Timeout();
        var a = await Pages().CreateAsync(Admin, QaSeed.EngWiki, null, "AT-14 cross A", ct);
        var b = await Pages().CreateAsync(Admin, QaSeed.EngWiki, a, "AT-14 cross B", ct);

        await Pages().MoveAsync(Admin, a, QaSeed.EngConfidential, null, ct);
        var moved = await Pages().TreeAsync(Admin, QaSeed.EngConfidential, ct: ct);
        Assert.Contains(moved, p => p.Id == a && p.ParentId == null);
        Assert.Contains(moved, p => p.Id == b && p.ParentId == a);

        // Operations is another workspace.
        var e = await Assert.ThrowsAsync<AtharError>(() => Pages().MoveAsync(Admin, a, QaSeed.OpsHandbook, null, ct));
        Assert.Equal("invalid_input", e.Code);
    }

    [Fact]
    public async Task AT_14_trash_and_restore_within_30_days_then_purge()
    {
        var ct = AtharApp.Timeout();
        var clock = new Clock();
        var a = await Pages(clock).CreateAsync(Admin, QaSeed.EngWiki, null, "AT-14 trash A", ct);
        var b = await Pages(clock).CreateAsync(Admin, QaSeed.EngWiki, a, "AT-14 trash B", ct);

        // Trash takes the subtree; restore brings it back.
        await Pages(clock).TrashAsync(Admin, a, ct);
        var live = await Pages(clock).TreeAsync(Admin, QaSeed.EngWiki, ct: ct);
        Assert.DoesNotContain(live, p => p.Id == a || p.Id == b);
        Assert.Equal(2, (await Pages(clock).TreeAsync(Admin, QaSeed.EngWiki, trash: true, ct: ct)).Count(p => p.Id == a || p.Id == b));

        clock.Now += TimeSpan.FromDays(29);
        Assert.Equal(0, await PurgeMine(clock, a, ct));
        await Pages(clock).RestoreAsync(Admin, a, ct);
        live = await Pages(clock).TreeAsync(Admin, QaSeed.EngWiki, ct: ct);
        Assert.Contains(live, p => p.Id == a);
        Assert.Contains(live, p => p.Id == b && p.ParentId == a);

        // After 30 days in the trash the subtree is purged and can't be restored.
        await Pages(clock).TrashAsync(Admin, a, ct);
        clock.Now += TimeSpan.FromDays(31);
        Assert.Equal(2, await PurgeMine(clock, a, ct));
        var gone = await Assert.ThrowsAsync<AtharError>(() => Pages(clock).RestoreAsync(Admin, a, ct));
        Assert.Equal("not_found", gone.Code);

        await using var db = athar.Db(QaSeed.Org);
        Assert.False(await db.Pages.AnyAsync(p => p.Id == a || p.Id == b, ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.trash" && e.ResourceId == a.ToString(), ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.restore" && e.ResourceId == a.ToString(), ct));
    }

    [Fact]
    public async Task AT_14_a_viewer_cannot_change_the_tree()
    {
        var ct = AtharApp.Timeout();
        var e = await Assert.ThrowsAsync<AtharError>(() => Pages().CreateAsync(Guest, QaSeed.EngWiki, null, "nope", ct));
        Assert.Equal("forbidden", e.Code);
        // Not a member of the confidential space at all: it doesn't exist for them.
        e = await Assert.ThrowsAsync<AtharError>(() => Pages().TreeAsync(Guest, QaSeed.EngConfidential, ct: ct));
        Assert.Equal("not_found", e.Code);
    }

    /// <summary>The tree follows Athar.Domain.Access (T012): restrictions hide and protect pages, page rights gate changes.</summary>
    [Fact]
    public async Task Page_tree_follows_restrictions_and_page_rights()
    {
        var ct = AtharApp.Timeout();
        WikiActor Lab(Guid user) => new(user, QaSeed.Org);

        // A viewer the restriction doesn't admit sees neither the restricted page nor its child.
        var viewerTree = await Pages().TreeAsync(Lab(QaSeed.LabViewer), QaSeed.AccessLab, ct: ct);
        Assert.Contains(viewerTree, p => p.Id == QaSeed.LabRoot);
        Assert.DoesNotContain(viewerTree, p => p.Id == QaSeed.LabRestricted || p.Id == QaSeed.LabRestrictedChild);

        // A space editor admitted only at view level can read it but not change it, or add under it.
        Assert.Contains(await Pages().TreeAsync(Lab(QaSeed.LabEditor), QaSeed.AccessLab, ct: ct), p => p.Id == QaSeed.LabRestricted);
        var e = await Assert.ThrowsAsync<AtharError>(() => Pages().RenameAsync(Lab(QaSeed.LabEditor), QaSeed.LabRestricted, "x", ct));
        Assert.Equal("forbidden", e.Code);
        e = await Assert.ThrowsAsync<AtharError>(() => Pages().CreateAsync(Lab(QaSeed.LabEditor), QaSeed.AccessLab, QaSeed.LabRestricted, "x", ct));
        Assert.Equal("forbidden", e.Code);
        // The viewer can't tell it exists.
        e = await Assert.ThrowsAsync<AtharError>(() => Pages().RenameAsync(Lab(QaSeed.LabViewer), QaSeed.LabRestricted, "x", ct));
        Assert.Equal("not_found", e.Code);

        // The editors team has the edit-level restriction: its member may change it.
        await Pages().RenameAsync(Lab(QaSeed.LabTeamEditor), QaSeed.LabRestricted, "Lab restricted (renamed)", ct);
        await Pages().RenameAsync(Lab(QaSeed.LabTeamEditor), QaSeed.LabRestricted, "Lab restricted", ct);

        // Not a workspace member: the space doesn't exist for them.
        e = await Assert.ThrowsAsync<AtharError>(() => Pages().TreeAsync(Lab(QaSeed.LabOutsider), QaSeed.AccessLab, ct: ct));
        Assert.Equal("not_found", e.Code);
    }

    [Fact]
    public async Task Cross_space_move_rebuilds_ancestry()
    {
        var ct = AtharApp.Timeout();
        var a = await Pages().CreateAsync(Admin, QaSeed.EngWiki, null, "Ancestry A", ct);
        var b = await Pages().CreateAsync(Admin, QaSeed.EngWiki, a, "Ancestry B", ct);
        var c = await Pages().CreateAsync(Admin, QaSeed.EngWiki, b, "Ancestry C", ct);

        await Pages().MoveAsync(Admin, b, QaSeed.EngConfidential, null, ct);

        await using var db = athar.Db(QaSeed.Org);
        Assert.Empty((await db.Pages.SingleAsync(p => p.Id == b, ct)).AncestorIds);
        Assert.Equal([b], (await db.Pages.SingleAsync(p => p.Id == c, ct)).AncestorIds);
    }

    /// <summary>Purges the org, and returns how many of this test's pages went (other tests' trash may expire too).</summary>
    private async Task<int> PurgeMine(Clock clock, Guid root, CancellationToken ct)
    {
        await using var db = athar.Db(QaSeed.Org);
        var mine = await db.Pages.Where(p => p.Id == root || p.ParentId == root).Select(p => p.Id).ToListAsync(ct);
        await new PageService(athar.Db(QaSeed.Org), clock).PurgeAsync(QaSeed.Org, ct);
        await using var after = athar.Db(QaSeed.Org);
        return mine.Count - await after.Pages.CountAsync(p => mine.Contains(p.Id), ct);
    }
}
