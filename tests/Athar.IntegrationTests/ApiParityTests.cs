using System.Text.Json;
using Athar.Data.Seed;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Athar.IntegrationTests;

/// <summary>T054 (FR-AG-01..03): every brain tool has a REST equivalent with the same results and codes; OpenAPI; resources, prompts, protocol pin.</summary>
[Collection(AtharCollection.Name)]
public class ApiParityTests(AtharApp athar)
{
    [Fact]
    public async Task FR_AG_02_rest_equivalents_of_the_brain_tools()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();
        var op = Guid.NewGuid().ToString();
        var remember = new { title = "Parity probe walrus", body_md = "Walrus parity check.", type = "fact", scope = "project", owner_id = QaSeed.PilotProject, op_id = op };

        var created = await Mcp.Post(http, "/api/v1/memories", QaSeed.MemberPat, remember, ct);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("published", created.Body.Str("result"));
        var id = created.Body.Str("memory_id");
        Assert.Equal(id, (await Mcp.Post(http, "/api/v1/memories", QaSeed.MemberPat, remember, ct)).Body.Str("memory_id")); // same op_id → stored result
        Assert.Equal("op_id_reused", (await Mcp.Post(http, "/api/v1/memories", QaSeed.MemberPat, remember with { title = "x" }, ct)).Body.Str("code"));

