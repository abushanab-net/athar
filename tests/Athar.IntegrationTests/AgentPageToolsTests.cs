using System.Text.Json;
using Aspire.Hosting;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;

namespace Athar.IntegrationTests;

/// <summary>T064 (FR-AG-06): the agent page tools over MCP, as Claude Code calls them.</summary>
[Collection(AtharCollection.Name)]
public class AgentPageToolsTests(AtharApp athar)
{
    [Fact]
    public async Task AT_32_agent_writes_are_live_attributed_and_idempotent()
    {
        var ct = AtharApp.Timeout();
        await using var mcp = await Connect(QaSeed.AgentPat, ct);
        var title = $"Agent notes {Guid.NewGuid():N}";
        var create = new Dictionary<string, object?> { ["space_id"] = QaSeed.EngWiki, ["title"] = title, ["markdown"] = "First line.", ["op_id"] = $"c-{title}" };
        var created = await Mcp.Call(mcp, "create_page", create, ct);
        var page = created.GetProperty("page_id").GetGuid();
        Assert.Equal(page, (await Mcp.Call(mcp, "create_page", create, ct)).GetProperty("page_id").GetGuid()); // repeated op_id

        await Mcp.Call(mcp, "append_to_page", new() { ["page_id"] = page, ["markdown"] = "Appended by the agent.", ["op_id"] = $"a-{title}" }, ct);
        var view = await Mcp.Call(mcp, "get_page", new() { ["page_id"] = page }, ct);
        Assert.Contains("Appended by the agent.", view.GetProperty("content").GetString());
        var list = await Mcp.Call(mcp, "list_pages", new() { ["space_id"] = QaSeed.EngWiki }, ct);
        Assert.Contains(list.GetProperty("pages").EnumerateArray(), p => p.GetProperty("page_id").GetGuid() == page);
        Assert.Equal("done", (await Mcp.Call(mcp, "get_operation", new() { ["op_id"] = $"a-{title}" }, ct)).GetProperty("status").GetString());

        // replace_page: a stale live token is refused, the current one reports the orphaned threads
        var token = view.GetProperty("live_token").GetString();
        await Mcp.Call(mcp, "append_to_page", new() { ["page_id"] = page, ["markdown"] = "Moves the token.", ["op_id"] = $"a2-{title}" }, ct);
        Assert.StartsWith("conflict_revision", await Refused(mcp, "replace_page", new() { ["page_id"] = page, ["markdown"] = "Stale.", ["expected_live_token"] = token, ["op_id"] = $"r1-{title}" }, ct));
        var current = (await Mcp.Call(mcp, "get_page", new() { ["page_id"] = page }, ct)).GetProperty("live_token").GetString();
        var replaced = await Mcp.Call(mcp, "replace_page", new() { ["page_id"] = page, ["markdown"] = "Rewritten.", ["expected_live_token"] = current, ["op_id"] = $"r2-{title}" }, ct);
        Assert.Equal(JsonValueKind.Array, replaced.GetProperty("orphaned_threads").ValueKind);

        // add_comment: an inline thread anchored by quote alone, a replay, a reply; a quote that isn't there stays page-level
        var comment = new Dictionary<string, object?> { ["page_id"] = page, ["body_md"] = "Please review.", ["quote"] = "Rewritten", ["op_id"] = $"m1-{title}" };
        var inline = await Mcp.Call(mcp, "add_comment", comment, ct);
        Assert.Equal("open", inline.GetProperty("status").GetString());
        var thread = inline.GetProperty("thread_id").GetGuid();
        Assert.Equal(thread, (await Mcp.Call(mcp, "add_comment", comment, ct)).GetProperty("thread_id").GetGuid());
        await Mcp.Call(mcp, "add_comment", new() { ["page_id"] = page, ["body_md"] = "Done.", ["thread_id"] = thread, ["op_id"] = $"m2-{title}" }, ct);
        var missing = await Mcp.Call(mcp, "add_comment", new() { ["page_id"] = page, ["body_md"] = "Where?", ["quote"] = "not on the page", ["op_id"] = $"m3-{title}" }, ct);
        Assert.Equal("anchor_failed", missing.GetProperty("status").GetString());
        Assert.StartsWith("invalid_input", await Refused(mcp, "add_comment", new() { ["page_id"] = page, ["body_md"] = "x", ["thread_id"] = thread, ["quote"] = "Rewritten", ["op_id"] = $"m4-{title}" }, ct));

        await using var db = athar.Db(QaSeed.Org);
        Assert.Equal(1, await db.Pages.CountAsync(p => p.Title == title, ct));
        Assert.Contains(await db.PageRevisions.Where(r => r.PageId == page).SelectMany(r => r.AgentCredentials).ToListAsync(ct), c => c == QaSeed.AgentPatId);
        var bodies = await db.Comments.Where(c => c.ThreadId == thread).OrderBy(c => c.CreatedAt).Select(c => c.BodyMd).ToListAsync(ct);
        Assert.Equal(["Please review.", "Done."], bodies);
        var anchored = await db.CommentThreads.SingleAsync(t => t.Id == thread, ct);
        Assert.Equal((ThreadStatus.Open, "Rewritten"), (anchored.Status, anchored.Quote));
    }

