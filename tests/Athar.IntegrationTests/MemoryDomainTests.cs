using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;

namespace Athar.IntegrationTests;

/// <summary>T017 memory domain (FR-MM-01..04) over MCP and REST, as the qa seed's users and PATs.</summary>
[Collection(AtharCollection.Name)]
public class MemoryDomainTests(AtharApp athar) : IAsyncLifetime
{
    private HttpClient _http = null!;
    private McpClient _member = null!, _owner = null!, _admin = null!;
    private readonly CancellationToken _ct = AtharApp.Timeout();

    public async Task InitializeAsync()
    {
        _http = athar.Web();
        var endpoint = new Uri(_http.BaseAddress!, "/mcp");
        _member = await Mcp.Connect(_http, endpoint, QaSeed.MemberPat, _ct);
        _owner = await Mcp.Connect(_http, endpoint, QaSeed.KnowledgeOwnerPat, _ct);
        _admin = await Mcp.Connect(_http, endpoint, QaSeed.AdminPat, _ct);
    }

    public async Task DisposeAsync()
    {
        await _member.DisposeAsync();
        await _owner.DisposeAsync();
        await _admin.DisposeAsync();
        _http.Dispose();
    }

    [Fact]
    public async Task AT_22_personal_memory_is_invisible_to_other_users_and_their_agents()
    {
        var id = (await Remember(_member, "personal", "Personal note on kettle descaling", "Descale the kettle monthly.")).Str("memory_id");

        var recall = await Mcp.Call(_admin, "recall", new() { ["query"] = "kettle descaling" }, _ct);
        Assert.DoesNotContain(Items(recall), i => i.Str("id") == id);
        Assert.Contains("not_found", await Mcp.CallError(_admin, "update_memory", Update(id, 1, "Hijacked"), _ct));
        Assert.Contains(Items(await Mcp.Call(_member, "recall", new() { ["query"] = "kettle descaling" }, _ct)), i => i.Str("id") == id);
    }

    [Fact]
    public async Task AT_23_non_owner_company_writes_propose_and_owner_writes_publish()
    {
        Assert.Equal("proposed", (await Remember(_member, "company", "Deploys freeze on Fridays", "No deploys after Friday noon.")).Str("result"));
        var published = await Remember(_owner, "company", "Release notes are bilingual", "Every release note ships in Arabic and English.");
        Assert.Equal("published", published.Str("result"));

        // An edit of a company memory by a non-owner becomes a replacement proposal against the revision read.
        var id = published.Str("memory_id");
        var edit = await Mcp.Call(_member, "update_memory", Update(id, 1, "Release notes are bilingual (AR first)"), _ct);
        Assert.Equal("proposed", edit.Str("result"));
        await using var db = athar.Db(QaSeed.Org);
        var p = await db.MemoryProposals.SingleAsync(x => x.Id == Guid.Parse(edit.Str("proposal_id")), _ct);
        Assert.Equal((ProposalKind.Replace, 1, ProposalStatus.Pending), (p.Kind, p.BaseRevision!.Value, p.Status));

        // The owner's own edit publishes revision 2; a stale expected_revision is a conflict.
        Assert.Equal(2, (await Mcp.Call(_owner, "update_memory", Update(id, 1, "Release notes: Arabic and English"), _ct)).GetProperty("revision").GetInt32());
        Assert.Contains("conflict_revision", await Mcp.CallError(_owner, "update_memory", Update(id, 1, "Stale"), _ct));
        Assert.Equal(2, await db.MemoryVersions.CountAsync(v => v.MemoryId == Guid.Parse(id), _ct));
    }

