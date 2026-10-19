using System.ComponentModel;
using Athar.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Athar.Web.Mcp;

/// <summary>docs/06 §6.2 <c>start_session</c>, <c>commit_session</c> (FR-AG-04) and <c>save_chat</c> (FR-AG-09), thin over <see cref="SessionService"/>.</summary>
[McpServerToolType]
public sealed class SessionTools(SessionService sessions, IHttpContextAccessor http)
{
    [McpServerTool(Name = "start_session", UseStructuredContent = true, Idempotent = true)]
    [Description("Start (or find) the capture session for an agent session id. Only for repositories opted in to capture.")]
    public Task<SessionStarted> StartSession(
        [Description("claude-code or codex")] string client,
        [Description("The agent's own session id")] string external_session_id,
        string? repo_remote = null,
        CancellationToken ct = default) =>
        Run(a => sessions.StartAsync(a, client, external_session_id, repo_remote, ct));

    [McpServerTool(Name = "commit_session", UseStructuredContent = true, Idempotent = true)]
    [Description("Store redacted transcript records. A repeated commit_id, or records already stored, are ignored. Returns the acked index.")]
    public Task<CommitAck> CommitSession(
        Guid session_id, string commit_id, int from_index, int to_index, int skipped,
        [Description("[{index, data}] JSON objects, at most 2 MB in total")] CommitRecord[] records,
        CancellationToken ct = default) =>
        Run(a => sessions.CommitAsync(a, session_id, new(commit_id, from_index, to_index, skipped, records), ct));

    [McpServerTool(Name = "save_chat", UseStructuredContent = true, Idempotent = true)]
    [Description("Save this chat to Athar as a session: a title, a Markdown summary and key points. Athar extracts memory candidates from it "
        + "like a coding session. Pass project_id from resolve_project (or the user's pick) when the chat is about a project.")]
    public Task<SavedChat> SaveChat(
        [Description("A short title for the chat")] string title,
        [Description("What was discussed and decided, in Markdown")] string summary_md,
        [Description("The decisions, facts and conventions worth remembering, one per item")] string[] key_points,
        [Description("A unique id for this save; reuse it only to retry")] string op_id,
        Guid? project_id = null,
        CancellationToken ct = default) =>
        Run(a => sessions.SaveChatAsync(a, title, summary_md, key_points, project_id, op_id, ct));

    private async Task<T> Run<T>(Func<Actor, Task<T>> call)
    {
        try { return await call(Actor.From(http.HttpContext!)); }
        catch (AtharError e) { throw new McpException($"{e.Code}: {e.Message}"); }
    }
}
