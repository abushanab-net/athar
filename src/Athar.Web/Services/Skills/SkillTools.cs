using System.ComponentModel;
using System.Text.Json.Serialization;
using Athar.Web.Api;
using Athar.Web.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Athar.Web.Services;

/// <summary>docs/06 §6.2 <c>list_skills</c> and <c>get_skill</c> (FR-SK-01, T063), thin over <see cref="SkillService"/>.</summary>
[McpServerToolType]
public sealed class SkillTools(SkillService skills, IHttpContextAccessor http)
{
    [McpServerTool(Name = "list_skills", UseStructuredContent = true, ReadOnly = true)]
    [Description("Skills (SKILL.md packages) you can use: company-wide, your teams' and projects', and your own. Optionally filter by text or scope.")]
    public Task<SkillList> ListSkills(string? query = null, [Description("personal, team, project or company")] string? scope = null, CancellationToken ct = default) =>
        Run(a => skills.ListAsync(a, query, scope, ct));

    [McpServerTool(Name = "get_skill", UseStructuredContent = true, ReadOnly = true)]
    [Description("One skill: its SKILL.md body and its other files.")]
    public Task<SkillDetail> GetSkill(Guid skill_id, CancellationToken ct = default) =>
        Run(a => skills.GetAsync(a, skill_id, ct));

    // PM decision (2026-10-04): agents may publish too, and every publish is audited with the credential. Company skills from
    // anyone but a knowledge owner become pending proposals (T141, FR-SK-02); a skill is instructions other agents follow.
    [McpServerTool(Name = "publish_skill", UseStructuredContent = true, Idempotent = true)]
    [Description("Publish a skill (a SKILL.md package). Publishing the same name in the same scope adds a revision. A company skill from anyone but a knowledge owner answers status 'pending' with a proposal_id: a knowledge owner approves it. Only publish what the user asked for.")]
    public Task<SkillPublishResult> PublishSkill(
        [Description("lowercase letters, digits and hyphens, at most 64 characters")] string name,
        string description, string body_md,
        [Description("personal, team, project or company")] string scope,
        [Description("Team or project id, for team and project scope")] Guid? owner_id = null,
        [Description("Other files: [{path, content}], relative paths, at most 20")] SkillFile[]? files = null,
        CancellationToken ct = default) =>
        Run(a => skills.PublishOrProposeAsync(a, new(name, description, body_md, scope, owner_id, files), propose: true, ct));

    private async Task<T> Run<T>(Func<Actor, Task<T>> call)
    {
        try { return await call(Actor.From(http.HttpContext!)); }
        catch (AtharError e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }
}

public sealed record SkillFileBody([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("content")] string Content);
public sealed record PublishSkillBody(
    [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("body_md")] string BodyMd, [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("owner_id")] Guid? OwnerId = null, [property: JsonPropertyName("files")] SkillFileBody[]? Files = null);

/// <summary><c>/api/v1/skills</c>: list, get, and publish (POST; a company skill from a non-owner is a pending proposal).</summary>
public static class SkillEndpoints
{
    public static void MapSkillEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/skills", (string? query, string? scope, SkillService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.ListAsync(Actor.From(http), query, scope, ct)))
            .WithName("list_skills").Produces<SkillList>();
        v1.MapGet("/skills/{id:guid}", (Guid id, SkillService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.GetAsync(Actor.From(http), id, ct)))
            .WithName("get_skill").Produces<SkillDetail>();
        v1.MapPost("/skills", (PublishSkillBody b, SkillService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.PublishOrProposeAsync(Actor.From(http), new(b.Name, b.Description, b.BodyMd, b.Scope, b.OwnerId,
                    b.Files?.Select(f => new SkillFile(f.Path, f.Content)).ToList()), propose: true, ct)))
            .WithName("publish_skill").Produces<SkillPublishResult>();
    }
}
