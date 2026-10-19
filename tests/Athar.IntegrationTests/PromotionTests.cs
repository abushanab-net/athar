using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;

namespace Athar.IntegrationTests;

/// <summary>T062: promote (AT-47) and manual conflicts (AT-48), over MCP as agents call them.</summary>
[Collection(AtharCollection.Name)]
public class PromotionTests(AtharApp athar)
{
    [Fact]
    public async Task AT_47_a_selected_section_becomes_a_linked_memory_with_its_classification()
    {
        var ct = AtharApp.Timeout();
        await using var agent = await Connect(QaSeed.AgentPat, ct); // Member, may write pages
        var title = $"Release notes {Guid.NewGuid():N}";
        var page = (await Mcp.Call(agent, "create_page", new() { ["space_id"] = QaSeed.EngWiki, ["title"] = title,
            ["markdown"] = "Releases ship on Tuesdays.\n\nEvery release is tagged vX.Y.Z on main.", ["op_id"] = $"c-{title}" }, ct)).GetProperty("page_id").GetGuid();

        await using var member = await Connect(QaSeed.MemberPat, ct);
        var promote = new Dictionary<string, object?> { ["page_id"] = page, ["selection"] = "Every release is tagged vX.Y.Z on main.", ["scope"] = "project",
            ["owner_id"] = QaSeed.PilotProject, ["type"] = "convention", ["title"] = "Releases are tagged vX.Y.Z", ["op_id"] = $"p1-{title}" };
        var published = await Mcp.Call(member, "promote_to_memory", promote, ct);
        Assert.Equal("published", published.GetProperty("result").GetString());
        var memory = published.GetProperty("memory_id").GetGuid();
        Assert.Equal(memory, (await Mcp.Call(member, "promote_to_memory", promote, ct)).GetProperty("memory_id").GetGuid()); // op_id replay

        // A non-owner promoting to company gets a proposal; a selection that isn't on the page is refused
        var proposed = await Mcp.Call(member, "promote_to_memory", new() { ["page_id"] = page, ["selection"] = "Releases ship on Tuesdays.",
            ["scope"] = "company", ["type"] = "convention", ["title"] = "Releases ship on Tuesdays", ["op_id"] = $"p2-{title}" }, ct);
        Assert.Equal("proposed", proposed.GetProperty("result").GetString());
        Assert.StartsWith("invalid_input", await Refused(member, "promote_to_memory", new() { ["page_id"] = page, ["selection"] = "Not on the page.",
            ["scope"] = "personal", ["type"] = "fact", ["title"] = "x", ["op_id"] = $"p3-{title}" }, ct));

        // From the confidential space, the memory is confidential too (a trusted-local agent of someone who can read it)
        await using var admin = await Connect(QaSeed.AdminTrustedPat, ct);
        var secret = (await Mcp.Call(admin, "promote_to_memory", new() { ["page_id"] = QaSeed.CompensationPage, ["selection"] = "salary bands per level",
            ["scope"] = "personal", ["type"] = "fact", ["title"] = "Salary bands exist per level", ["op_id"] = $"p4-{title}" }, ct)).GetProperty("memory_id").GetGuid();

        await using var db = athar.Db(QaSeed.Org);
        var m = await db.Memories.SingleAsync(x => x.Id == memory, ct);
        Assert.Equal((Scope.Project, "promote", false, "Every release is tagged vX.Y.Z on main."), (m.Scope, m.Origin, m.IsConfidential, m.BodyMd));
        Assert.True(await db.MemoryLinks.AnyAsync(l => l.MemoryId == memory && l.TargetKind == "page" && l.TargetId == page.ToString(), ct));
        var proposal = await db.MemoryProposals.SingleAsync(p => p.Id == proposed.GetProperty("proposal_id").GetGuid(), ct);
        Assert.Equal((ProposalKind.New, ProposalStatus.Pending), (proposal.Kind, proposal.Status));
        Assert.Contains(page.ToString(), proposal.Evidence);
        Assert.True((await db.Memories.SingleAsync(x => x.Id == secret, ct)).IsConfidential);
    }

