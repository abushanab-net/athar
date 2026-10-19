using System.Text.Json;
using Athar.Data.Seed;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;

namespace Athar.IntegrationTests;

/// <summary>T053 recall per project group (FR-SR-03..04): replace in place, withheld_override, dedupe, annotations, audit.</summary>
[Collection(AtharCollection.Name)]
public class RecallTests(AtharApp athar) : IAsyncLifetime
{
    private HttpClient _http = null!;
    private McpClient _member = null!, _trusted = null!, _owner = null!, _admin = null!;
    private readonly CancellationToken _ct = AtharApp.Timeout();

    public async Task InitializeAsync()
    {
        _http = athar.Web();
        var endpoint = new Uri(_http.BaseAddress!, "/mcp");
        _member = await Mcp.Connect(_http, endpoint, QaSeed.MemberPat, _ct);
        _trusted = await Mcp.Connect(_http, endpoint, QaSeed.MemberTrustedPat, _ct);
        _owner = await Mcp.Connect(_http, endpoint, QaSeed.KnowledgeOwnerPat, _ct);
        _admin = await Mcp.Connect(_http, endpoint, QaSeed.AdminPat, _ct);
    }

    public async Task DisposeAsync()
    {
        foreach (var c in new[] { _member, _trusted, _owner, _admin }) await c.DisposeAsync();
        _http.Dispose();
    }

    [Fact]
    public async Task AT_26_recall_replaces_in_place_and_withholds_confidential_overrides()
    {
        const string query = "zephyr release windows";
        var company = (await Mcp.Call(_owner, "remember", new()
        {
            ["title"] = "Zephyr release windows", ["body_md"] = "Zephyr releases ship on Tuesdays and Thursdays.", ["type"] = "convention",
            ["scope"] = "company", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("memory_id");
        // The shadow shares no words with the query: it can only appear by replacing its base.
        var shadow = (await Mcp.Call(_admin, "shadow_memory", new()
        {
            ["company_memory_id"] = company, ["project_id"] = QaSeed.PilotProject.ToString(), ["body_md"] = "The pilot ships daily from main.",
            ["reason"] = "pilot cadence", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("memory_id");

        // The pilot's group: the shadow takes the base's place, the company version never appears. (Positions aren't
        // asserted: a just-written memory has no vectors yet, so its fused rank depends on what else the shared DB holds.)
        var pilot = Group(await Recall(_member, query, QaSeed.PilotProject), QaSeed.PilotProject);
        var first = Assert.Single(pilot, i => i.Str("id") == shadow); // once, at the higher rank
        Assert.Equal(company, first.GetProperty("annotations").GetProperty("override").Str("base_id"));
        Assert.False(first.GetProperty("annotations").GetProperty("override").GetProperty("stale").GetBoolean());
        Assert.True(first.GetProperty("annotations").GetProperty("binding").GetBoolean());
        Assert.DoesNotContain(pilot, i => i.Str("id") == company);

        // Another project without an override sees the company version.
        Assert.Contains(Group(await Recall(_owner, query, QaSeed.BillingProject), QaSeed.BillingProject), i => i.Str("id") == company);

        // The base changes: the override is flagged stale.
        await Mcp.Call(_owner, "update_memory", new()
        {
            ["memory_id"] = company, ["expected_revision"] = 1, ["body_md"] = "Zephyr releases ship on Tuesdays only.", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct);
        Assert.True(Group(await Recall(_member, query, QaSeed.PilotProject), QaSeed.PilotProject).Single(i => i.Str("id") == shadow)
            .GetProperty("annotations").GetProperty("override").GetProperty("stale").GetBoolean());

        // A confidential shadow through an external-agent credential: withheld_override, never the company version.
        Assert.Equal(HttpStatusCode.OK, (await Mcp.Post(_http, $"/api/v1/memories/{shadow}/classify", QaSeed.MemberTrustedPat, new { confidential = true }, _ct)).Status);
        var withheld = await Recall(_member, query, QaSeed.PilotProject);
        var marker = Assert.Single(Group(withheld, QaSeed.PilotProject), i => i.Str("id") == company);
        Assert.Equal("withheld_override", marker.Str("kind"));
        Assert.True(marker.GetProperty("annotations").GetProperty("withheld_override").GetBoolean());
        Assert.DoesNotContain(shadow, withheld.GetRawText());
        Assert.DoesNotContain("Zephyr releases ship", withheld.GetRawText());
        var trusted = Group(await Recall(_trusted, query, QaSeed.PilotProject), QaSeed.PilotProject);
        Assert.Contains(trusted, i => i.Str("id") == shadow);
        Assert.DoesNotContain(trusted, i => i.Str("id") == company);

        // Audited with the query and the result IDs.
        await using var db = athar.Db(QaSeed.Org);
        var audits = await db.AuditEvents.Where(e => e.Action == "memory.recall" && e.ActorUserId == QaSeed.Member).ToListAsync(_ct);
        Assert.Contains(audits, e => e.After!.Contains(query) && e.After.Contains(company));
    }

    [Fact]
    public async Task FR_SR_04_conflict_and_pending_annotations()
    {
        // The seeded open conflict: both sides are visible to the member, so the other side is named.
        var items = Group(await Recall(_member, "session transcripts kept days", null), null);
        var fact = Assert.Single(items, i => i.Str("id") == QaSeed.MemoryCompanyFact.ToString());
        Assert.Equal(QaSeed.MemoryConflicting.ToString(), fact.GetProperty("annotations").GetProperty("conflict").Str("other_id"));

        // The caller's own pending proposal on a memory.
        var target = (await Mcp.Call(_owner, "remember", new()
        {
            ["title"] = "Quokka naming rules", ["body_md"] = "Quokka services use kebab-case.", ["type"] = "fact", ["scope"] = "company", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("memory_id");
        await Mcp.Call(_member, "update_memory", new()
        {
            ["memory_id"] = target, ["expected_revision"] = 1, ["body_md"] = "Quokka services use snake_case.", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct);
        var mine = Assert.Single(Group(await Recall(_member, "quokka naming", null), null), i => i.Str("id") == target);
        Assert.True(mine.GetProperty("annotations").GetProperty("pending").GetBoolean());
        var theirs = Assert.Single(Group(await Recall(_admin, "quokka naming", null), null), i => i.Str("id") == target);
        Assert.False(theirs.GetProperty("annotations").TryGetProperty("pending", out _)); // only the proposer's own
    }

    private Task<JsonElement> Recall(McpClient as_, string query, Guid? project) => Mcp.Call(as_, "recall", project is { } p
        ? new() { ["query"] = query, ["project_ids"] = new[] { p.ToString() } }
        : new() { ["query"] = query }, _ct);

    private static List<JsonElement> Group(JsonElement recall, Guid? project) =>
        [.. recall.GetProperty("groups").EnumerateArray()
            .Single(g => project is { } p ? g.TryGetProperty("project_id", out var id) && id.GetString() == p.ToString() : !g.TryGetProperty("project_id", out _))
            .GetProperty("items").EnumerateArray()];
}
