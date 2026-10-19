using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>
/// T043 (FR-CM-04, FR-NT-01): mentions and reply notifications reach only people who can see the page (AT-17).
/// LabViewer is an Engineering member with no role in the Engineering wiki, so the wiki is invisible to them.
/// </summary>
[Collection(AtharCollection.Name)]
public class MentionTests(AtharApp athar)
{
    private static readonly HashSet<string> Scopes = ["read", "write"];
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, Scopes, DisclosureClass.TrustedLocal, null);
    private static string At(string name, Guid id) => $"@[{name}](user:{id})";

    private async Task<CommentService> Comments()
    {
        var db = athar.Db(QaSeed.Org);
        return new CommentService(db, await athar.OpsAsync(db: db), TimeProvider.System, new NotificationService(db));
    }

    private async Task<List<Notification>> For(Guid user, string reference)
    {
        await using var db = athar.Db(QaSeed.Org);
        return await db.Notifications.Where(n => n.UserId == user && n.Ref == reference).ToListAsync(AtharApp.Timeout());
    }

    [Fact]
    public async Task AT_17_a_reply_with_mentions_notifies_only_people_who_can_see_the_page()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await athar.OpsAsync()).CreatePageAsync(Person(QaSeed.Admin), QaSeed.EngWiki, null, "AT-17", "Mentions here.", Guid.NewGuid().ToString(), ct)).PageId;
        var thread = await (await Comments()).AddPageThreadAsync(Person(QaSeed.Member), page, "Kick-off", ct);

        await (await Comments()).ReplyAsync(Person(QaSeed.KnowledgeOwner), thread,
            $"{At("Qa Admin", QaSeed.Admin)} and {At("Lab Viewer", QaSeed.LabViewer)}, please look", ct);

        var reference = $"thread:{thread}";
        Assert.Single(await For(QaSeed.Admin, reference), n => n.Kind == "mention");
        Assert.Empty(await For(QaSeed.LabViewer, reference)); // can't see the page: no notification, no mention row
        Assert.Single(await For(QaSeed.Member, reference), n => n.Kind == "reply"); // the thread's author hears about the reply
        Assert.Empty(await For(QaSeed.KnowledgeOwner, reference)); // never yourself

        await using var db = athar.Db(QaSeed.Org);
        Assert.Equal([QaSeed.Admin], await db.Mentions.Where(m => m.SourceKind == MentionSource.Comment && m.SourceId == thread).Select(m => m.UserId).ToListAsync(ct));

        // Editing a comment notifies only people newly mentioned.
        var first = (await (await Comments()).ListAsync(Person(QaSeed.Admin), page, ct: ct)).Single(t => t.Id == thread).Comments[0].Id;
        // (Count mentions only: Admin created the page, so they also watch it and hear about the new thread, T118.)
        await (await Comments()).EditAsync(Person(QaSeed.Member), first, $"Kick-off, {At("Qa Admin", QaSeed.Admin)}", ct);
        Assert.Equal(2, (await For(QaSeed.Admin, reference)).Count(n => n.Kind == "mention"));
        await (await Comments()).EditAsync(Person(QaSeed.Member), first, $"Kick-off! {At("Qa Admin", QaSeed.Admin)}", ct);
        Assert.Equal(2, (await For(QaSeed.Admin, reference)).Count(n => n.Kind == "mention"));
    }

    [Fact]
    public async Task Mentions_in_page_content_notify_once_and_only_people_who_can_see_it()
    {
        var ct = AtharApp.Timeout();
        var ops = await athar.OpsAsync();
        var page = (await ops.CreatePageAsync(Person(QaSeed.Admin), QaSeed.EngWiki, null, "Page mentions",
            $"Ping {At("Qa Member", QaSeed.Member)} and {At("Lab Viewer", QaSeed.LabViewer)}.", Guid.NewGuid().ToString(), ct)).PageId;

        var reference = $"page:{page}";
        Assert.Single(await For(QaSeed.Member, reference));
        Assert.Empty(await For(QaSeed.LabViewer, reference));

        // Another persist with the same mention doesn't notify again.
        await (await athar.OpsAsync()).RunAsync(Person(QaSeed.Admin), page, Guid.NewGuid().ToString(), new PageOp("append", "More text."), ct);
        Assert.Single(await For(QaSeed.Member, reference));

        var notifications = new NotificationService(athar.Db(QaSeed.Org));
        Assert.Contains(await notifications.ListAsync(QaSeed.Member, QaSeed.Org, ct: ct), n => n.Ref == reference && n.Href == $"/wiki/p/{page}" && !n.Read);
        var unread = await notifications.UnreadCountAsync(QaSeed.Member, QaSeed.Org, ct);
        Assert.True(await notifications.MarkReadAsync(QaSeed.Member, QaSeed.Org, ct: ct) >= 1);
        Assert.Equal(0, await notifications.UnreadCountAsync(QaSeed.Member, QaSeed.Org, ct));
        Assert.True(unread >= 1);
    }

    [Fact]
    public async Task Mention_candidates_are_people_who_can_see_the_page()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await athar.OpsAsync()).CreatePageAsync(Person(QaSeed.Admin), QaSeed.EngWiki, null, "Candidates", "x", Guid.NewGuid().ToString(), ct)).PageId;
        var people = await new NotificationService(athar.Db(QaSeed.Org)).CandidatesAsync(QaSeed.Org, page, "", 50, ct);
        Assert.Contains(people, p => p.Id == QaSeed.Member);
        Assert.DoesNotContain(people, p => p.Id == QaSeed.LabViewer);
    }
}
