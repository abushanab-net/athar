using System.ComponentModel;
using System.Text.Json.Serialization;
using Athar.Web.Api;
using Athar.Web.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Athar.Web.Services;

/// <summary>docs/06 §6.2 <c>promote_to_memory</c>, <c>open_conflict</c> and <c>resolve_conflict</c> (T062), thin over <see cref="MemoryService"/>.</summary>
[McpServerToolType]
public sealed class PromotionTools(MemoryService memories, IHttpContextAccessor http)
{
    [McpServerTool(Name = "promote_to_memory", UseStructuredContent = true, Idempotent = true)]
    [Description("Turn a section of a page into a memory linked to it (with the page's classification). Pass selection (exact text from the page) or anchor_id (an inline comment's anchor). Company scope by a non-owner becomes a proposal.")]
    public Task<PromoteResult> Promote(Guid page_id,
        [Description("personal, team, project or company")] string scope,
        [Description("fact, decision, convention, procedure, policy, preference, incident or glossary")] string type,
        string title, [Description("Idempotency key")] string op_id,
        string? selection = null, string? anchor_id = null,
        [Description("Team or project id, for team and project scope")] Guid? owner_id = null, CancellationToken ct = default) =>
        Run(a => memories.PromoteAsync(a, new(page_id, anchor_id, selection, scope, owner_id, type, title, op_id), ct));

    [McpServerTool(Name = "open_conflict", UseStructuredContent = true, Idempotent = true)]
    [Description("Record that two memories you can read disagree. Recall then annotates both until someone resolves it.")]
    public Task<ConflictOpened> OpenConflict(Guid memory_a, Guid memory_b, string note, string? op_id = null, CancellationToken ct = default) =>
        Run(a => memories.OpenConflictAsync(a, new(memory_a, memory_b, note, op_id ?? Guid.NewGuid().ToString()), ct));

    [McpServerTool(Name = "resolve_conflict", UseStructuredContent = true, Idempotent = true)]
    [Description("Resolve a conflict: keep_a or keep_b supersedes the other memory; merged gives A new text (body_md) and supersedes B; not_conflict changes nothing. Changes to company memories you can't publish become proposals (status pending_approval). If the project only differs from a company memory, shadow it (shadow_memory) and resolve not_conflict instead.")]
    public Task<ConflictResolved> ResolveConflict(Guid conflict_id,
        [Description("keep_a, keep_b, merged or not_conflict")] string resolution,
        [Description("Idempotency key")] string op_id, string? title = null, string? body_md = null, CancellationToken ct = default) =>
        Run(a => memories.ResolveConflictAsync(a, new(conflict_id, resolution, op_id, title, body_md), ct));

    private async Task<T> Run<T>(Func<Actor, Task<T>> call)
    {
        try { return await call(Actor.From(http.HttpContext!)); }
        catch (AtharError e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }
}

public sealed record PromoteBody(
    [property: JsonPropertyName("page_id")] Guid PageId, [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("op_id")] string OpId, [property: JsonPropertyName("selection")] string? Selection = null,
    [property: JsonPropertyName("anchor_id")] string? AnchorId = null, [property: JsonPropertyName("owner_id")] Guid? OwnerId = null);
public sealed record OpenConflictBody(
    [property: JsonPropertyName("memory_a")] Guid MemoryA, [property: JsonPropertyName("memory_b")] Guid MemoryB,
    [property: JsonPropertyName("note")] string Note, [property: JsonPropertyName("op_id")] string? OpId = null);
public sealed record ResolveConflictBody(
    [property: JsonPropertyName("resolution")] string Resolution, [property: JsonPropertyName("op_id")] string OpId,
    [property: JsonPropertyName("title")] string? Title = null, [property: JsonPropertyName("body_md")] string? BodyMd = null);

/// <summary>docs/06 §6.3: <c>POST /api/v1/memories/promote</c>, <c>POST /api/v1/conflicts</c>, <c>POST /api/v1/conflicts/{id}/resolve</c>.</summary>
public static class PromotionEndpoints
{
    public static void MapPromotionEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/memories/promote", (PromoteBody b, MemoryService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.PromoteAsync(Actor.From(http), new(b.PageId, b.AnchorId, b.Selection, b.Scope, b.OwnerId, b.Type, b.Title, b.OpId), ct)))
            .WithName("promote_to_memory").Produces<PromoteResult>();
        v1.MapPost("/conflicts", (OpenConflictBody b, MemoryService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.OpenConflictAsync(Actor.From(http), new(b.MemoryA, b.MemoryB, b.Note, b.OpId ?? Guid.NewGuid().ToString()), ct)))
            .WithName("open_conflict").Produces<ConflictOpened>();
        v1.MapPost("/conflicts/{id:guid}/resolve", (Guid id, ResolveConflictBody b, MemoryService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.ResolveConflictAsync(Actor.From(http), new(id, b.Resolution, b.OpId, b.Title, b.BodyMd), ct)))
            .WithName("resolve_conflict").Produces<ConflictResolved>();
    }
}
