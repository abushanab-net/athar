using System.Text.Json.Serialization;
using Athar.Web.Mcp;
using Athar.Web.Services;

namespace Athar.Web.Api;

// Request bodies: the §6.2 tool inputs, snake_case like the MCP tools.
public sealed record RecallBody(
    string Query,
    [property: JsonPropertyName("repo_remote")] string? RepoRemote = null,
    [property: JsonPropertyName("project_ids")] Guid[]? ProjectIds = null,
    string[]? Scopes = null,
    string[]? Types = null,
    int? Limit = null);
public sealed record RememberBody(
    string Title,
    [property: JsonPropertyName("body_md")] string BodyMd,
    string Type,
    string Scope,
    [property: JsonPropertyName("op_id")] string OpId,
    [property: JsonPropertyName("owner_id")] Guid? OwnerId = null,
    string[]? Links = null);
public sealed record UpdateMemoryBody(
    [property: JsonPropertyName("expected_revision")] int ExpectedRevision,
    [property: JsonPropertyName("op_id")] string OpId,
    string? Title = null,
    [property: JsonPropertyName("body_md")] string? BodyMd = null);
public sealed record ShadowBody(
    [property: JsonPropertyName("project_id")] Guid ProjectId,
    [property: JsonPropertyName("body_md")] string BodyMd,
    string Reason,
    [property: JsonPropertyName("op_id")] string OpId);
public sealed record ProposeBody(
    string Title,
    [property: JsonPropertyName("body_md")] string BodyMd,
    string Type,
    [property: JsonPropertyName("op_id")] string OpId,
    [property: JsonPropertyName("target_memory_id")] Guid? TargetMemoryId = null,
    [property: JsonPropertyName("evidence_links")] string[]? EvidenceLinks = null);

/// <summary>
/// The REST equivalent of each brain tool in contract §6.2 (FR-AG-02). Same services, same results and error codes as MCP.
/// <c>search</c> is in <see cref="MemoryEndpoints"/>; sessions and operations are mapped by their lanes.
/// </summary>
public static class BrainToolEndpoints
{
    public static void MapBrainToolEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/recall", (RecallBody b, MemoryService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.RecallAsync(Actor.From(http), new(b.Query, b.ProjectIds, b.Scopes, b.Types, b.Limit ?? 10, b.RepoRemote), ct)))
            .WithName("recall").Produces<RecallResult>();
        v1.MapPost("/memories", (RememberBody b, MemoryService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.RememberAsync(Actor.From(http), new(b.Title, b.BodyMd, b.Type, b.Scope, b.OpId, b.OwnerId, b.Links), ct)))
            .WithName("remember").Produces<RememberResult>();
        v1.MapPatch("/memories/{id:guid}", (Guid id, UpdateMemoryBody b, MemoryService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.UpdateAsync(Actor.From(http), new(id, b.Title, b.BodyMd, b.ExpectedRevision, b.OpId), ct)))
            .WithName("update_memory").Produces<UpdateResult>();
        v1.MapPost("/memories/{id:guid}/shadow", (Guid id, ShadowBody b, MemoryService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.ShadowAsync(Actor.From(http), new(id, b.ProjectId, b.BodyMd, b.Reason, b.OpId), ct)))
            .WithName("shadow_memory").Produces<ShadowResult>();
        v1.MapPost("/proposals", (ProposeBody b, MemoryService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.ProposeAsync(Actor.From(http), new(b.Title, b.BodyMd, b.Type, b.TargetMemoryId, b.EvidenceLinks, b.OpId), ct)))
            .WithName("propose_company_memory").Produces<ProposeResult>();
        v1.MapGet("/projects/resolve", (string repo_remote, ProjectService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(async () => new ResolvedProjects(await s.ResolveAsync(Actor.From(http), repo_remote, ct))))
            .WithName("resolve_project").Produces<ResolvedProjects>();
    }
}
