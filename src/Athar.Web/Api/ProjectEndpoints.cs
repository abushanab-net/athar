using System.Text.Json.Serialization;
using Athar.Web.Mcp;
using Athar.Web.Services;

namespace Athar.Web.Api;

public sealed record CreateProjectBody(string Name, string Slug);
public sealed record MemberBody([property: JsonPropertyName("user_id")] Guid UserId, string Role);
public sealed record LinkBody(
    [property: JsonPropertyName("repo_remote")] string? RepoRemote,
    [property: JsonPropertyName("space_id")] Guid? SpaceId,
    [property: JsonPropertyName("team_id")] Guid? TeamId);

/// <summary>Projects, members and links (FR-PJ-01).</summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this RouteGroupBuilder v1)
    {
        var api = v1.MapGroup("/projects");
        api.MapGet("/", (ProjectService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.ListAsync(Actor.From(http), ct)));
        api.MapPost("/", (CreateProjectBody body, ProjectService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.CreateAsync(Actor.From(http), body.Name, body.Slug, ct)));
        api.MapPut("/{id:guid}/members", (Guid id, MemberBody body, ProjectService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.SetMemberAsync(Actor.From(http), id, body.UserId, body.Role, ct)));
        api.MapDelete("/{id:guid}/members/{userId:guid}", (Guid id, Guid userId, ProjectService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.RemoveMemberAsync(Actor.From(http), id, userId, ct)));
        api.MapPost("/{id:guid}/links", (Guid id, LinkBody body, ProjectService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.LinkAsync(Actor.From(http), id, new(body.RepoRemote, body.SpaceId, body.TeamId), ct)));
    }
}
