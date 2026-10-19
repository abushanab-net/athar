using System.Text.Json;
using Aspire.Hosting;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>
/// T041 op kinds through <see cref="PageOpsService"/> (the service T064's agent tools call): AT-13 restore, AT-32 agent
/// writes, AT-50 live tokens. Live clients are `src/collab/scripts/page-client.mjs`.
/// </summary>
[Collection(AtharCollection.Name)]
public class PageOpsTests(AtharApp athar)
{
    private static readonly HashSet<string> Write = ["read", "write"];
    private static readonly Actor Agent = new(QaSeed.Member, QaSeed.Org, QaSeed.MemberPatId, Write, DisclosureClass.ExternalAgent, null);
    private static readonly Actor ReadOnlyAgent = new(QaSeed.Member, QaSeed.Org, QaSeed.MemberReadPatId, new HashSet<string> { "read" }, DisclosureClass.ExternalAgent, null);
    private static readonly Actor AdminAgent = new(QaSeed.Admin, QaSeed.Org, QaSeed.AdminPatId, Write, DisclosureClass.ExternalAgent, null);
    /// <summary>A signed-in person: page rights only, no AI policy.</summary>
    private static readonly Actor Person = new(QaSeed.Admin, QaSeed.Org, null, Write, DisclosureClass.TrustedLocal, null);

    private static string Op() => Guid.NewGuid().ToString();

    private Task<PageOpsService> Ops() => athar.OpsAsync();

    [Fact]
    public async Task AT_32_agent_create_and_append_appear_attributed_and_a_repeated_op_id_does_not_duplicate()
    {
        var ct = AtharApp.Timeout();
        var op = Op();
        var created = await (await Ops()).CreatePageAsync(Agent, QaSeed.EngWiki, null, "AT-32 created", "# From an agent\n\nFirst line", op, ct);
        var again = await (await Ops()).CreatePageAsync(Agent, QaSeed.EngWiki, null, "AT-32 created", "# From an agent\n\nFirst line", op, ct);
        Assert.Equal((created.PageId, created.Revision), (again.PageId, again.Revision));

        var appendOp = Op();
        await (await Ops()).RunAsync(Agent, created.PageId, appendOp, new PageOp("append", "Second line"), ct);
        var view = await (await Ops()).GetPageAsync(Agent, created.PageId, ct: ct);
        Assert.Contains("First line", view.Content);
        Assert.Contains("Second line", view.Content);

        await using var db = athar.Db(QaSeed.Org);
        Assert.Equal(1, await db.Pages.CountAsync(p => p.SpaceId == QaSeed.EngWiki && p.Title == "AT-32 created", ct));
        var rev = await db.PageRevisions.Where(r => r.PageId == created.PageId).OrderBy(r => r.Revision).LastAsync(ct);
        Assert.Contains(QaSeed.Member, rev.Contributors);
        Assert.Contains(QaSeed.MemberPatId, rev.AgentCredentials);
        Assert.Contains(appendOp, rev.OpIds);
    }

