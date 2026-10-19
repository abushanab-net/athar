using System.Text.Json;
using Aspire.Hosting;
using Athar.Data.Seed;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T176 (FR-AG-12a, FR-AU-01): every MCP tool call is captured without its arguments, and agent reads are audited.</summary>
[Collection(AtharCollection.Name)]
public class McpCaptureTests(AtharApp athar)
{
    [Fact]
    public async Task AT_67_tool_calls_are_captured_and_agent_reads_audited()
    {
        var ct = AtharApp.Timeout();
        var since = DateTimeOffset.UtcNow.AddSeconds(-5);
        var query = $"rotate deploy key {Guid.NewGuid():N}";
        await using (var mcp = await Mcp.Connect(athar.Web(), new Uri(athar.App.GetEndpoint("web", "https"), "/mcp"), QaSeed.AgentPat, ct))
        {
            var hits = (await Mcp.Call(mcp, "search", new() { ["query"] = query }, ct)).GetProperty("hits").GetArrayLength();
            await Mcp.Call(mcp, "get_page", new() { ["page_id"] = QaSeed.DeployRunbookPage }, ct);
            var pages = (await Mcp.Call(mcp, "list_pages", new() { ["space_id"] = QaSeed.EngWiki }, ct)).GetProperty("pages").GetArrayLength();
            await Mcp.CallError(mcp, "get_page", new() { ["page_id"] = Guid.NewGuid() }, ct);

            await using var db = athar.Db(QaSeed.Org);
            var calls = await db.McpCalls.Where(c => c.CredentialId == QaSeed.AgentPatId && c.CreatedAt >= since).OrderBy(c => c.CreatedAt).ToListAsync(ct);
            Assert.Equal(["search", "get_page", "list_pages", "get_page"], calls.Select(c => c.Tool));
            Assert.All(calls, c => Assert.Equal(QaSeed.Member, c.ActorUserId));
            Assert.Equal(["ok", "ok", "ok", "not_found"], calls.Select(c => c.Outcome));
            Assert.Equal([hits, null, pages, null], calls.Select(c => c.ResultCount));

            var search = Assert.Single(await Searches(db, since, ct), e => e.After!.Contains(query));
            Assert.Equal(QaSeed.AgentPatId, search.CredentialId);
            Assert.Equal(hits, JsonDocument.Parse(search.After!).RootElement.GetProperty("ids").GetArrayLength());
            Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "page.view" && e.CreatedAt >= since && e.ResourceId == QaSeed.DeployRunbookPage.ToString(), ct));
            var list = await db.AuditEvents.SingleAsync(e => e.Action == "page.list" && e.CreatedAt >= since && e.ResourceId == QaSeed.EngWiki.ToString(), ct);
            Assert.Equal(pages, JsonDocument.Parse(list.After!).RootElement.GetProperty("ids").GetArrayLength());
        }

        // REST reads are audited the same way; only MCP tool calls go to mcp_calls.
        var http = athar.Web();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await Mcp.Get(http, $"/api/v1/pages/{QaSeed.DeployRunbookPage}", QaSeed.AgentPat, ct)).Status);
        await Mcp.Get(http, $"/api/v1/search?q={Uri.EscapeDataString(query + " rest")}", QaSeed.AgentPat, ct);
        await using (var db = athar.Db(QaSeed.Org))
        {
            Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "page.view" && e.CreatedAt >= since && e.ResourceId == QaSeed.DeployRunbookPage.ToString(), ct));
            Assert.Contains(await Searches(db, since, ct), e => e.After!.Contains(query + " rest"));
            Assert.Equal(4, await db.McpCalls.CountAsync(c => c.CredentialId == QaSeed.AgentPatId && c.CreatedAt >= since, ct));
        }
    }

    // After is jsonb: filter its text in memory.
    private static Task<List<Athar.Domain.Model.AuditEvent>> Searches(Athar.Data.AtharDbContext db, DateTimeOffset since, CancellationToken ct) =>
        db.AuditEvents.Where(e => e.Action == "memory.search" && e.CreatedAt >= since).ToListAsync(ct);
}
