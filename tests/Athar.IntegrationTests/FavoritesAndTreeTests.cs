using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Web.Auth;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>Owner requests: drag and drop in the page tree, side-menu favorites, workspace and space badges.</summary>
[Collection(AtharCollection.Name)]
public class FavoritesAndTreeTests(AtharApp athar)
{
    private static readonly Guid Org = QaSeed.Org;
    private static readonly WikiActor Member = new(QaSeed.Member, Org);
    private static readonly WikiActor Guest = new(QaSeed.Guest, Org);

    private PageService Pages() => new(athar.Db(Org), TimeProvider.System);

    [Fact]
    public async Task Dropping_a_page_puts_it_inside_or_beside_another_and_refuses_cycles_and_viewers()
    {
        var ct = AtharApp.Timeout();
        var a = await Pages().CreateAsync(Member, QaSeed.EngWiki, null, "DnD A", ct);
        var b = await Pages().CreateAsync(Member, QaSeed.EngWiki, null, "DnD B", ct);
        var c = await Pages().CreateAsync(Member, QaSeed.EngWiki, null, "DnD C", ct);

        // Inside: C becomes A's child.
        await Pages().DropAsync(Member, c, a, DropPlace.Inside, ct);
        Assert.Equal(a, (await Node(c, ct)).ParentId);

        // After B: C comes out of A to the top level, right after B (before whatever followed B).
        await Pages().DropAsync(Member, c, b, DropPlace.After, ct);
        var top = (await Pages().TreeAsync(Member, QaSeed.EngWiki, ct: ct)).Where(p => p.ParentId == null).Select(p => p.Id).ToList();
        Assert.Null((await Node(c, ct)).ParentId);
        Assert.Equal(top.IndexOf(b) + 1, top.IndexOf(c));

        // Before A: same level, just ahead of it.
        await Pages().DropAsync(Member, c, a, DropPlace.Before, ct);
        top = (await Pages().TreeAsync(Member, QaSeed.EngWiki, ct: ct)).Where(p => p.ParentId == null).Select(p => p.Id).ToList();
        Assert.Equal(top.IndexOf(a) - 1, top.IndexOf(c));

        // Not into its own subtree; a viewer can't move pages.
        await Pages().DropAsync(Member, c, a, DropPlace.Inside, ct);
        var cycle = await Assert.ThrowsAsync<AtharError>(() => Pages().DropAsync(Member, a, c, DropPlace.Inside, ct));
        Assert.Equal("invalid_input", cycle.Code);
        await Assert.ThrowsAsync<AtharError>(() => Pages().DropAsync(Guest, b, a, DropPlace.Inside, ct));

        await using var db = athar.Db(Org);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.move" && e.ResourceId == c.ToString(), ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.reorder" && e.ResourceId == c.ToString(), ct));
    }

    [Fact]
    public async Task Favorites_list_only_what_the_person_can_see_and_toggle_off()
    {
        var ct = AtharApp.Timeout();
        var favorites = new FavoriteService(new Factory(athar));
        Assert.True(await favorites.ToggleAsync(Org, QaSeed.Member, "space", QaSeed.EngWiki, ct));
        Assert.True(await favorites.ToggleAsync(Org, QaSeed.Member, "workspace", QaSeed.Engineering, ct));

        var list = await favorites.ListAsync(Org, QaSeed.Member, ct);
        Assert.Contains(list, f => f.Kind == "space" && f.Id == QaSeed.EngWiki && f.Href == $"/wiki/s/{QaSeed.EngWiki}");
        Assert.Contains(list, f => f.Kind == "workspace" && f.Id == QaSeed.Engineering && f.Href == $"/wiki#ws-{QaSeed.Engineering}");

        // A confidential space the member has no role in doesn't exist for them, so it can't be pinned (no name leaks).
        var hidden = await Assert.ThrowsAsync<AtharError>(() => favorites.ToggleAsync(Org, QaSeed.Member, "space", QaSeed.EngConfidential, ct));
        Assert.Equal("not_found", hidden.Code);

        Assert.False(await favorites.ToggleAsync(Org, QaSeed.Member, "space", QaSeed.EngWiki, ct));
        Assert.DoesNotContain(await favorites.ListAsync(Org, QaSeed.Member, ct), f => f.Id == QaSeed.EngWiki);
        // Personal: nobody else sees them.
        Assert.DoesNotContain(await favorites.ListAsync(Org, QaSeed.Admin, ct), f => f.Id == QaSeed.Engineering);

        await using var db = athar.Db(Org);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "favorite.add" && e.ResourceId == QaSeed.EngWiki.ToString(), ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "favorite.remove" && e.ResourceId == QaSeed.EngWiki.ToString(), ct));
        await favorites.ToggleAsync(Org, QaSeed.Member, "workspace", QaSeed.Engineering, ct); // leave the seed as it was
    }

    [Fact]
    public async Task Space_and_workspace_badges_take_a_palette_color_and_icon_from_whoever_manages_them()
    {
        var ct = AtharApp.Timeout();
        var spaces = new WorkspaceAdminService(new Factory(athar));
        Assert.Equal(OrgDecision.Allow, await spaces.SetSpaceAppearanceAsync(Org, QaSeed.Admin, QaSeed.EngWiki, "teal", "code", ct));
        Assert.Equal(OrgDecision.Invalid, await spaces.SetSpaceAppearanceAsync(Org, QaSeed.Admin, QaSeed.EngWiki, "#ff0000", null, ct));
        Assert.Equal(OrgDecision.Forbidden, await spaces.SetSpaceAppearanceAsync(Org, QaSeed.Guest, QaSeed.EngWiki, "red", null, ct));
        Assert.Equal(OrgDecision.Allow, await spaces.SetWorkspaceAppearanceAsync(Org, QaSeed.Admin, QaSeed.Engineering, "indigo", null, ct));
        Assert.Equal(OrgDecision.Forbidden, await spaces.SetWorkspaceAppearanceAsync(Org, QaSeed.Member, QaSeed.Engineering, "red", null, ct));

        var ws = (await spaces.WorkspacesAsync(Org, QaSeed.Admin, ct)).Single(w => w.Id == QaSeed.Engineering);
        Assert.Equal(("indigo", (string?)null), (ws.Color, ws.Icon));
        Assert.Contains(ws.Spaces, s => s.Id == QaSeed.EngWiki && s.Color == "teal" && s.Icon == "code");

        await using var db = athar.Db(Org);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "space.appearance" && e.ResourceId == QaSeed.EngWiki.ToString(), ct));
        // Back to the defaults (derived color, initials) for the other tests.
        await spaces.SetSpaceAppearanceAsync(Org, QaSeed.Admin, QaSeed.EngWiki, null, null, ct);
        await spaces.SetWorkspaceAppearanceAsync(Org, QaSeed.Admin, QaSeed.Engineering, null, null, ct);
    }

    [Fact]
    public async Task Space_managers_rename_a_space_from_the_wiki_dialog()
    {
        var ct = AtharApp.Timeout();
        var spaces = new WorkspaceAdminService(new Factory(athar));
        Assert.True(await spaces.CanManageSpaceAsync(Org, QaSeed.Admin, QaSeed.EngWiki, ct));
        Assert.False(await spaces.CanManageSpaceAsync(Org, QaSeed.Guest, QaSeed.EngWiki, ct));

        Assert.Equal(OrgDecision.Forbidden, await spaces.RenameSpaceAsync(Org, QaSeed.Guest, QaSeed.EngWiki, "Nope", ct));
        Assert.Equal(OrgDecision.Invalid, await spaces.RenameSpaceAsync(Org, QaSeed.Admin, QaSeed.EngWiki, "   ", ct));
        Assert.Equal(OrgDecision.Allow, await spaces.RenameSpaceAsync(Org, QaSeed.Admin, QaSeed.EngWiki, " Engineering handbook ", ct));
        await using (var db = athar.Db(Org))
        {
            var space = await db.Spaces.SingleAsync(s => s.Id == QaSeed.EngWiki, ct);
            Assert.Equal(("Engineering handbook", "wiki"), (space.Name, space.Slug)); // the slug stays, so links keep working
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "space.rename" && e.ResourceId == QaSeed.EngWiki.ToString(), ct));
        }
        await spaces.RenameSpaceAsync(Org, QaSeed.Admin, QaSeed.EngWiki, "Engineering wiki", ct); // back, for the other tests
    }

    [Theory]
    [InlineData("Core Dev", "CD")]
    [InlineData("DevOps", "DE")]
    [InlineData("RVP", "RV")]
    [InlineData("People (confidential)", "PC")]
    [InlineData("دليل العمليات", "دا")]
    [InlineData("(x)", "X")]
    public void Badge_initials_take_letters_only(string name, string initials) =>
        Assert.Equal(initials, Athar.Domain.Wiki.Appearance.Initials(name));

    private async Task<PageNode> Node(Guid id, CancellationToken ct) =>
        (await Pages().TreeAsync(Member, QaSeed.EngWiki, ct: ct)).Single(p => p.Id == id);
}
