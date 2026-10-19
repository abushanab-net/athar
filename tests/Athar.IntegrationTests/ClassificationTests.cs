using System.Text.Json;
using Athar.Data.Seed;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;

namespace Athar.IntegrationTests;

/// <summary>
/// T050, AT-34 (memory side): external-agent credentials never receive confidential memories or anything derived from
/// them (recall items, proposals, shadows, op replays, provenance links), and declassifying is the publisher's, with a reason.
/// </summary>
[Collection(AtharCollection.Name)]
public class ClassificationTests(AtharApp athar) : IAsyncLifetime
{
    private HttpClient _http = null!;
    private McpClient _external = null!, _trusted = null!, _owner = null!, _admin = null!;
    private readonly CancellationToken _ct = AtharApp.Timeout();

    public async Task InitializeAsync()
    {
        _http = athar.Web();
        var endpoint = new Uri(_http.BaseAddress!, "/mcp");
        _external = await Mcp.Connect(_http, endpoint, QaSeed.MemberPat, _ct);
        _trusted = await Mcp.Connect(_http, endpoint, QaSeed.MemberTrustedPat, _ct);
        _owner = await Mcp.Connect(_http, endpoint, QaSeed.KnowledgeOwnerPat, _ct);
        _admin = await Mcp.Connect(_http, endpoint, QaSeed.AdminPat, _ct);
    }

    public async Task DisposeAsync()
    {
        foreach (var c in new[] { _external, _trusted, _owner, _admin }) await c.DisposeAsync();
        _http.Dispose();
    }

    [Fact]
    public async Task AT_34_classifying_a_memory_hides_it_and_what_derives_from_it()
    {
        var company = await Remember(_owner, "company", "Quarterly pricing review", "convention");
        var proposal = (await Mcp.Call(_external, "update_memory", Update(company, 1, "Quarterly pricing review (draft)"), _ct)).Str("proposal_id");
        var shadow = (await Mcp.Call(_admin, "shadow_memory", new()
        {
            ["company_memory_id"] = company, ["project_id"] = QaSeed.PilotProject.ToString(), ["body_md"] = "Pilot reviews monthly.",
            ["reason"] = "pilot", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("memory_id");

        var classified = await Mcp.Post(_http, $"/api/v1/memories/{company}/classify", QaSeed.KnowledgeOwnerPat, new { confidential = true }, _ct);
        Assert.True(classified.Body.GetProperty("is_confidential").GetBoolean());

        // External agents: no item, no title, no ID, and no way to act on it.
        var recall = await Mcp.Call(_external, "recall", new() { ["query"] = "quarterly pricing review" }, _ct);
        Assert.DoesNotContain(company, recall.GetRawText());
        Assert.DoesNotContain("Quarterly pricing review", recall.GetRawText());
        Assert.Equal(HttpStatusCode.NotFound, (await Mcp.Get(_http, $"/api/v1/memories/{company}", QaSeed.MemberPat, _ct)).Status);
        Assert.Contains("not_found", await Mcp.CallError(_external, "update_memory", Update(company, 1, "x"), _ct));
        Assert.Contains("not_found", await Mcp.CallError(_external, "propose_company_memory", new()
        {
            ["title"] = "x", ["body_md"] = "x", ["type"] = "fact", ["target_memory_id"] = company, ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct));
        Assert.DoesNotContain(proposal, (await Mcp.Get(_http, "/api/v1/proposals", QaSeed.MemberPat, _ct)).Body.GetRawText());
        Assert.Equal(HttpStatusCode.NotFound, (await Mcp.Post(_http, $"/api/v1/proposals/{proposal}/approve", QaSeed.KnowledgeOwnerPat, null, _ct)).Status);

        // Derived items inherit the classification: the open proposal and the project shadow.
        await using var db = athar.Db(QaSeed.Org);
        Assert.True((await db.MemoryProposals.SingleAsync(p => p.Id == Guid.Parse(proposal), _ct)).IsConfidential);
        Assert.True((await db.Memories.SingleAsync(m => m.Id == Guid.Parse(shadow), _ct)).IsConfidential);
        Assert.True(await db.Chunks.Where(c => c.OwnerId == Guid.Parse(shadow)).AllAsync(c => c.IsConfidential, _ct));

        // The same member through a trusted-local token still sees it, and their own proposal.
        Assert.Contains(company, (await Mcp.Call(_trusted, "recall", new() { ["query"] = "quarterly pricing review" }, _ct)).GetRawText());
        Assert.Contains(proposal, (await Mcp.Get(_http, "/api/v1/proposals", QaSeed.MemberTrustedPat, _ct)).Body.GetRawText());

        // Declassifying: the publisher (a knowledge owner for company), with a stated reason, audited.
        Assert.Equal(HttpStatusCode.Forbidden, (await Mcp.Post(_http, $"/api/v1/memories/{company}/classify", QaSeed.MemberTrustedPat, new { confidential = false, reason = "no" }, _ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Mcp.Post(_http, $"/api/v1/memories/{company}/classify", QaSeed.KnowledgeOwnerTrustedPat, new { confidential = false }, _ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Mcp.Post(_http, $"/api/v1/memories/{company}/classify", QaSeed.KnowledgeOwnerTrustedPat, new { confidential = false, reason = "Published in the handbook" }, _ct)).Status);
        Assert.Contains(company, (await Mcp.Call(_external, "recall", new() { ["query"] = "quarterly pricing review" }, _ct)).GetRawText());
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "memory.declassify" && e.ResourceId == company, _ct);
        Assert.Contains("Published in the handbook", audit.After);
    }

    [Fact]
    public async Task AT_34_op_replay_gets_a_fresh_disclosure_check()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Replay probe", ["body_md"] = "Personal note.", ["type"] = "fact", ["scope"] = "personal", ["op_id"] = Guid.NewGuid().ToString(),
        };
        var id = (await Mcp.Call(_external, "remember", args, _ct)).Str("memory_id");
        Assert.Equal(HttpStatusCode.OK, (await Mcp.Post(_http, $"/api/v1/memories/{id}/classify", QaSeed.MemberTrustedPat, new { confidential = true }, _ct)).Status);

        // The external token repeating its own write no longer gets the (now confidential) memory's ID back.
        Assert.Contains("not_found", await Mcp.CallError(_external, "remember", args, _ct));
        Assert.Equal(id, (await Mcp.Call(_trusted, "remember", args, _ct)).Str("memory_id"));
    }

    [Fact]
    public async Task AT_34_provenance_never_carries_a_confidential_page_id()
    {
        var page = QaSeed.CompensationPage.ToString();
        var recall = await Mcp.Call(_external, "recall", new() { ["query"] = "session transcripts kept" }, _ct);
        Assert.Contains(QaSeed.MemoryCompanyFact.ToString(), recall.GetRawText());
        Assert.DoesNotContain(page, recall.GetRawText());
        Assert.DoesNotContain(page, (await Mcp.Get(_http, $"/api/v1/memories/{QaSeed.MemoryCompanyFact}", QaSeed.MemberPat, _ct)).Body.GetRawText());
    }

    private async Task<string> Remember(McpClient as_, string scope, string title, string type) =>
        (await Mcp.Call(as_, "remember", new()
        {
            ["title"] = title, ["body_md"] = title + ".", ["type"] = type, ["scope"] = scope, ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("memory_id");

    private static Dictionary<string, object?> Update(string id, int expected, string title) => new()
    {
        ["memory_id"] = id, ["expected_revision"] = expected, ["title"] = title, ["op_id"] = Guid.NewGuid().ToString(),
    };
}
