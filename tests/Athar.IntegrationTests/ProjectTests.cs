using System.Text.Json;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T018 projects (FR-PJ-01..02): resolve_project, membership and links, recall narrowed by repo_remote.</summary>
[Collection(AtharCollection.Name)]
public class ProjectTests(AtharApp athar)
{
    [Theory]
    [InlineData("git@github.com:Abushanab-Net/athar.git")]
    [InlineData("https://github.com/abushanab-net/athar")]
    [InlineData("ssh://git@github.com:22/abushanab-net/athar.git/")]
    [InlineData("https://token@github.com/abushanab-net/athar.git")]
    public void Remotes_normalize_to_one_spelling(string remote) =>
        Assert.Equal(QaSeed.PilotRemote, ProjectRules.NormalizeRemote(remote));

    [Fact]
    public async Task FR_PJ_02_resolve_project_returns_member_projects_only()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();
        var endpoint = new Uri(http.BaseAddress!, "/mcp");
        await using var member = await Mcp.Connect(http, endpoint, QaSeed.MemberPat, ct);
        await using var admin = await Mcp.Connect(http, endpoint, QaSeed.AdminPat, ct);
        await using var outsider = await Mcp.Connect(http, endpoint, QaSeed.KnowledgeOwnerPat, ct);
        var remote = new Dictionary<string, object?> { ["repo_remote"] = "git@github.com:abushanab-net/athar.git" };

        var mine = (await Mcp.Call(member, "resolve_project", remote, ct)).GetProperty("projects");
        var p = Assert.Single(mine.EnumerateArray());
        Assert.Equal((QaSeed.PilotProject.ToString(), "member"), (p.Str("project_id"), p.Str("role")));
        Assert.Equal("admin", (await Mcp.Call(admin, "resolve_project", remote, ct)).GetProperty("projects")[0].Str("role"));
        Assert.Empty((await Mcp.Call(outsider, "resolve_project", remote, ct)).GetProperty("projects").EnumerateArray());

        // recall with repo_remote: one group per resolved project, holding that project's memories.
        var created = await Mcp.Call(member, "remember", new()
        {
            ["title"] = "Pilot builds with dotnet 10", ["body_md"] = "The pilot pins the SDK in global.json.", ["type"] = "fact",
            ["scope"] = "project", ["owner_id"] = QaSeed.PilotProject.ToString(), ["op_id"] = Guid.NewGuid().ToString(),
        }, ct);
        var recall = await Mcp.Call(member, "recall", new() { ["query"] = "pilot sdk", ["repo_remote"] = "https://github.com/abushanab-net/athar" }, ct);
        var group = Assert.Single(recall.GetProperty("groups").EnumerateArray());
        Assert.Equal(QaSeed.PilotProject.ToString(), group.Str("project_id"));
        Assert.Contains(group.GetProperty("items").EnumerateArray(), i => i.Str("id") == created.Str("memory_id"));
    }

    [Fact]
    public async Task FR_PJ_01_projects_members_and_links()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();
        var slug = "p-" + Guid.NewGuid().ToString("N")[..8];

        // Org admins create projects and become their first admin; members can't create.
        Assert.Equal(HttpStatusCode.Forbidden, (await Mcp.Post(http, "/api/v1/projects", QaSeed.MemberPat, new { name = "Nope", slug }, ct)).Status);
        var created = await Mcp.Post(http, "/api/v1/projects", QaSeed.AdminPat, new { name = "Docs site", slug }, ct);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("admin", created.Body.Str("role"));
        var id = created.Body.Str("id");

        Assert.Equal(HttpStatusCode.OK, (await Put(http, $"/api/v1/projects/{id}/members", new { user_id = QaSeed.Member, role = "member" }, ct)));
        Assert.Equal(HttpStatusCode.OK, (await Mcp.Post(http, $"/api/v1/projects/{id}/links", QaSeed.AdminPat, new { repo_remote = $"git@github.com:acme/{slug}.git" }, ct)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Mcp.Post(http, $"/api/v1/projects/{id}/links", QaSeed.AdminPat, new { repo_remote = "x", space_id = QaSeed.EngWiki }, ct)).Status);

        // The new member resolves the project from any spelling of the remote.
        await using var member = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.MemberPat, ct);
        var resolved = await Mcp.Call(member, "resolve_project", new() { ["repo_remote"] = $"https://github.com/ACME/{slug}" }, ct);
        Assert.Equal(id, Assert.Single(resolved.GetProperty("projects").EnumerateArray()).Str("project_id"));

        // The last admin can't be removed or demoted; every change is audited.
        using var remove = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/projects/{id}/members/{QaSeed.Admin}");
        remove.Headers.Add("Authorization", $"Bearer {QaSeed.AdminPat}");
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(remove, ct)).StatusCode);
        await using var db = athar.Db(QaSeed.Org);
        Assert.Equal(3, await db.AuditEvents.CountAsync(e => e.ResourceType == "project" && e.ResourceId == id, ct));

        async Task<HttpStatusCode> Put(HttpClient c, string path, object body, CancellationToken t)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = System.Net.Http.Json.JsonContent.Create(body) };
            request.Headers.Add("Authorization", $"Bearer {QaSeed.AdminPat}");
            return (await c.SendAsync(request, t)).StatusCode;
        }
    }
}
