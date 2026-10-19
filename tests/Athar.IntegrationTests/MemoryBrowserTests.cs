using System.Text.Json;
using Athar.Data.Seed;
using ModelContextProtocol.Client;

namespace Athar.IntegrationTests;

/// <summary>T019 browser, queue and inbox APIs (FR-MM-09): filters, history, archive, link, proposal diff inputs.</summary>
[Collection(AtharCollection.Name)]
public class MemoryBrowserTests(AtharApp athar) : IAsyncLifetime
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
    public async Task FR_MM_09_filters_history_and_visibility()
    {
        var personal = await Remember(_member, "personal", null, "Browser personal note");
        var project = await Remember(_member, "project", QaSeed.PilotProject, "Browser project note");

        var byProject = await List(QaSeed.MemberPat, $"scope=project&project_id={QaSeed.PilotProject}");
        Assert.Contains(byProject, i => i.Str("id") == project);
        Assert.DoesNotContain(byProject, i => i.Str("id") == personal);
        Assert.DoesNotContain(await List(QaSeed.AdminPat, "scope=personal&take=100"), i => i.Str("id") == personal);
        Assert.Equal(HttpStatusCode.NotFound, (await Mcp.Get(_http, $"/api/v1/memories/{personal}", QaSeed.AdminPat, _ct)).Status);

        await Mcp.Call(_member, "update_memory", new()
        {
            ["memory_id"] = project, ["expected_revision"] = 1, ["body_md"] = "Edited.", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct);
        var detail = await Mcp.Get(_http, $"/api/v1/memories/{project}", QaSeed.MemberPat, _ct);
        Assert.Equal([2, 1], detail.Body.GetProperty("history").EnumerateArray().Select(v => v.GetProperty("revision").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Mcp.Get(_http, "/api/v1/memories?scope=galaxy", QaSeed.MemberPat, _ct)).Status);
    }

    [Fact]
    public async Task FR_MM_03_archiving_a_base_leaves_its_shadow_as_a_project_memory()
    {
        var company = await Remember(_owner, "company", null, "Browser archived base", "convention");
        var shadow = (await Mcp.Call(_admin, "shadow_memory", new()
        {
            ["company_memory_id"] = company, ["project_id"] = QaSeed.PilotProject.ToString(), ["body_md"] = "Pilot differs.",
            ["reason"] = "test", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("memory_id");
        await Mcp.Call(_owner, "update_memory", new()
        {
            ["memory_id"] = company, ["expected_revision"] = 1, ["title"] = "Browser archived base v2", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct);
        var stale = await List(QaSeed.AdminPat, "stale=true&take=100");
        Assert.Equal(company, Assert.Single(stale, i => i.Str("id") == shadow).Str("shadow_of"));

        // Only someone who can publish archives; the owner's archive keeps the shadow as a plain project memory.
        Assert.Equal(HttpStatusCode.Forbidden, (await Mcp.Post(_http, $"/api/v1/memories/{company}/archive", QaSeed.MemberPat, null, _ct)).Status);
        var archived = await Mcp.Post(_http, $"/api/v1/memories/{company}/archive", QaSeed.KnowledgeOwnerPat, new { reason = "obsolete" }, _ct);
        Assert.Equal("archived", archived.Body.GetProperty("memory").Str("status"));
        Assert.Contains(await List(QaSeed.KnowledgeOwnerPat, "status=archived&scope=company&take=100"), i => i.Str("id") == company);
        var plain = (await Mcp.Get(_http, $"/api/v1/memories/{shadow}", QaSeed.AdminPat, _ct)).Body.GetProperty("memory");
        Assert.Equal(("active", false), (plain.Str("status"), plain.TryGetProperty("shadow_of", out _)));

        var linked = await Mcp.Post(_http, $"/api/v1/memories/{shadow}/links", QaSeed.AdminPat, new { target = "https://example.test/ci" }, _ct);
        Assert.Contains("https://example.test/ci", linked.Body.GetProperty("links").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task FR_MM_09_proposal_queue_carries_diff_inputs_and_inbox_shell()
    {
        var target = await Remember(_owner, "company", null, "Browser queue target");
        var proposal = (await Mcp.Call(_member, "update_memory", new()
        {
            ["memory_id"] = target, ["expected_revision"] = 1, ["body_md"] = "Proposed body.", ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("proposal_id");

        var queue = await Mcp.Get(_http, "/api/v1/proposals", QaSeed.KnowledgeOwnerPat, _ct);
        var row = Assert.Single(queue.Body.EnumerateArray(), p => p.Str("id") == proposal);
        Assert.Equal(("replace", "pending", 1), (row.Str("kind"), row.Str("status"), row.GetProperty("base_revision").GetInt32()));
        Assert.Equal("Proposed body.", row.GetProperty("proposed").Str("body_md"));
        Assert.Equal(row.GetProperty("base").Str("body_md"), row.GetProperty("current").Str("body_md"));

        Assert.Contains((await Mcp.Get(_http, "/api/v1/proposals", QaSeed.MemberPat, _ct)).Body.EnumerateArray(), p => p.Str("id") == proposal);
        Assert.DoesNotContain((await Mcp.Get(_http, "/api/v1/proposals", QaSeed.AdminPat, _ct)).Body.EnumerateArray(), p => p.Str("id") == proposal);

        var inbox = await Mcp.Get(_http, "/api/v1/inbox", QaSeed.MemberPat, _ct);
        Assert.Equal((HttpStatusCode.OK, JsonValueKind.Array), (inbox.Status, inbox.Body.ValueKind));
    }

    private async Task<string> Remember(McpClient as_, string scope, Guid? owner, string title, string type = "fact") =>
        (await Mcp.Call(as_, "remember", new()
        {
            ["title"] = title, ["body_md"] = title + ".", ["type"] = type, ["scope"] = scope,
            ["owner_id"] = owner?.ToString(), ["op_id"] = Guid.NewGuid().ToString(),
        }, _ct)).Str("memory_id");

    private async Task<List<JsonElement>> List(string pat, string query)
    {
        var r = await Mcp.Get(_http, $"/api/v1/memories?{query}", pat, _ct);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return [.. r.Body.GetProperty("items").EnumerateArray()];
    }
}
