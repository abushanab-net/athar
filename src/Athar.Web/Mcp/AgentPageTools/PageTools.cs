using System.ComponentModel;
using Athar.Web.CollabBridge;
using Athar.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Athar.Web.Mcp.AgentPageTools;

/// <summary>
/// docs/06 §6.2 page tools for agents (FR-AG-06, T064). Writes go through <see cref="PageOpsService"/> (durable op_ids,
/// collab, the write scope and the space AI policy); delete, list and comments through <see cref="AgentPageService"/>.
/// </summary>
[McpServerToolType]
public sealed class PageTools(PageOpsService ops, AgentPageService agentPages, IHttpContextAccessor http)
{
    [McpServerTool(Name = "get_page", UseStructuredContent = true, ReadOnly = true)]
    [Description("A page's title and content, with the live_token replace_page needs (both from one snapshot).")]
    public Task<PageView> GetPage(Guid page_id, [Description("markdown (default) or json")] string format = "markdown", CancellationToken ct = default) =>
        Run(a => ops.GetPageAsync(a, page_id, format, ct));

    [McpServerTool(Name = "list_pages", UseStructuredContent = true, ReadOnly = true)]
    [Description("The pages of a space you can read, optionally only the children of parent_id.")]
    public Task<PageList> ListPages(Guid space_id, Guid? parent_id = null, CancellationToken ct = default) =>
        Run(a => agentPages.ListAsync(a, space_id, parent_id, ct));

    [McpServerTool(Name = "create_page", UseStructuredContent = true, Idempotent = true)]
    [Description("Create a page from Markdown in a space whose AI policy allows agent writes.")]
    public Task<PageOpResult> CreatePage(Guid space_id, string title, string markdown,
        [Description("Idempotency key: repeating it with the same payload returns the stored result")] string op_id,
        Guid? parent_id = null, CancellationToken ct = default) =>
        Run(a => ops.CreatePageAsync(a, space_id, parent_id, title, markdown, op_id, ct));

    [McpServerTool(Name = "append_to_page", UseStructuredContent = true, Idempotent = true)]
    [Description("Append Markdown at the end of a page.")]
    public Task<PageOpResult> AppendToPage(Guid page_id, string markdown, string op_id, CancellationToken ct = default) =>
        Run(a => ops.RunAsync(a, page_id, op_id, new PageOp("append", markdown), ct));

    [McpServerTool(Name = "replace_page", UseStructuredContent = true, Idempotent = true)]
    [Description("Replace a page's content. Fails with conflict_revision if expected_live_token (from get_page) has moved; returns the comment threads whose anchors were removed.")]
    public Task<PageOpResult> ReplacePage(Guid page_id, string markdown, string expected_live_token, string op_id, CancellationToken ct = default) =>
        Run(a => ops.RunAsync(a, page_id, op_id, new PageOp("replace", markdown, expected_live_token), ct));

    [McpServerTool(Name = "delete_page", UseStructuredContent = true, Idempotent = true, Destructive = true)]
    [Description("Move a page and its subpages to the trash (restorable for 30 days). Needs the pages:delete scope and a space AI policy that allows it.")]
    public Task<PageDeleted> DeletePage(Guid page_id, string reason, string op_id, CancellationToken ct = default) =>
        Run(a => agentPages.DeleteAsync(a, page_id, reason, op_id, ct));

    [McpServerTool(Name = "add_comment", UseStructuredContent = true, Idempotent = true)]
    [Description("Comment on a page: a new thread, or a reply when thread_id is given. With quote (exact text from one paragraph) the new thread is anchored inline on it; status anchor_failed means it was kept page-level, so retry with anchor_hint (nearby text from the same paragraph) if the quote repeats.")]
    public Task<CommentAdded> AddComment(Guid page_id, string body_md, string op_id, Guid? thread_id = null, string? quote = null,
        [Description("Nearby text from the quote's paragraph, to pick the right occurrence when the quote repeats")] string? anchor_hint = null,
        CancellationToken ct = default) =>
        Run(a => agentPages.CommentAsync(a, page_id, body_md, thread_id, quote, op_id, ct, anchor_hint));

    [McpServerTool(Name = "get_operation", UseStructuredContent = true, ReadOnly = true)]
    [Description("The outcome of one of your durable writes, by op_id.")]
    public Task<OperationView> GetOperation(string op_id, CancellationToken ct = default) =>
        Run(async a =>
        {
            var s = await ops.StatusAsync(a, op_id, ct);
            return new OperationView(s.OpId, s.Status, s.TargetKind, s.TargetId, s.Result, s.Error);
        });

    private async Task<T> Run<T>(Func<Actor, Task<T>> call)
    {
        try { return await call(Actor.From(http.HttpContext!)); }
        catch (AtharError e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }
}