    [Fact]
    public async Task AT_33_delete_page_follows_the_ai_policy_and_trashes()
    {
        var ct = AtharApp.Timeout();
        await using var agent = await Connect(QaSeed.AgentPat, ct);
        var title = $"Scratch {Guid.NewGuid():N}";
        var page = (await Mcp.Call(agent, "create_page", new() { ["space_id"] = QaSeed.AgentSandbox, ["title"] = title, ["markdown"] = "Temporary.", ["op_id"] = $"c-{title}" }, ct))
            .GetProperty("page_id").GetGuid();

        // EngWiki lets agents write but not delete; a token without pages:delete can't delete anywhere
        Assert.StartsWith("policy_denied", await Refused(agent, "delete_page", new() { ["page_id"] = QaSeed.DeployRunbookPage, ["reason"] = "x", ["op_id"] = $"d0-{title}" }, ct));
        await using (var noScope = await Connect(QaSeed.MemberPat, ct))
            Assert.StartsWith("forbidden", await Refused(noScope, "delete_page", new() { ["page_id"] = page, ["reason"] = "x", ["op_id"] = $"d1-{title}" }, ct));

        var delete = new Dictionary<string, object?> { ["page_id"] = page, ["reason"] = "No longer needed", ["op_id"] = $"d2-{title}" };
        var deleted = await Mcp.Call(agent, "delete_page", delete, ct);
        Assert.True(deleted.GetProperty("trashed").GetBoolean());
        Assert.True(deleted.GetProperty("restore_until").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29));
        Assert.Equal(deleted.GetRawText(), (await Mcp.Call(agent, "delete_page", delete, ct)).GetRawText()); // repeated op_id

        await using var db = athar.Db(QaSeed.Org);
        Assert.Null((await db.Pages.SingleAsync(p => p.Id == QaSeed.DeployRunbookPage, ct)).DeletedAt); // the refused delete changed nothing
        Assert.NotNull((await db.Pages.SingleAsync(p => p.Id == page, ct)).DeletedAt); // in the trash, restorable
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.trash" && e.ResourceId == page.ToString(), ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.agent_delete" && e.ResourceId == page.ToString() && e.CredentialId == QaSeed.AgentPatId, ct));
    }

    private Task<McpClient> Connect(string pat, CancellationToken ct) =>
        Mcp.Connect(athar.Web(), new Uri(athar.App.GetEndpoint("web", "https"), "/mcp"), pat, ct);

    /// <summary>The error of a refused tool call, from its docs/06 §6.6 code on (the SDK prefixes "An error occurred invoking 'tool': ").</summary>
    private static async Task<string> Refused(McpClient mcp, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await mcp.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.True(result.IsError == true, $"{tool} should have been refused");
        var text = string.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        var i = text.IndexOf("': ", StringComparison.Ordinal);
        return i < 0 ? text : text[(i + 3)..];
    }
}
