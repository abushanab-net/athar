using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T042 comments (FR-CM-01..07) on the Engineering wiki: AT-15, AT-16, AT-18, AT-19, AT-51.</summary>
[Collection(AtharCollection.Name)]
public class CommentTests(AtharApp athar)
{
    private static readonly HashSet<string> Scopes = ["read", "write"];
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, Scopes, DisclosureClass.TrustedLocal, null);
    private static readonly Actor Admin = Person(QaSeed.Admin);            // space admin
    private static readonly Actor Editor = Person(QaSeed.Member);          // editor
    private static readonly Actor Commenter = Person(QaSeed.KnowledgeOwner);
    private static readonly Actor Viewer = Person(QaSeed.Guest);

    /// <summary>A clock the test moves (the anchor reconciler's 2 minutes).</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<CommentService> Comments(HttpMessageHandler? collab = null, TimeProvider? clock = null)
    {
        var db = athar.Db(QaSeed.Org);
        return new CommentService(db, await athar.OpsAsync(collab, db), clock ?? TimeProvider.System, new NotificationService(db));
    }

    private async Task<Guid> PageWith(string title, string text) =>
        (await (await athar.OpsAsync()).CreatePageAsync(Admin, QaSeed.EngWiki, null, title, text, Guid.NewGuid().ToString(), AtharApp.Timeout())).PageId;

    private async Task<ThreadView> Inline(Actor who, Guid page, string quote, string body = "Inline note", CommentService? service = null)
    {
        var pos = JsonSerializer.Deserialize<JsonElement>(await athar.RunAsync(page, QaSeed.Admin, "view", AtharApp.Timeout(), "relpos", quote));
        return await (service ?? await Comments()).AddInlineThreadAsync(who, page, body, pos.GetProperty("from").GetString()!, pos.GetProperty("to").GetString()!,
            quote, Guid.NewGuid().ToString(), AtharApp.Timeout());
    }

    private async Task<ThreadView> Thread(Guid page, Guid id) =>
        (await (await Comments()).ListAsync(Admin, page, includeResolved: true, AtharApp.Timeout())).Single(t => t.Id == id);

    private async Task<string> Text(Guid page) => (await (await athar.OpsAsync()).GetPageAsync(Admin, page, ct: AtharApp.Timeout())).Content;

    [Fact]
    public async Task AT_15_a_commenter_anchors_an_inline_thread_but_cannot_edit_the_content()
    {
        var ct = AtharApp.Timeout();
        var page = await PageWith("AT-15", "The deploy key rotates every quarter.");

        var thread = await Inline(Commenter, page, "every quarter");
        Assert.Equal("open", thread.Status);
        Assert.True(thread.Inline);
        Assert.Equal("every quarter", thread.Quote);

        // The same commenter's raw content update is dropped by collab (FR-RT-03).
        await athar.RunAsync(page, QaSeed.KnowledgeOwner, "comment", ct, "forge", "forged by a commenter");
        Assert.DoesNotContain("forged by a commenter", await Text(page));
    }

    [Fact]
    public async Task AT_15_an_agent_anchors_by_quote_alone_and_a_hint_settles_repeats()
    {
        var ct = AtharApp.Timeout();
        var page = await PageWith("AT-15 by quote", "Then rotate the key again.\n\nOnly here: rotate the key.");
        async Task<ThreadView> ByQuote(string quote, string? hint = null) =>
            await (await Comments()).AddInlineThreadAsync(Commenter, page, "Agent note", null, null, quote, Guid.NewGuid().ToString(), ct, hint);

        Assert.Equal("open", (await ByQuote("again")).Status);
        Assert.Equal("anchor_failed", (await ByQuote("rotate the key")).Status);   // twice, no hint
        Assert.Equal("open", (await ByQuote("rotate the key", "Only here")).Status);
        Assert.Equal("anchor_failed", (await ByQuote("not in the page")).Status);
    }

    [Fact]
    public async Task AT_16_anchors_shrink_survive_concurrent_edits_and_orphan_on_full_delete()
    {
        var ct = AtharApp.Timeout();
        var page = await PageWith("AT-16", "Alpha beta gamma delta.");
        var thread = await Inline(Editor, page, "beta gamma");

        // Partial delete shrinks the anchor; the thread stays inline.
        await athar.RunAsync(page, QaSeed.Member, "edit", ct, "delete", "beta ");
        Assert.Equal("open", (await Thread(page, thread.Id)).Status);

        // Two people typing inside the anchored text at once: Yjs merges, the anchor covers what's there.
        using (var one = athar.Start(page, QaSeed.Member, "edit", "insert", "gamma", "X"))
        using (var two = athar.Start(page, QaSeed.Admin, "edit", "insert", "gamma", "Y"))
        {
            await one.ExpectAsync("ready", ct);
            await two.ExpectAsync("ready", ct);
            await one.StandardInput.WriteLineAsync("go");
            await two.StandardInput.WriteLineAsync("go");
            await one.StandardInput.FlushAsync(ct);
            await two.StandardInput.FlushAsync(ct);
            await Task.WhenAll(one.WaitForExitAsync(ct), two.WaitForExitAsync(ct));
            Assert.Equal(0, one.ExitCode + two.ExitCode);
        }
        await Task.Delay(2500, ct); // the last client leaving persists; give the debounce room
        Assert.Equal("open", (await Thread(page, thread.Id)).Status);

        // Deleting all of it orphans the thread: page-level, quote kept, detected on persist without a browser.
        var remaining = (await Text(page)).Split(' ')[1]; // what's left of "gamma" with the inserts
        await athar.RunAsync(page, QaSeed.Member, "edit", ct, "delete", remaining);
        var orphaned = await Thread(page, thread.Id);
        for (var i = 0; i < 50 && orphaned.Status != "orphaned"; i++) // the persist runs as the last client leaves
        {
            await Task.Delay(200, ct);
            orphaned = await Thread(page, thread.Id);
        }
        Assert.Equal("orphaned", orphaned.Status);
        Assert.False(orphaned.Inline);
        Assert.Equal("beta gamma", orphaned.Quote);
    }

    [Fact]
    public async Task AT_18_edit_delete_resolve_and_reopen_are_audited_with_before_and_after()
    {
        var ct = AtharApp.Timeout();
        var page = await PageWith("AT-18", "Audit me.");
        var threadId = await (await Comments()).AddPageThreadAsync(Editor, page, "First draft", ct);
        var commentId = (await Thread(page, threadId)).Comments.Single().Id;

        await (await Comments()).EditAsync(Editor, commentId, "Second draft", ct);
        await (await Comments()).ResolveAsync(Commenter, threadId, ct);
        Assert.Equal("resolved", (await Thread(page, threadId)).Status);
        Assert.DoesNotContain(await (await Comments()).ListAsync(Admin, page, ct: ct), t => t.Id == threadId); // hidden by default
        await (await Comments()).ReopenAsync(Commenter, threadId, ct);
        var reply = await (await Comments()).ReplyAsync(Commenter, threadId, "A reply", ct);

        // Authors edit their own; a space admin may delete anyone's; an editor may not delete someone else's.
        var e = await Assert.ThrowsAsync<AtharError>(() => Task.Run(async () => await (await Comments()).EditAsync(Editor, reply, "hijack", ct)));
        Assert.Equal("forbidden", e.Code);
        e = await Assert.ThrowsAsync<AtharError>(() => Task.Run(async () => await (await Comments()).DeleteAsync(Editor, reply, ct)));
        Assert.Equal("forbidden", e.Code);
        await (await Comments()).DeleteAsync(Admin, reply, ct);
        await (await Comments()).DeleteAsync(Editor, commentId, ct);
        var view = await Thread(page, threadId);
        Assert.All(view.Comments, c => Assert.True(c.Deleted));
        Assert.All(view.Comments, c => Assert.Null(c.BodyMd));

        await using var db = athar.Db(QaSeed.Org);
        foreach (var (action, id) in new[] { ("comment.edit", commentId), ("comment.delete", commentId), ("comment.delete", reply), ("comment.resolve", threadId), ("comment.reopen", threadId) })
        {
            var audit = await db.AuditEvents.Where(a => a.Action == action && a.ResourceId == id.ToString()).ToListAsync(ct);
            Assert.NotEmpty(audit);
            Assert.All(audit, a => { Assert.NotNull(a.Before); Assert.NotNull(a.After); });
        }
        var edit = await db.AuditEvents.SingleAsync(a => a.Action == "comment.edit" && a.ResourceId == commentId.ToString(), ct);
        Assert.Contains("First draft", edit.Before);
        Assert.Contains("Second draft", edit.After);
    }

    [Fact]
    public async Task AT_19_a_viewer_cannot_comment()
    {
        var ct = AtharApp.Timeout();
        var page = await PageWith("AT-19", "Read only for some.");
        var e = await Assert.ThrowsAsync<AtharError>(() => Task.Run(async () => await (await Comments()).AddPageThreadAsync(Viewer, page, "Hi", ct)));
        Assert.Equal("forbidden", e.Code);
        e = await Assert.ThrowsAsync<AtharError>(() => Inline(Viewer, page, "Read only"));
        Assert.Equal("forbidden", e.Code);
        Assert.NotNull(await (await Comments()).ListAsync(Viewer, page, ct: ct)); // viewers still read threads
    }

    [Fact]
    public async Task AT_51_collab_unavailable_during_anchoring_fails_the_anchor_and_notifies_the_author()
    {
        var ct = AtharApp.Timeout();
        var page = await PageWith("AT-51", "Anchor this sentence.");
        var clock = new Clock();
        var service = await Comments(new Unavailable(), clock);

        var thread = await Inline(Commenter, page, "this sentence", service: service);
        Assert.Equal("pending_anchor", thread.Status);

        clock.Now += TimeSpan.FromMinutes(3);
        Assert.True(await (await Comments(clock: clock)).ReconcileAnchorsAsync(ct) >= 1);

        var failed = await Thread(page, thread.Id);
        Assert.Equal("anchor_failed", failed.Status);
        Assert.False(failed.Inline); // kept as a page-level thread, with its comment and quote
        Assert.Equal("Inline note", failed.Comments.Single().BodyMd);
        await using var db = athar.Db(QaSeed.Org);
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == QaSeed.KnowledgeOwner && n.Kind == "anchor_failed" && n.Ref == $"thread:{thread.Id}", ct));
    }

    /// <summary>Collab is down: every call fails before reaching it.</summary>
    private sealed class Unavailable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("collab unavailable");
    }
}
