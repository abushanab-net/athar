using System.Text.Json.Serialization;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Mcp.AgentPageTools;

namespace Athar.Web.Api;

public sealed record CreatePageBody(
    [property: JsonPropertyName("space_id")] Guid SpaceId,
    string Title,
    string Markdown,
    [property: JsonPropertyName("op_id")] string OpId,
    [property: JsonPropertyName("parent_id")] Guid? ParentId = null);
public sealed record AppendPageBody(string Markdown, [property: JsonPropertyName("op_id")] string OpId);
public sealed record ReplacePageBody(
    string Markdown,
    [property: JsonPropertyName("expected_live_token")] string ExpectedLiveToken,
    [property: JsonPropertyName("op_id")] string OpId);
public sealed record CommentBody(
    [property: JsonPropertyName("body_md")] string BodyMd,
    [property: JsonPropertyName("op_id")] string OpId,
    [property: JsonPropertyName("thread_id")] Guid? ThreadId = null,
    string? Quote = null,
    [property: JsonPropertyName("anchor_hint")] string? AnchorHint = null);

/// <summary>
/// The REST equivalents of the page tools (contract §6.2, FR-AG-02). Each route calls exactly what its MCP tool calls
/// (<see cref="PageTools"/>): writes go through Lane B's durable <see cref="PageOpsService"/>, and list, delete and comment
/// through Lane D's agent-aware <see cref="AgentPageService"/>. No second write path.
/// </summary>
public static class PageToolEndpoints
{
    public static void MapPageToolEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/pages/{id:guid}", (Guid id, string? format, PageOpsService ops, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => ops.GetPageAsync(Actor.From(http), id, format ?? "markdown", ct).AuditAsync(http, "get_page", new { page_id = id })))
            .WithName("get_page").Produces<PageView>();
        v1.MapGet("/spaces/{spaceId:guid}/pages", (Guid spaceId, Guid? parent_id, AgentPageService pages, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => pages.ListAsync(Actor.From(http), spaceId, parent_id, ct).AuditAsync(http, "list_pages", new { space_id = spaceId, parent_id })))
            .WithName("list_pages").Produces<PageList>();
        v1.MapPost("/pages", (CreatePageBody b, PageOpsService ops, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => ops.CreatePageAsync(Actor.From(http), b.SpaceId, b.ParentId, b.Title, b.Markdown, b.OpId, ct)))
            .WithName("create_page").Produces<PageOpResult>();
        v1.MapPost("/pages/{id:guid}/append", (Guid id, AppendPageBody b, PageOpsService ops, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => ops.RunAsync(Actor.From(http), id, b.OpId, new PageOp("append", b.Markdown), ct)))
            .WithName("append_to_page").Produces<PageOpResult>();
        v1.MapPut("/pages/{id:guid}", (Guid id, ReplacePageBody b, PageOpsService ops, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => ops.RunAsync(Actor.From(http), id, b.OpId, new PageOp("replace", b.Markdown, b.ExpectedLiveToken), ct)))
            .WithName("replace_page").Produces<PageOpResult>();
        // Moves to the trash (restorable for 30 days); needs pages:delete and the space's AI policy.
        v1.MapDelete("/pages/{id:guid}", (Guid id, string reason, string op_id, AgentPageService pages, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => pages.DeleteAsync(Actor.From(http), id, reason, op_id, ct)))
            .WithName("delete_page").Produces<PageDeleted>();
        v1.MapPost("/pages/{id:guid}/comments", (Guid id, CommentBody b, AgentPageService pages, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => pages.CommentAsync(Actor.From(http), id, b.BodyMd, b.ThreadId, b.Quote, b.OpId, ct, b.AnchorHint)))
            .WithName("add_comment").Produces<CommentAdded>();
    }
}