        var updated = await Send(http, HttpMethod.Patch, $"/api/v1/memories/{id}", QaSeed.MemberPat, new { expected_revision = 1, body_md = "Walrus parity, revised.", op_id = Guid.NewGuid().ToString() }, ct);
        Assert.Equal(("published", 2), (updated.Body.Str("result"), updated.Body.GetProperty("revision").GetInt32()));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(http, HttpMethod.Patch, $"/api/v1/memories/{id}", QaSeed.MemberPat, new { expected_revision = 1, title = "stale", op_id = Guid.NewGuid().ToString() }, ct)).Status);

        var recall = await Mcp.Post(http, "/api/v1/recall", QaSeed.MemberPat, new { query = "walrus parity", project_ids = new[] { QaSeed.PilotProject } }, ct);
        Assert.Contains(recall.Body.GetProperty("groups")[0].GetProperty("items").EnumerateArray(), i => i.Str("id") == id);

        var company = (await Mcp.Post(http, "/api/v1/memories", QaSeed.KnowledgeOwnerPat, new { title = "Parity convention", body_md = "Use parity.", type = "convention", scope = "company", op_id = Guid.NewGuid().ToString() }, ct)).Body.Str("memory_id");
        var shadow = await Mcp.Post(http, $"/api/v1/memories/{company}/shadow", QaSeed.AdminPat, new { project_id = QaSeed.PilotProject, body_md = "Pilot parity.", reason = "test", op_id = Guid.NewGuid().ToString() }, ct);
        Assert.Equal(1, shadow.Body.GetProperty("base_revision").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await Mcp.Post(http, $"/api/v1/memories/{company}/shadow", QaSeed.MemberPat, new { project_id = QaSeed.PilotProject, body_md = "x", reason = "x", op_id = Guid.NewGuid().ToString() }, ct)).Status);

        var proposal = await Mcp.Post(http, "/api/v1/proposals", QaSeed.MemberPat, new { title = "Parity proposal", body_md = "Proposed.", type = "fact", op_id = Guid.NewGuid().ToString() }, ct);
        Assert.True(Guid.TryParse(proposal.Body.Str("proposal_id"), out _));

        var resolved = await Mcp.Get(http, "/api/v1/projects/resolve?repo_remote=git@github.com:abushanab-net/athar.git", QaSeed.MemberPat, ct);
        Assert.Equal(QaSeed.PilotProject.ToString(), resolved.Body.GetProperty("projects")[0].Str("project_id"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Mcp.Get(http, "/api/v1/projects/resolve?repo_remote=nope", QaSeed.MemberPat, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/projects/resolve?repo_remote=x", ct)).StatusCode);
    }

    [Fact]
    public async Task FR_AG_02_rest_equivalents_of_the_page_tools()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();
        var pat = QaSeed.MemberPat; // EngWiki editor; the space's AI policy allows agent writes

        var created = await Mcp.Post(http, "/api/v1/pages", pat, new { space_id = QaSeed.EngWiki, title = "REST parity page", markdown = "First line.", op_id = Guid.NewGuid().ToString() }, ct);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var id = created.Body.Str("page_id");
        Assert.Equal(HttpStatusCode.OK, (await Mcp.Post(http, $"/api/v1/pages/{id}/append", pat, new { markdown = "Second line.", op_id = Guid.NewGuid().ToString() }, ct)).Status);

        var page = await Mcp.Get(http, $"/api/v1/pages/{id}", pat, ct);
        Assert.Contains("Second line.", page.Body.Str("content"));
        var replaced = await Send(http, HttpMethod.Put, $"/api/v1/pages/{id}", pat, new { markdown = "Replaced.", expected_live_token = page.Body.Str("live_token"), op_id = Guid.NewGuid().ToString() }, ct);
        Assert.Equal(HttpStatusCode.OK, replaced.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(http, HttpMethod.Put, $"/api/v1/pages/{id}", pat, new { markdown = "Stale.", expected_live_token = page.Body.Str("live_token"), op_id = Guid.NewGuid().ToString() }, ct)).Status);

        var comment = await Mcp.Post(http, $"/api/v1/pages/{id}/comments", pat, new { body_md = "Looks good.", op_id = Guid.NewGuid().ToString() }, ct);
        Assert.True(Guid.TryParse(comment.Body.Str("thread_id"), out _));
        var list = await Mcp.Get(http, $"/api/v1/spaces/{QaSeed.EngWiki}/pages", pat, ct);
        Assert.Contains(list.Body.GetProperty("pages").EnumerateArray(), p => p.Str("page_id") == id);

        // delete_page needs the pages:delete scope, which this token lacks.
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/pages/{id}?reason=cleanup&op_id={Guid.NewGuid()}");
        delete.Headers.Add("Authorization", $"Bearer {pat}");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(delete, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Mcp.Get(http, $"/api/v1/pages/{QaSeed.CompensationPage}", pat, ct)).Status);
    }

    [Fact]
    public async Task FR_AG_02_openapi_documents_api_v1_in_snake_case()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();
        using var doc = JsonDocument.Parse(await http.GetStringAsync("/openapi/v1.json", ct));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/recall", "/api/v1/memories", "/api/v1/memories/{id}", "/api/v1/memories/{id}/shadow", "/api/v1/proposals", "/api/v1/projects/resolve", "/api/v1/search",
                     "/api/v1/pages", "/api/v1/pages/{id}", "/api/v1/pages/{id}/append", "/api/v1/pages/{id}/comments", "/api/v1/spaces/{spaceId}/pages" })
            Assert.True(paths.TryGetProperty(path, out _), path);
        Assert.DoesNotContain(paths.EnumerateObject(), p => !p.Name.StartsWith("/api/v1/"));
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.GetProperty("RememberBody").GetProperty("properties").TryGetProperty("body_md", out _));
        Assert.True(schemas.GetProperty("RecallResult").GetProperty("properties").TryGetProperty("degraded", out _));
        Assert.True(schemas.GetProperty("RecallItem").GetProperty("properties").TryGetProperty("snippet", out _));
    }

    [Fact]
    public async Task FR_AG_01_resources_prompts_and_protocol_pin()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();
        var endpoint = new Uri(http.BaseAddress!, "/mcp");
        await using var member = await Mcp.Connect(http, endpoint, QaSeed.MemberPat, ct);
        await using var owner = await Mcp.Connect(http, endpoint, QaSeed.KnowledgeOwnerPat, ct);
        Assert.Equal("2025-11-25", member.NegotiatedProtocolVersion);

        var templates = await member.ListResourceTemplatesAsync(cancellationToken: ct);
        Assert.Contains(templates, t => t.UriTemplate == "athar://memory/{id}");
        Assert.Contains(templates, t => t.UriTemplate == "athar://page/{id}");

        // Authorized like the tools: the member's personal memory, but not another user's; a readable page, not a hidden one.
        var mine = await member.ReadResourceAsync($"athar://memory/{QaSeed.MemoryPersonal}", cancellationToken: ct);
        Assert.Contains("I prefer small PRs", Text(mine));
        await Assert.ThrowsAnyAsync<Exception>(async () => await owner.ReadResourceAsync($"athar://memory/{QaSeed.MemoryPersonal}", cancellationToken: ct));
        Assert.Contains("Deploy runbook", Text(await member.ReadResourceAsync($"athar://page/{QaSeed.DeployRunbookPage}", cancellationToken: ct)));
        await Assert.ThrowsAnyAsync<Exception>(async () => await member.ReadResourceAsync($"athar://page/{QaSeed.CompensationPage}", cancellationToken: ct));

        var prompts = await member.ListPromptsAsync(cancellationToken: ct);
        Assert.Contains(prompts, p => p.Name == "athar-context");
        var brief = await member.GetPromptAsync("athar-context", new Dictionary<string, object?> { ["repo_remote"] = "git@github.com:abushanab-net/athar.git" }, cancellationToken: ct);
        var text = string.Join("\n", brief.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("Athar context for athar", text);
        Assert.Contains($"Project {QaSeed.PilotProject}", text);
    }

    private static string Text(ReadResourceResult r) => string.Join("\n", r.Contents.OfType<TextResourceContents>().Select(c => c.Text));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient http, HttpMethod method, string path, string pat, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = System.Net.Http.Json.JsonContent.Create(body) };
        request.Headers.Add("Authorization", $"Bearer {pat}");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, text.Length == 0 ? default
            : text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone()
            : throw new InvalidOperationException($"{(int)response.StatusCode}: {text[..Math.Min(text.Length, 2000)]}"));
    }
}