    [Fact]
    public async Task AT_48_conflicts_need_both_readable_and_publish_rights_or_a_proposal()
    {
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var member = await Connect(QaSeed.MemberPat, ct);
        await using var owner = await Connect(QaSeed.KnowledgeOwnerPat, ct);
        async Task<Guid> Remember(McpClient who, string scope, string body, Guid? ownerId = null) =>
            Guid.Parse((await Mcp.Call(who, "remember", new() { ["title"] = $"{body} {tag}", ["body_md"] = body, ["type"] = "convention", ["scope"] = scope,
                ["owner_id"] = ownerId, ["op_id"] = $"r-{body}-{tag}" }, ct)).GetProperty("memory_id").GetString()!);
        var project = await Remember(member, "project", "Deploys need two approvals", QaSeed.PilotProject);
        var company = await Remember(owner, "company", "Deploys need one approval");

        // The knowledge owner can't read the project memory, so can't open a conflict on it
        Assert.StartsWith("not_found", await Refused(owner, "open_conflict", new() { ["memory_a"] = project, ["memory_b"] = company, ["note"] = "x" }, ct));
        var open = new Dictionary<string, object?> { ["memory_a"] = project, ["memory_b"] = company, ["note"] = "Approvals differ", ["op_id"] = $"o-{tag}" };
        var conflict = (await Mcp.Call(member, "open_conflict", open, ct)).GetProperty("conflict_id").GetGuid();
        Assert.Equal(conflict, (await Mcp.Call(member, "open_conflict", new(open) { ["op_id"] = $"o2-{tag}" }, ct)).GetProperty("conflict_id").GetGuid()); // same pair, still open

        // Keeping the project memory would change the company one: the member can only propose, so the conflict stays open
        var resolved = await Mcp.Call(member, "resolve_conflict", new() { ["conflict_id"] = conflict, ["resolution"] = "keep_a", ["op_id"] = $"k-{tag}" }, ct);
        Assert.Equal("pending_approval", resolved.GetProperty("status").GetString());
        var proposal = resolved.GetProperty("proposal_ids")[0].GetGuid();
        await using (var db = athar.Db(QaSeed.Org))
            Assert.Equal(ConflictStatus.Open, (await db.MemoryConflicts.SingleAsync(c => c.Id == conflict, ct)).Status);

        using (var http = athar.Web())
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", QaSeed.KnowledgeOwnerPat);
            using var approve = await http.PostAsJsonAsync($"/api/v1/proposals/{proposal}/approve", new { }, ct);
            Assert.True(approve.IsSuccessStatusCode, await approve.Content.ReadAsStringAsync(ct));
        }
        await using (var db = athar.Db(QaSeed.Org))
        {
            var settled = await db.MemoryConflicts.SingleAsync(c => c.Id == conflict, ct);
            Assert.Equal((ConflictStatus.Resolved, ConflictResolution.KeepA), (settled.Status, settled.Resolution));
            var c = await db.Memories.SingleAsync(m => m.Id == company, ct);
            Assert.Equal((2, "Deploys need two approvals"), (c.Revision, c.BodyMd)); // the approved proposal applied the kept text
        }

        // Two memories the member can publish: resolved at once, the loser superseded
        var first = await Remember(member, "project", "Hotfixes skip staging", QaSeed.PilotProject);
        var second = await Remember(member, "project", "Hotfixes go through staging", QaSeed.PilotProject);
        var direct = (await Mcp.Call(member, "open_conflict", new() { ["memory_a"] = first, ["memory_b"] = second, ["note"] = "Staging" }, ct)).GetProperty("conflict_id").GetGuid();
        var keepB = new Dictionary<string, object?> { ["conflict_id"] = direct, ["resolution"] = "keep_b", ["op_id"] = $"kb-{tag}" };
        Assert.Equal("resolved", (await Mcp.Call(member, "resolve_conflict", keepB, ct)).GetProperty("status").GetString());
        Assert.Equal("resolved", (await Mcp.Call(member, "resolve_conflict", keepB, ct)).GetProperty("status").GetString()); // op_id replay
        await using (var db = athar.Db(QaSeed.Org))
            Assert.Equal((MemoryStatus.Superseded, MemoryStatus.Active),
                (await db.Memories.Where(m => m.Id == first).Select(m => m.Status).SingleAsync(ct), await db.Memories.Where(m => m.Id == second).Select(m => m.Status).SingleAsync(ct)));
    }

    private Task<McpClient> Connect(string pat, CancellationToken ct) =>
        Mcp.Connect(athar.Web(), new Uri(athar.App.GetEndpoint("web", "https"), "/mcp"), pat, ct);

    /// <summary>The refused call's error, from its §6.6 code on (the SDK prefixes "An error occurred invoking 'tool': ").</summary>
    private static async Task<string> Refused(McpClient mcp, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await mcp.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.True(result.IsError == true, $"{tool} should have been refused");
        var text = string.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        var i = text.IndexOf("': ", StringComparison.Ordinal);
        return i < 0 ? text : text[(i + 3)..];
    }
}
