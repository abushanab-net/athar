using Athar.Web.Mcp;
using Athar.Web.Services;

namespace Athar.Web.Api;

public sealed record ArchiveBody(string? Reason);
public sealed record LinkMemoryBody(string Target);
public sealed record ClassifyBody(bool Confidential, string? Reason);
public sealed record AcceptBody(string? Title = null, [property: System.Text.Json.Serialization.JsonPropertyName("body_md")] string? BodyMd = null,
    string? Type = null, string? Scope = null, [property: System.Text.Json.Serialization.JsonPropertyName("owner_id")] Guid? OwnerId = null);

/// <summary>The memory browser and capture inbox over REST (FR-MM-09). The pages (T022) call these.</summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this RouteGroupBuilder v1)
    {
        var api = v1.MapGroup("/memories");
        // Query: scope, type, status (default active), project_id, stale, skip, take.
        api.MapGet("/", (string? scope, string? type, string? status, Guid? project_id, bool? stale, int? skip, int? take,
                MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.ListAsync(Actor.From(http), new(scope, type, status, project_id, stale, skip ?? 0, take ?? 50), ct)));
        api.MapGet("/{id:guid}", (Guid id, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.GetAsync(Actor.From(http), id, ct)));
        api.MapPost("/{id:guid}/archive", (Guid id, ArchiveBody? body, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.ArchiveAsync(Actor.From(http), id, body?.Reason, ct)));
        // FR-CL-01: mark confidential, or declassify with a reason (audited).
        api.MapPost("/{id:guid}/classify", (Guid id, ClassifyBody body, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.ClassifyAsync(Actor.From(http), id, body.Confidential, body.Reason, ct)));
        api.MapPost("/{id:guid}/links", (Guid id, LinkMemoryBody body, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.LinkAsync(Actor.From(http), id, body.Target, ct)));

        // FR-SR-01: GET /api/v1/search?q=…&kinds=page&kinds=memory&space_ids=…&project_ids=…&limit=10
        v1.MapGet("/search", (string q, string[]? kinds, Guid[]? space_ids, Guid[]? project_ids, int? limit,
                SearchService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.SearchAsync(Actor.From(http), new(q, kinds is { Length: > 0 } ? kinds : null,
                space_ids is { Length: > 0 } ? space_ids : null, project_ids is { Length: > 0 } ? project_ids : null, limit ?? 10), ct).AuditAsync(http, "search", new { query = q })));
        // FR-MM-06: accept (optionally edited) or discard a capture candidate (docs/06 §6.3).
        v1.MapPost("/inbox/{id:guid}/accept", (Guid id, AcceptBody? b, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.AcceptAsync(Actor.From(http), id, new(b?.Title, b?.BodyMd, b?.Type, b?.Scope, b?.OwnerId), ct)));
        v1.MapPost("/inbox/{id:guid}/discard", (Guid id, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.DiscardAsync(Actor.From(http), id, ct)));
        // T137: undo an auto-accepted candidate (archives its memory).
        v1.MapPost("/inbox/{id:guid}/undo", (Guid id, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.UndoAutoAcceptAsync(Actor.From(http), id, ct)));
        v1.MapGet("/inbox", (MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.InboxAsync(Actor.From(http), ct)));
    }
}
