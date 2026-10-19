using System.Security.Cryptography;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athar.Web.Mcp.AgentPageTools;

public sealed record PageListItem(Guid PageId, string Title, double Position, Guid? ParentId = null);
public sealed record PageList(IReadOnlyList<PageListItem> Pages);
public sealed record PageDeleted(bool Trashed, DateTimeOffset RestoreUntil);
/// <summary><paramref name="Status"/>, for inline threads: open (anchored), anchor_failed (kept page-level) or pending.</summary>
public sealed record CommentAdded(Guid CommentId, Guid ThreadId, string? Status = null);
/// <summary><see cref="OperationStatusView"/> for MCP: optional members default to null, so the output schema doesn't require them.</summary>
public sealed record OperationView(string OpId, string Status, string TargetKind, string? TargetId = null, JsonElement? Result = null, string? Error = null);

/// <summary>
/// What the agent page tools add on top of <see cref="PageOpsService"/> and <see cref="CommentService"/> (FR-AG-06):
/// an agent-aware <c>list_pages</c>, and durable (op_id) <c>delete_page</c> and <c>add_comment</c>.
/// </summary>
public sealed class AgentPageService(AtharDbContext db, PageService pages, CommentService comments)
{
    /// <summary>The live pages of a space the caller can read; for an agent, only where the space's AI policy allows reads.</summary>
    public async Task<PageList> ListAsync(Actor actor, Guid spaceId, Guid? parentId, CancellationToken ct)
    {
        var caller = await CallerAsync(actor, ct);
        if (!await db.SpacesFor(caller).AnyAsync(s => s.Id == spaceId, ct)) throw new AtharError("not_found", "Space not found.");
        var rows = await db.PagesFor(caller).Where(p => p.SpaceId == spaceId && p.DeletedAt == null && (parentId == null || p.ParentId == parentId))
            .OrderBy(p => p.Position).Select(p => new PageListItem(p.Id, p.Title, p.Position, p.ParentId)).ToListAsync(ct);
        return new(rows);
    }

    /// <summary>`delete_page`: to trash only, with the <c>pages:delete</c> scope and, for agents, the space's delete_to_trash policy.</summary>
    public Task<PageDeleted> DeleteAsync(Actor actor, Guid pageId, string reason, string opId, CancellationToken ct) =>
        OnceAsync(actor, opId, new { tool = "delete_page", pageId, reason }, OperationTarget.Page, async () =>
        {
            if (!actor.Scopes.Contains("pages:delete")) throw new AtharError("forbidden", "The token lacks the 'pages:delete' scope.");
            var caller = await CallerAsync(actor, ct);
            if (!await db.CanAsync(caller, pageId, PageAction.Edit, ct))
                throw await db.CanAsync(caller, pageId, PageAction.Read, ct) ? new AtharError("forbidden", "You can't delete this page.") : new AtharError("not_found", "Page not found.");
            var spaceId = await db.Pages.Where(p => p.Id == pageId).Select(p => p.SpaceId).SingleAsync(ct);
            if (caller.Agent && !await db.Spaces.AnyAsync(s => s.Id == spaceId && s.AiPolicy.DeleteToTrash, ct))
                throw new AtharError("policy_denied", "This space's AI policy doesn't allow agents to delete pages.");
            var until = await pages.TrashAsync(new WikiActor(actor.UserId, actor.OrgId), pageId, ct);
            db.Audit(actor.OrgId, actor.UserId, "page.agent_delete", "page", pageId, after: new { reason }, credentialId: actor.CredentialId);
            return (new PageDeleted(true, until), pageId);
        }, ct);

    /// <summary>
    /// `add_comment`: a reply to <paramref name="threadId"/>, a new page-level thread, or, with a <paramref name="quote"/>, an
    /// inline thread anchored on that text by collab (<paramref name="anchorHint"/> settles a quote that repeats). An
    /// unplaceable quote leaves a page-level thread with status <c>anchor_failed</c>.
    /// </summary>
    public async Task<CommentAdded> CommentAsync(Actor actor, Guid pageId, string bodyMd, Guid? threadId, string? quote, string opId, CancellationToken ct, string? anchorHint = null)
    {
        if (!string.IsNullOrWhiteSpace(quote))
        {
            if (threadId is not null) throw new AtharError("invalid_input", "A reply can't quote: pass quote only for a new thread.");
            // CommentService records this op_id itself (through the page op that places the anchor), so no OnceAsync here.
            var inline = await comments.AddInlineThreadAsync(actor, pageId, bodyMd, from: null, to: null, quote, opId, ct, anchorHint: anchorHint);
            return new(inline.Comments[0].Id, inline.Id, inline.Status);
        }
        return await OnceAsync(actor, opId, new { tool = "add_comment", pageId, bodyMd, threadId }, OperationTarget.Comment, async () =>
        {
            if (threadId is { } t)
            {
                db.CurrentOrgId = actor.OrgId;
                if (!await db.CommentThreads.AnyAsync(x => x.Id == t && x.PageId == pageId, ct)) throw new AtharError("not_found", "Thread not found.");
                return (new CommentAdded(await comments.ReplyAsync(actor, t, bodyMd, ct), t), t);
            }
            var thread = await comments.AddPageThreadAsync(actor, pageId, bodyMd, ct);
            var comment = await db.Comments.Where(c => c.ThreadId == thread).Select(c => c.Id).SingleAsync(ct);
            return (new CommentAdded(comment, thread), thread);
        }, ct);
    }

    private async Task<Caller> CallerAsync(Actor actor, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        return await db.ActorAsync(actor.UserId, actor.CredentialId, ct) ?? throw new AtharError("unauthorized", "The user is inactive or the credential is revoked.");
    }

    /// <summary>
    /// FR-RT-07 for writes that don't go through collab: the work, its audit row and the op_id row commit in one transaction;
    /// the same actor and payload get the stored result, another payload <c>op_id_reused</c>, another actor <c>not_found</c>.
    /// ponytail: same rules as MemoryService.WriteOnce; one shared helper if a third copy appears.
    /// </summary>
    private async Task<T> OnceAsync<T>(Actor actor, string opId, object payload, OperationTarget kind, Func<Task<(T Result, Guid Target)>> work, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opId)) throw new AtharError("invalid_input", "op_id is required.");
        db.CurrentOrgId = actor.OrgId;
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
        if (await StoredAsync<T>(actor, opId, hash, ct) is { } stored) return stored;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var (result, target) = await work();
            db.Operations.Add(new Operation
            {
                OpId = opId, ActorUserId = actor.UserId, CredentialId = actor.CredentialId, RequestHash = hash, TargetKind = kind,
                TargetId = target.ToString(), Status = OperationStatus.Done, Result = JsonSerializer.Serialize(result, McpEndpoint.Json),
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await StoredAsync<T>(actor, opId, hash, ct) ?? throw new AtharError("pending", "The operation is still in progress.");
        }
    }

    private async Task<T?> StoredAsync<T>(Actor actor, string opId, string hash, CancellationToken ct)
    {
        var op = await db.Operations.AsNoTracking().FirstOrDefaultAsync(o => o.OpId == opId, ct);
        if (op is null) return default;
        if (op.ActorUserId != actor.UserId || op.CredentialId != actor.CredentialId) throw new AtharError("not_found", "Unknown op_id.");
        if (op.RequestHash != hash) throw new AtharError("op_id_reused", "This op_id was already used with a different payload.");
        return JsonSerializer.Deserialize<T>(op.Result!, McpEndpoint.Json);
    }
}