    [Fact]
    public async Task AT_24_proposal_races_and_reviewer_rechecks()
    {
        // Double approve: exactly one wins.
        var p1 = await Propose("Incident reviews within 48 hours");
        var twice = await Task.WhenAll(Approve(p1), Approve(p1));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], twice.Select(r => r.Status).Order());

        // Approve vs reject: exactly one wins.
        var p2 = await Propose("On-call hands over at 09:00");
        var race = await Task.WhenAll(Approve(p2), Mcp.Post(_http, $"/api/v1/proposals/{p2}/reject", QaSeed.KnowledgeOwnerPat, new { note = "duplicate" }, _ct));
        Assert.Single(race, r => r.Status == HttpStatusCode.OK);
        Assert.Single(race, r => r.Status == HttpStatusCode.Conflict);

        // Target changed since the base: needs_rereview, and the target keeps the owner's revision.
        var target = (await Remember(_owner, "company", "Backups run nightly", "Backups run at 02:00.")).Str("memory_id");
        var replace = (await Mcp.Call(_member, "update_memory", Update(target, 1, "Backups run hourly"), _ct)).Str("proposal_id");
        await Mcp.Call(_owner, "update_memory", Update(target, 1, "Backups run at 03:00"), _ct);
        var stale = await Approve(replace);
        Assert.Equal(HttpStatusCode.OK, stale.Status);
        Assert.Equal("needs_rereview", stale.Body.Str("status"));

        // A deactivated knowledge owner is denied; a non-owner is forbidden.
        var p3 = await Propose("Coffee machine policy");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Mcp.Post(_http, $"/api/v1/proposals/{p3}/approve", QaSeed.DeactivatedPat, null, _ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mcp.Post(_http, $"/api/v1/proposals/{p3}/approve", QaSeed.MemberPat, null, _ct)).Status);
    }

    [Fact]
    public async Task AT_25_shadowing_rules_staleness_and_notification()
    {
        var convention = (await Remember(_owner, "company", "Branches use kebab-case", "Name branches like feature/short-name.", "convention")).Str("memory_id");
        var policy = (await Remember(_owner, "company", "Secrets never in git", "Use the secret store.", "policy")).Str("memory_id");

        Assert.Contains("forbidden", await Mcp.CallError(_member, "shadow_memory", Shadow(convention), _ct));   // member, not admin
        Assert.Contains("not_overridable", await Mcp.CallError(_admin, "shadow_memory", Shadow(policy), _ct)); // policies default non-overridable
        var shadow = await Mcp.Call(_admin, "shadow_memory", Shadow(convention), _ct);
        Assert.Equal(1, shadow.GetProperty("base_revision").GetInt32());
        Assert.Contains("conflict_revision", await Mcp.CallError(_admin, "shadow_memory", Shadow(convention), _ct)); // one active shadow per pair

        // The base changes: the shadow goes stale and the project admin is notified.
        await Mcp.Call(_owner, "update_memory", Update(convention, 1, "Branches use kebab-case with a ticket id"), _ct);
        await using var db = athar.Db(QaSeed.Org);
        var row = await db.MemoryShadows.SingleAsync(s => s.ShadowMemoryId == Guid.Parse(shadow.Str("memory_id")), _ct);
        Assert.True(row.Stale);
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == QaSeed.Admin && n.Kind == "shadow_stale" && n.Ref == shadow.Str("memory_id"), _ct));
    }

    [Fact]
    public async Task AT_52_new_replace_and_rebase()
    {
        // New: approved into revision 1.
        var created = await Approve(await Propose("Postmortems are blameless"));
        Assert.Equal(("approved", 1), (created.Body.Str("status"), created.Body.GetProperty("revision").GetInt32()));
        var id = created.Body.Str("memory_id");

        // Replacement needs its base: the reviewer's base must match, and a moved target sends it to needs_rereview.
        var replace = (await Mcp.Call(_member, "propose_company_memory", new()
        {
            ["title"] = "Postmortems are blameless and public", ["body_md"] = "Publish within a week.", ["type"] = "procedure",
            ["target_memory_id"] = id, ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct));
        Assert.Equal(1, replace.GetProperty("base_revision").GetInt32());
        var proposal = replace.Str("proposal_id");
        Assert.Equal(HttpStatusCode.Conflict, (await Mcp.Post(_http, $"/api/v1/proposals/{proposal}/approve", QaSeed.KnowledgeOwnerPat, new { base_revision = 7 }, _ct)).Status);
        await Mcp.Call(_owner, "update_memory", Update(id, 1, "Postmortems are blameless (v2)"), _ct);
        Assert.Equal("needs_rereview", (await Approve(proposal)).Body.Str("status"));

        // Rebase: a new linked proposal against the current revision, which then approves into revision 3.
        var rebased = await Mcp.Post(_http, $"/api/v1/proposals/{proposal}/rebase", QaSeed.MemberPat, null, _ct);
        Assert.Equal(HttpStatusCode.OK, rebased.Status);
        Assert.Equal(2, rebased.Body.GetProperty("base_revision").GetInt32());
        await using var db = athar.Db(QaSeed.Org);
        var linked = await db.MemoryProposals.SingleAsync(p => p.Id == Guid.Parse(rebased.Body.Str("proposal_id")), _ct);
        Assert.Equal(Guid.Parse(proposal), linked.RebasedFrom);
        var approved = await Approve(linked.Id.ToString());
        Assert.Equal(("approved", 3), (approved.Body.Str("status"), approved.Body.GetProperty("revision").GetInt32()));
        Assert.Equal("Postmortems are blameless and public", (await db.Memories.SingleAsync(m => m.Id == Guid.Parse(id), _ct)).Title);
    }

    private Task<JsonElement> Remember(McpClient as_, string scope, string title, string body, string type = "decision") =>
        Mcp.Call(as_, "remember", new()
        {
            ["title"] = title, ["body_md"] = body, ["type"] = type, ["scope"] = scope, ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct);

    private async Task<string> Propose(string title) =>
        (await Mcp.Call(_member, "propose_company_memory", new()
        {
            ["title"] = title, ["body_md"] = title + ".", ["type"] = "procedure", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("proposal_id");

    private Task<(HttpStatusCode Status, JsonElement Body)> Approve(string proposalId) =>
        Mcp.Post(_http, $"/api/v1/proposals/{proposalId}/approve", QaSeed.KnowledgeOwnerPat, null, _ct);

    private static Dictionary<string, object?> Update(string id, int expected, string title) => new()
    {
        ["memory_id"] = id, ["expected_revision"] = expected, ["title"] = title, ["op_id"] = Guid.NewGuid().ToString(),
    };

    private static Dictionary<string, object?> Shadow(string companyId) => new()
    {
        ["company_memory_id"] = companyId, ["project_id"] = QaSeed.PilotProject.ToString(), ["body_md"] = "Pilot uses snake_case branches.",
        ["reason"] = "Legacy CI scripts", ["op_id"] = Guid.NewGuid().ToString(),
    };

    private static IEnumerable<JsonElement> Items(JsonElement recall) =>
        recall.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("items").EnumerateArray());
}