    [Fact]
    public async Task AT_32_replace_checks_the_live_token_and_reports_orphaned_threads()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await Ops()).CreatePageAsync(Agent, QaSeed.EngWiki, null, "AT-32 replace", "Please anchor right here.", Op(), ct)).PageId;

        // A commenter-level anchor on "right here" (FR-CM-01).
        var thread = Guid.NewGuid();
        var pos = JsonSerializer.Deserialize<JsonElement>(await athar.RunAsync(page, QaSeed.Admin, "view", ct, "relpos", "right here"));
        await (await Ops()).RunAsync(Agent, page, Op(),
            new PageOp("anchorComment", ThreadId: thread, From: pos.GetProperty("from").GetString(), To: pos.GetProperty("to").GetString(), Quote: "right here"), ct);
        // A quote that doesn't match the anchored text is refused.
        var bad = await Assert.ThrowsAsync<AtharError>(async () => await (await Ops()).RunAsync(Agent, page, Op(),
            new PageOp("anchorComment", ThreadId: Guid.NewGuid(), From: pos.GetProperty("from").GetString(), To: pos.GetProperty("to").GetString(), Quote: "not this"), ct));
        Assert.Equal("anchor_failed", bad.Code);

        var stale = await Assert.ThrowsAsync<AtharError>(async () =>
            await (await Ops()).RunAsync(Agent, page, Op(), new PageOp("replace", "Replaced", ExpectedLiveToken: "1:0"), ct));
        Assert.Equal("conflict_revision", stale.Code);

        var token = (await (await Ops()).GetPageAsync(Agent, page, ct: ct)).LiveToken;
        var replaced = await (await Ops()).RunAsync(Agent, page, Op(), new PageOp("replace", "Replaced", ExpectedLiveToken: token), ct);
        Assert.Equal([thread], replaced.OrphanedThreads);
        Assert.Equal("Replaced", (await (await Ops()).GetPageAsync(Agent, page, ct: ct)).Content);
    }

    [Fact]
    public async Task AT_32_agent_writes_need_the_write_scope_and_the_space_ai_policy()
    {
        var ct = AtharApp.Timeout();
        // The confidential space keeps the default AI policy (no agent writes); the admin edits there in person.
        var closed = await new PageService(athar.Db(QaSeed.Org), TimeProvider.System).CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngConfidential, null, "AT-32 policy", ct);
        var e = await Assert.ThrowsAsync<AtharError>(async () => await (await Ops()).RunAsync(AdminAgent, closed, Op(), new PageOp("append", "x"), ct));
        Assert.Equal("policy_denied", e.Code);
        await (await Ops()).RunAsync(Person, closed, Op(), new PageOp("append", "In person"), ct);

        var deskPage = (await (await Ops()).CreatePageAsync(Agent, QaSeed.EngWiki, null, "AT-32 scope", "x", Op(), ct)).PageId;
        e = await Assert.ThrowsAsync<AtharError>(async () => await (await Ops()).RunAsync(ReadOnlyAgent, deskPage, Op(), new PageOp("append", "x"), ct));
        Assert.Equal("forbidden", e.Code);
    }

    [Fact]
    public async Task AT_13_restore_creates_the_next_revision_updates_open_editors_and_is_audited()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await Ops()).CreatePageAsync(Person, QaSeed.EngWiki, null, "AT-13 restore", "Version one", Op(), ct)).PageId;
        await (await Ops()).RunAsync(Person, page, Op(), new PageOp("append", "Version two"), ct);

        await using var db = athar.Db(QaSeed.Org);
        var first = await db.PageVersions.Where(v => v.PageId == page).OrderBy(v => v.Revision).FirstAsync(ct);
        Assert.Contains("Version one", first.ContentMd);
        Assert.DoesNotContain("Version two", first.ContentMd);

        using var watcher = athar.Start(page, QaSeed.Admin, "view", "watch", "Version two");
        await watcher.ExpectAsync("ready", ct);
        var before = (await (await Ops()).GetPageAsync(Person, page, ct: ct)).Revision;
        var restoreOp = Op();
        var restored = await (await Ops()).RestoreAsync(Person, page, first.Id, restoreOp, ct);

        Assert.Equal(before + 1, restored.Revision);
        await watcher.ExpectAsync("seen", ct); // the open editor got the restored content live
        var view = await (await Ops()).GetPageAsync(Person, page, ct: ct);
        Assert.Contains("Version one", view.Content);
        Assert.DoesNotContain("Version two", view.Content);
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "page.restore" && a.ResourceId == page.ToString(), ct));
    }

    [Fact]
    public async Task AT_50_an_unsaved_human_edit_moves_the_live_token_and_both_are_attributed()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await Ops()).CreatePageAsync(Agent, QaSeed.EngWiki, null, "AT-50 human", "Start", Op(), ct)).PageId;
        var stale = (await (await Ops()).GetPageAsync(Agent, page, ct: ct)).LiveToken;

        using var human = athar.Start(page, QaSeed.Admin, "edit", "type", "Human words");
        await human.ExpectAsync("typed", ct); // in collab, not persisted yet
        var e = await Assert.ThrowsAsync<AtharError>(async () =>
            await (await Ops()).RunAsync(Agent, page, Op(), new PageOp("replace", "Agent replaced", ExpectedLiveToken: stale), ct));
        Assert.Equal("conflict_revision", e.Code);

        var current = await (await Ops()).GetPageAsync(Agent, page, ct: ct);
        Assert.Contains("Human words", current.Content); // the snapshot includes the unsaved edit
        await (await Ops()).RunAsync(Agent, page, Op(), new PageOp("replace", "Agent replaced", ExpectedLiveToken: current.LiveToken), ct);

        // Both are attributed in the revisions since the start (the human's may persist on its own debounce first).
        await using var db = athar.Db(QaSeed.Org);
        var since = await db.PageRevisions.Where(r => r.PageId == page && r.Revision > 1).SelectMany(r => r.Contributors).ToListAsync(ct);
        Assert.Contains(QaSeed.Admin, since);
        Assert.Contains(QaSeed.Member, since);
        human.StandardInput.Close();
        await human.WaitForExitAsync(ct);
    }

    /// <summary>
    /// Bug bash (T085): a page stored with content but no Yjs state (the qa seed, imports) opened as empty in collab, and
    /// its first edit persisted an empty page over the content. Collab now builds the live document from the stored JSON.
    /// </summary>
    [Fact]
    public async Task A_page_stored_without_yjs_state_keeps_its_content_on_first_edit()
    {
        var ct = AtharApp.Timeout();
        var page = QaSeed.ApiReferencePage; // seeded: JSON and Markdown, no Yjs state
        await (await Ops()).RunAsync(Person, page, Op(), new PageOp("append", "Added in the bug bash."), ct);

        var view = await (await Ops()).GetPageAsync(Person, page, ct: ct);
        Assert.Contains("كل طلب يحمل", view.Content);
        Assert.Contains("Every request carries a bearer token", view.Content);
        Assert.Contains("Added in the bug bash.", view.Content);
    }

    [Fact]
    public async Task AT_50_a_live_token_from_before_a_collab_restart_is_stale()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await Ops()).CreatePageAsync(Agent, QaSeed.EngWiki, null, "AT-50 restart", "Before restart", Op(), ct)).PageId;

        // A token read while the page is live in collab...
        using var reader = athar.Start(page, QaSeed.Admin, "view", "watch", "never-present-marker");
        await reader.ExpectAsync("ready", ct);
        var token = (await (await Ops()).GetPageAsync(Agent, page, ct: ct)).LiveToken;
        reader.Kill();

        // ...then collab restarts (a new epoch) and someone else edits.
        var restarted = await athar.App.ResourceCommands.ExecuteCommandAsync("collab", KnownResourceCommands.RestartCommand, ct);
        Assert.True(restarted.Success, restarted.Message);
        await athar.App.ResourceNotifications.WaitForResourceHealthyAsync("collab", ct);
        await (await Ops()).RunAsync(Person, page, Op(), new PageOp("append", "After restart"), ct);

        var e = await Assert.ThrowsAsync<AtharError>(async () =>
            await (await Ops()).RunAsync(Agent, page, Op(), new PageOp("replace", "Too late", ExpectedLiveToken: token), ct));
        Assert.Equal("conflict_revision", e.Code);
    }
}
