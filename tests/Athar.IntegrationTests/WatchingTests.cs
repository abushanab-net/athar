using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>
/// T118 (U-3): watching a page. Creating a page or commenting on it for the first time watches it; watchers hear about
/// edits (at most once per window), comments (new, replies, resolved) and the trash, never about their own actions, and
/// only while they can still read the page. LabViewer can't see the Engineering wiki.
/// </summary>
[Collection(AtharCollection.Name)]
public class WatchingTests(AtharApp athar)
{
    private static readonly HashSet<string> Scopes = ["read", "write"];
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, Scopes, DisclosureClass.TrustedLocal, null);
    private static string At(string name, Guid id) => $"@[{name}](user:{id})";

    private async Task<CommentService> Comments()
    {
        var db = athar.Db(QaSeed.Org);
        return new CommentService(db, await athar.OpsAsync(db: db), TimeProvider.System, new NotificationService(db));
    }

    private NotificationService Notifications() => new(athar.Db(QaSeed.Org));

    private async Task<Guid> NewPageAsync(Guid author, string title, Guid? parent = null) =>
        (await (await athar.OpsAsync()).CreatePageAsync(Person(author), QaSeed.EngWiki, parent, title, "Body.", Guid.NewGuid().ToString(), AtharApp.Timeout())).PageId;

    /// <summary>A watch row written directly, as if the person could read the page when they started watching.</summary>
    private async Task ForceWatchAsync(Guid user, Guid page)
    {
        await using var db = athar.Db(QaSeed.Org);
        db.PageWatches.Add(new PageWatch { OrgId = QaSeed.Org, PageId = page, UserId = user });
        await db.SaveChangesAsync(AtharApp.Timeout());
    }

    private async Task<List<string>> KindsAsync(Guid user, string reference)
    {
        await using var db = athar.Db(QaSeed.Org);
        return await db.Notifications.Where(n => n.UserId == user && n.Ref == reference).OrderBy(n => n.CreatedAt).Select(n => n.Kind).ToListAsync(AtharApp.Timeout());
    }

    [Fact]
    public async Task T118_creating_or_first_commenting_watches_a_page_and_unwatching_sticks()
    {
        var ct = AtharApp.Timeout();
        var page = await NewPageAsync(QaSeed.Admin, "T118 auto-watch");
        Assert.True(await Notifications().IsWatchingAsync(QaSeed.Org, QaSeed.Admin, page, ct));
        await using (var db = athar.Db(QaSeed.Org))
            Assert.Equal(QaSeed.Admin, (await db.Pages.SingleAsync(p => p.Id == page, ct)).CreatedBy);

        await (await Comments()).AddPageThreadAsync(Person(QaSeed.Member), page, "First comment", ct);
        Assert.True(await Notifications().IsWatchingAsync(QaSeed.Org, QaSeed.Member, page, ct));

        // Unwatching sticks: a later comment doesn't watch again.
        Assert.True(await Notifications().SetWatchAsync(QaSeed.Org, QaSeed.Member, page, false, ct));
        await (await Comments()).AddPageThreadAsync(Person(QaSeed.Member), page, "Second comment", ct);
        Assert.False(await Notifications().IsWatchingAsync(QaSeed.Org, QaSeed.Member, page, ct));

        // Watching needs read access; watching twice is fine.
        Assert.False(await Notifications().SetWatchAsync(QaSeed.Org, QaSeed.LabViewer, page, true, ct));
        Assert.False(await Notifications().IsWatchingAsync(QaSeed.Org, QaSeed.LabViewer, page, ct));
        Assert.True(await Notifications().SetWatchAsync(QaSeed.Org, QaSeed.KnowledgeOwner, page, true, ct));
        Assert.True(await Notifications().SetWatchAsync(QaSeed.Org, QaSeed.KnowledgeOwner, page, true, ct));
    }

    [Fact]
    public async Task T118_watchers_hear_about_comments_once_and_never_about_their_own()
    {
        var ct = AtharApp.Timeout();
        var page = await NewPageAsync(QaSeed.Admin, "T118 comments"); // Admin watches
        await Notifications().SetWatchAsync(QaSeed.Org, QaSeed.KnowledgeOwner, page, true, ct);
        await ForceWatchAsync(QaSeed.LabViewer, page); // can't read the page: must hear nothing

        var thread = await (await Comments()).AddPageThreadAsync(Person(QaSeed.Member), page, "A question", ct);
        var reference = $"thread:{thread}";
        Assert.Equal(["watch_comment"], await KindsAsync(QaSeed.Admin, reference));
        Assert.Equal(["watch_comment"], await KindsAsync(QaSeed.KnowledgeOwner, reference));
        Assert.Empty(await KindsAsync(QaSeed.Member, reference)); // their own comment

        // A reply that mentions Admin: Admin gets the mention, Member (the thread's author, now watching) the reply,
        // and nobody gets a second notification for the same reply.
        await (await Comments()).ReplyAsync(Person(QaSeed.KnowledgeOwner), thread, $"{At("Qa Admin", QaSeed.Admin)} can you check?", ct);
        Assert.Equal(["watch_comment", "mention"], await KindsAsync(QaSeed.Admin, reference));
        Assert.Equal(["reply"], await KindsAsync(QaSeed.Member, reference));
        Assert.Equal(["watch_comment"], await KindsAsync(QaSeed.KnowledgeOwner, reference)); // their own reply

        await (await Comments()).ResolveAsync(Person(QaSeed.Member), thread, ct);
        Assert.Equal(["watch_comment", "mention", "watch_resolved"], await KindsAsync(QaSeed.Admin, reference));
        Assert.Equal(["watch_comment", "watch_resolved"], await KindsAsync(QaSeed.KnowledgeOwner, reference));
        Assert.Equal(["reply"], await KindsAsync(QaSeed.Member, reference)); // they resolved it

        Assert.Empty(await KindsAsync(QaSeed.LabViewer, reference));
    }

    [Fact]
    public async Task T118_edits_notify_watchers_at_most_once_per_window_and_the_trash_reaches_the_subtree()
    {
        var ct = AtharApp.Timeout();
        var page = await NewPageAsync(QaSeed.Admin, "T118 edits");
        var child = await NewPageAsync(QaSeed.Admin, "T118 edits child", page);
        await Notifications().SetWatchAsync(QaSeed.Org, QaSeed.Member, page, true, ct);
        await Notifications().SetWatchAsync(QaSeed.Org, QaSeed.Member, child, true, ct);
        await ForceWatchAsync(QaSeed.LabViewer, page);

        var ops = await athar.OpsAsync();
        await ops.RunAsync(Person(QaSeed.Admin), page, Guid.NewGuid().ToString(), new PageOp("append", "First edit."), ct);
        await ops.RunAsync(Person(QaSeed.Admin), page, Guid.NewGuid().ToString(), new PageOp("append", "Second edit."), ct);
        var reference = $"page:{page}";
        Assert.Equal(["page_updated"], await KindsAsync(QaSeed.Member, reference)); // two persists, one notification
        Assert.Empty(await KindsAsync(QaSeed.Admin, reference)); // the editor watches it, but made the change
        Assert.Empty(await KindsAsync(QaSeed.LabViewer, reference));
        Assert.Contains(await Notifications().ListAsync(QaSeed.Member, QaSeed.Org, ct: ct),
            n => n.Kind == "page_updated" && n.Href == $"/wiki/p/{page}" && n.Where == "T118 edits" && n.Who == "Qa Admin");

        await new PageService(athar.Db(QaSeed.Org), TimeProvider.System).TrashAsync(new WikiActor(QaSeed.Admin, QaSeed.Org), page, ct);
        Assert.Equal(["page_updated", "page_trashed"], await KindsAsync(QaSeed.Member, reference));
        Assert.Equal(["page_trashed"], await KindsAsync(QaSeed.Member, $"page:{child}"));
        Assert.DoesNotContain("page_trashed", await KindsAsync(QaSeed.Admin, reference));
        Assert.DoesNotContain("page_trashed", await KindsAsync(QaSeed.LabViewer, reference));
    }

    [Fact]
    public async Task T118_a_draft_notifies_only_watchers_who_can_edit_it()
    {
        var ct = AtharApp.Timeout();
        // U-1: a draft is visible only to people who can edit it. KnowledgeOwner comments in the Engineering wiki but can't edit.
        var draft = await new PageService(athar.Db(QaSeed.Org), TimeProvider.System).CreateAsync(new WikiActor(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, null, "T118 draft", ct, draft: true);
        await ForceWatchAsync(QaSeed.Member, draft); // an editor
        await ForceWatchAsync(QaSeed.KnowledgeOwner, draft); // a commenter: can't read the draft

        await (await athar.OpsAsync()).RunAsync(Person(QaSeed.Admin), draft, Guid.NewGuid().ToString(), new PageOp("append", "Draft text."), ct);
        var reference = $"page:{draft}";
        Assert.Equal(["page_updated"], await KindsAsync(QaSeed.Member, reference));
        Assert.Empty(await KindsAsync(QaSeed.KnowledgeOwner, reference));
    }
}
