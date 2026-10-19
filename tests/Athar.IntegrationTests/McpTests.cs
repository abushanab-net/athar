using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;

namespace Athar.IntegrationTests;

[Collection(AtharCollection.Name)]
public class McpTests(AtharApp athar)
{
    // G1-d (T005): PAT auth, remember and recall (FTS) over stateless Streamable HTTP, against the qa seed.
    [Fact]
    public async Task G1d_remember_and_recall_over_mcp_with_pat()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();
        var endpoint = new Uri(http.BaseAddress!, "/mcp");

        // No token, a deactivated user's token, or a browser Origin off the allow-list: refused before MCP runs.
        await Mcp.Refused(HttpStatusCode.Unauthorized, http, endpoint, null, null, ct);
        await Mcp.Refused(HttpStatusCode.Unauthorized, http, endpoint, QaSeed.DeactivatedPat, null, ct);
        await Mcp.Refused(HttpStatusCode.Forbidden, http, endpoint, QaSeed.MemberPat, "https://evil.example", ct);

        await using var member = await Mcp.Connect(http, endpoint, QaSeed.MemberPat, ct);
        await using var owner = await Mcp.Connect(http, endpoint, QaSeed.KnowledgeOwnerPat, ct);
        await using var readOnly = await Mcp.Connect(http, endpoint, QaSeed.MemberReadPat, ct);

        var tools = await member.ListToolsAsync(cancellationToken: ct);
        Assert.Contains(tools, t => t.Name == "remember");
        Assert.Contains(tools, t => t.Name == "recall");

        var opId = Guid.NewGuid().ToString();
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Use UTC timestamps", ["body_md"] = "All stored timestamps are UTC.",
            ["type"] = "convention", ["scope"] = "personal", ["op_id"] = opId, ["links"] = new[] { "https://example.test/adr/7" },
        };
        var first = await Mcp.Call(member, "remember", args, ct);
        Assert.Equal("published", first.GetProperty("result").GetString());
        var memoryId = Guid.Parse(first.GetProperty("memory_id").GetString()!);

        // op_id (FR-RT-07): same actor and payload get the stored result; another payload is op_id_reused; another actor gets not_found.
        Assert.Equal(memoryId, Guid.Parse((await Mcp.Call(member, "remember", args, ct)).GetProperty("memory_id").GetString()!));
        Assert.Contains("op_id_reused", await Mcp.CallError(member, "remember", new(args) { ["title"] = "Other" }, ct));
        Assert.Contains("not_found", await Mcp.CallError(owner, "remember", args, ct));

        // Company writes (FR-MM-02): a member proposes, a knowledge owner publishes.
        var proposed = await Mcp.Call(member, "remember", new(args) { ["scope"] = "company", ["op_id"] = Guid.NewGuid().ToString() }, ct);
        Assert.Equal("proposed", proposed.GetProperty("result").GetString());
        var proposalId = Guid.Parse(proposed.GetProperty("proposal_id").GetString()!);
        var published = await Mcp.Call(owner, "remember", new(args)
        {
            ["title"] = "Timestamps policy", ["body_md"] = "Log timestamps in UTC with an offset.", ["type"] = "policy",
            ["scope"] = "company", ["op_id"] = Guid.NewGuid().ToString(),
        }, ct);
        Assert.Equal("published", published.GetProperty("result").GetString());
        var companyId = published.GetProperty("memory_id").GetString();

        // Token scopes: read-only can't write.
        Assert.Contains("forbidden", await Mcp.CallError(readOnly, "remember", new(args) { ["op_id"] = Guid.NewGuid().ToString() }, ct));

        // Recall: any term matches, own personal plus company; the owner doesn't see the member's personal memory.
        // (`degraded` depends on Ollama; AT-39 in SearchTests covers it.)
        var recall = await Mcp.Call(member, "recall", new() { ["query"] = "timestamps offset" }, ct);
        var items = recall.GetProperty("groups")[0].GetProperty("items").EnumerateArray().ToList();
        var item = Assert.Single(items, i => i.GetProperty("id").GetString() == memoryId.ToString());
        Assert.Equal("personal", item.GetProperty("scope").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());
        Assert.True(item.GetProperty("annotations").GetProperty("binding").GetBoolean());
        Assert.Equal("https://example.test/adr/7", item.GetProperty("provenance").GetProperty("links")[0].GetString());
        Assert.Contains(items, i => i.GetProperty("id").GetString() == companyId);
        var ownerItems = (await Mcp.Call(owner, "recall", new() { ["query"] = "timestamps" }, ct)).GetProperty("groups")[0].GetProperty("items");
        Assert.DoesNotContain(ownerItems.EnumerateArray(), i => i.GetProperty("id").GetString() == memoryId.ToString());

        // Same-transaction rows: memory, revision 1, chunk, operation, audit with the authenticated credential.
        await using var db = athar.Db(QaSeed.Org);
        Assert.Equal(1, await db.MemoryVersions.CountAsync(v => v.MemoryId == memoryId && v.Revision == 1, ct));
        Assert.Equal(1, await db.Chunks.CountAsync(c => c.OwnerId == memoryId, ct));
        Assert.Equal(1, await db.Operations.CountAsync(o => o.OpId == opId && o.ActorUserId == QaSeed.Member, ct));
        Assert.Equal(ProposalStatus.Pending, (await db.MemoryProposals.SingleAsync(p => p.Id == proposalId, ct)).Status);
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "memory.create" && e.ResourceId == memoryId.ToString(), ct);
        Assert.Equal(QaSeed.Member, audit.ActorUserId);
        Assert.Equal(QaSeed.MemberPatId, audit.CredentialId);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "memory.recall" && e.ActorUserId == QaSeed.Member, ct));
    }
}
