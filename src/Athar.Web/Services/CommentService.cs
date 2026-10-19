using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record CommentView(Guid Id, Guid AuthorId, string AuthorName, string? BodyMd, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, bool Deleted);

/// <summary>A thread. <c>Inline</c> while its anchor is live; orphaned and failed anchors read as page-level.</summary>
public sealed record ThreadView(Guid Id, string Status, string? Quote, bool Inline, Guid? ResolvedBy, IReadOnlyList<CommentView> Comments);

/// <summary>
/// Comments (FR-CM-01..07). Commenters and above may comment (FR-CM-07); authors edit and delete their own, space admins
/// delete any (FR-CM-06); deletes are soft. Every change is audited with before and after (AT-18).
/// Inline threads follow one recoverable flow under one op_id (FR-CM-01): the thread is created pending with its first
/// comment, collab anchors it through the durable ops path, and the persist holding that op opens it
/// (<see cref="CollabStore"/>). <see cref="ReconcileAnchorsAsync"/> fails threads still pending after 2 minutes.
/// </summary>
public sealed class CommentService(AtharDbContext db, PageOpsService ops, TimeProvider clock, NotificationService notifications)
{
    public static readonly TimeSpan AnchorTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The page's threads, newest first. Resolved ones only when asked (FR-CM-05).</summary>
    public async Task<List<ThreadView>> ListAsync(Actor actor, Guid pageId, bool includeResolved = false, CancellationToken ct = default)
    {
        await RequireAsync(actor, pageId, PageAction.Read, ct);
        var threads = await db.CommentThreads.AsNoTracking()
            .Where(t => t.PageId == pageId && (includeResolved || t.Status != ThreadStatus.Resolved))
            .OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        var ids = threads.Select(t => t.Id).ToList();
        var comments = await db.Comments.AsNoTracking().Where(c => ids.Contains(c.ThreadId)).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var authors = comments.Select(c => c.AuthorId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => authors.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var byThread = comments.ToLookup(c => c.ThreadId);
        return [.. threads.Select(t => View(t, byThread[t.Id], names))];
    }

    /// <summary>A page-level thread (FR-CM-03).</summary>
    public async Task<Guid> AddPageThreadAsync(Actor actor, Guid pageId, string body, CancellationToken ct = default)
    {
        await RequireAsync(actor, pageId, PageAction.Comment, ct);
        var thread = new CommentThread { PageId = pageId, Status = ThreadStatus.Open };
        db.CommentThreads.Add(thread);
        db.Comments.Add(new Comment { ThreadId = thread.Id, AuthorId = actor.UserId, BodyMd = Body(body) });
        db.Audit(actor.OrgId, actor.UserId, "comment.thread", "comment_thread", thread.Id, after: new { page_id = pageId, inline = false }, credentialId: actor.CredentialId);
        var mentioned = await MentionAsync(actor, pageId, thread.Id, body, null, ct);
        await WatchersAsync(actor, pageId, thread.Id, "watch_comment", mentioned, ct);
        await db.SaveChangesAsync(ct);
        return thread.Id;
    }

    /// <summary>
    /// An inline thread (FR-CM-01) on the text between two Yjs relative positions or, with none (agents), on the one place
    /// the quote appears (<paramref name="anchorHint"/>: nearby text that picks between repeats). Retrying with the same op_id resumes the
    /// same thread. Returns the thread: open once anchored, pending if collab didn't answer, anchor_failed (page-level,
    /// author notified) if the anchor can't be placed.
    /// </summary>
    public async Task<ThreadView> AddInlineThreadAsync(Actor actor, Guid pageId, string body, string? from, string? to, string quote, string opId, CancellationToken ct = default, string? anchorHint = null)
    {
        if (string.IsNullOrWhiteSpace(opId)) throw new AtharError("invalid_input", "op_id is required.");
        if (string.IsNullOrWhiteSpace(quote)) throw new AtharError("invalid_input", "An inline comment needs the quoted text.");
        await RequireAsync(actor, pageId, PageAction.Comment, ct);

        // 1. The thread, pending, with its first comment: one transaction.
        var thread = await db.CommentThreads.SingleOrDefaultAsync(t => t.OpId == opId && t.PageId == pageId, ct);
        if (thread is null)
        {
            thread = new CommentThread { PageId = pageId, Quote = quote, Status = ThreadStatus.PendingAnchor, OpId = opId };
            thread.AnchorId = thread.Id.ToString();
            db.CommentThreads.Add(thread);
            db.Comments.Add(new Comment { ThreadId = thread.Id, AuthorId = actor.UserId, BodyMd = Body(body) });
            db.Audit(actor.OrgId, actor.UserId, "comment.thread", "comment_thread", thread.Id,
                after: new { page_id = pageId, inline = true, quote, op_id = opId }, credentialId: actor.CredentialId);
            var mentioned = await MentionAsync(actor, pageId, thread.Id, body, null, ct);
            await WatchersAsync(actor, pageId, thread.Id, "watch_comment", mentioned, ct);
            await db.SaveChangesAsync(ct);
        }
        else if (!await db.Comments.AnyAsync(c => c.ThreadId == thread.Id && c.AuthorId == actor.UserId, ct))
            throw new AtharError("not_found", "Unknown op_id.");

        // 2. Collab places the anchor through the durable ops path, under the same op_id.
        if (thread.Status == ThreadStatus.PendingAnchor)
        {
            try
            {
                await ops.RunAsync(actor, pageId, opId, new PageOp("anchorComment", ThreadId: thread.Id, From: from, To: to, Quote: quote, AnchorHint: anchorHint), ct);
            }
            catch (AtharError e) when (e.Code == "pending")
            {
                // Collab didn't answer: the persist may still open it, or the reconciler fails it after 2 minutes.
            }
            catch (AtharError e) when (e.Code is "anchor_failed" or "conflict_revision" or "invalid_input")
            {
                await FailAnchorAsync(thread, ct);
            }
            // 3. The persist holding the op opened it.
            await db.Entry(thread).ReloadAsync(ct);
        }
        return (await ListAsync(actor, pageId, includeResolved: true, ct)).Single(t => t.Id == thread.Id);
    }

    public async Task<Guid> ReplyAsync(Actor actor, Guid threadId, string body, CancellationToken ct = default)
    {
        var thread = await ThreadAsync(actor, threadId, PageAction.Comment, ct);
        var comment = new Comment { ThreadId = thread.Id, AuthorId = actor.UserId, BodyMd = Body(body) };
        db.Comments.Add(comment);
        db.Audit(actor.OrgId, actor.UserId, "comment.reply", "comment", comment.Id, after: new { thread_id = threadId, body_md = comment.BodyMd }, credentialId: actor.CredentialId);
        var mentioned = await MentionAsync(actor, thread.PageId, thread.Id, body, null, ct);
        // FR-NT-01: replies in your threads (people already told through a mention get one notification).
        var participants = await db.Comments.Where(c => c.ThreadId == thread.Id && c.DeletedAt == null).Select(c => c.AuthorId).Distinct().ToListAsync(ct);
        var replied = await notifications.NotifyAsync(actor.OrgId, participants.Except(mentioned), "reply", $"thread:{thread.Id}", thread.PageId, actor.UserId, ct);
        await WatchersAsync(actor, thread.PageId, thread.Id, "watch_reply", [.. mentioned, .. replied], ct);
        await db.SaveChangesAsync(ct);
        return comment.Id;
    }

    /// <summary>Authors edit their own comments (FR-CM-06).</summary>
    public async Task EditAsync(Actor actor, Guid commentId, string body, CancellationToken ct = default)
    {
        var comment = await CommentAsync(actor, commentId, ct);
        if (comment.AuthorId != actor.UserId) throw new AtharError("forbidden", "Only the author can edit a comment.");
        var before = comment.BodyMd;
        comment.BodyMd = Body(body);
        comment.EditedAt = clock.GetUtcNow();
        var pageId = await db.CommentThreads.Where(t => t.Id == comment.ThreadId).Select(t => t.PageId).SingleAsync(ct);
        await MentionAsync(actor, pageId, comment.ThreadId, comment.BodyMd, before, ct); // only people newly mentioned
        db.Audit(actor.OrgId, actor.UserId, "comment.edit", "comment", comment.Id, new { body_md = before }, new { body_md = comment.BodyMd }, actor.CredentialId);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Authors delete their own comments, space admins any (FR-CM-06). Soft: the row stays, the audit keeps the text.</summary>
    public async Task DeleteAsync(Actor actor, Guid commentId, CancellationToken ct = default)
    {
        var comment = await CommentAsync(actor, commentId, ct);
        if (comment.AuthorId != actor.UserId)
        {
            var space = await db.CommentThreads.Where(t => t.Id == comment.ThreadId).Select(t => db.Pages.Where(p => p.Id == t.PageId).Select(p => p.SpaceId).Single()).SingleAsync(ct);
            if (await db.SpaceRoleAsync(actor.UserId, space, ct) != SpaceRole.Admin) throw new AtharError("forbidden", "Only the author or a space admin can delete a comment.");
        }
        db.Audit(actor.OrgId, actor.UserId, "comment.delete", "comment", comment.Id, new { body_md = comment.BodyMd, deleted = false }, new { deleted = true }, actor.CredentialId);
        comment.DeletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>FR-CM-05.</summary>
    public Task ResolveAsync(Actor actor, Guid threadId, CancellationToken ct = default) => SetResolvedAsync(actor, threadId, true, ct);

    public Task ReopenAsync(Actor actor, Guid threadId, CancellationToken ct = default) => SetResolvedAsync(actor, threadId, false, ct);

    /// <summary>
    /// FR-CM-01 recovery: inline threads still pending 2 minutes after creation fail. They stay as page-level threads with
    /// their quote, and the author is notified (AT-51). Runs for every org (the anchor reconciler calls it).
    /// </summary>
    public async Task<int> ReconcileAnchorsAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow() - AnchorTimeout;
        var stale = await db.CommentThreads.IgnoreQueryFilters()
            .Where(t => t.Status == ThreadStatus.PendingAnchor && t.OpId != null && t.CreatedAt < cutoff).ToListAsync(ct);
        foreach (var thread in stale) await FailAnchorAsync(thread, ct);
        return stale.Count;
    }

    private async Task FailAnchorAsync(CommentThread thread, CancellationToken ct)
    {
        thread.Status = ThreadStatus.AnchorFailed;
        var author = await db.Comments.IgnoreQueryFilters().Where(c => c.OrgId == thread.OrgId && c.ThreadId == thread.Id)
            .OrderBy(c => c.CreatedAt).Select(c => c.AuthorId).FirstAsync(ct);
        db.Notifications.Add(new Notification { OrgId = thread.OrgId, UserId = author, Kind = "anchor_failed", Ref = $"thread:{thread.Id}" });
        db.Audit(thread.OrgId, null, "comment.anchor_failed", "comment_thread", thread.Id, new { status = "pending_anchor" }, new { status = "anchor_failed" });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// FR-CM-04: records the mentions in <paramref name="body"/> that <paramref name="previous"/> didn't have, and notifies
    /// the ones who can see the page. Returns who was notified.
    /// </summary>
    private async Task<List<Guid>> MentionAsync(Actor actor, Guid pageId, Guid threadId, string body, string? previous, CancellationToken ct)
    {
        var added = NotificationService.MentionsIn(body).Except(NotificationService.MentionsIn(previous)).ToList();
        if (added.Count == 0) return [];
        var notified = await notifications.NotifyAsync(actor.OrgId, added, "mention", $"thread:{threadId}", pageId, actor.UserId, ct);
        foreach (var user in notified) db.Mentions.Add(new Mention { SourceKind = MentionSource.Comment, SourceId = threadId, UserId = user });
        return notified;
    }

    private async Task SetResolvedAsync(Actor actor, Guid threadId, bool resolved, CancellationToken ct)
    {
        var thread = await ThreadAsync(actor, threadId, PageAction.Comment, ct);
        var before = Status(thread.Status);
        // A reopened inline thread whose text went away meanwhile is orphaned by the next persist.
        thread.Status = resolved ? ThreadStatus.Resolved : ThreadStatus.Open;
        thread.ResolvedBy = resolved ? actor.UserId : null;
        db.Audit(actor.OrgId, actor.UserId, resolved ? "comment.resolve" : "comment.reopen", "comment_thread", thread.Id,
            new { status = before }, new { status = Status(thread.Status) }, actor.CredentialId);
        if (resolved) await notifications.NotifyWatchersAsync(actor.OrgId, thread.PageId, "watch_resolved", $"thread:{thread.Id}", actor.UserId, ct: ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// U-3 (T118): a person's first comment on a page watches it (only the first, so unwatching sticks), then the page's
    /// watchers hear about the comment, except people <paramref name="told"/> about it already (mentions, replies).
    /// </summary>
    private async Task WatchersAsync(Actor actor, Guid pageId, Guid threadId, string kind, IEnumerable<Guid> told, CancellationToken ct)
    {
        var commentedBefore = await db.Comments.AnyAsync(c => c.AuthorId == actor.UserId && db.CommentThreads.Any(t => t.Id == c.ThreadId && t.PageId == pageId), ct);
        if (!commentedBefore) await notifications.WatchAsync(actor.OrgId, pageId, actor.UserId, ct);
        await notifications.NotifyWatchersAsync(actor.OrgId, pageId, kind, $"thread:{threadId}", actor.UserId, told, ct: ct);
    }

    private async Task<CommentThread> ThreadAsync(Actor actor, Guid threadId, PageAction action, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var thread = await db.CommentThreads.SingleOrDefaultAsync(t => t.Id == threadId, ct) ?? throw new AtharError("not_found", "Thread not found.");
        await RequireAsync(actor, thread.PageId, action, ct);
        return thread;
    }

    private async Task<Comment> CommentAsync(Actor actor, Guid commentId, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == commentId && c.DeletedAt == null, ct) ?? throw new AtharError("not_found", "Comment not found.");
        await ThreadAsync(actor, comment.ThreadId, PageAction.Comment, ct);
        return comment;
    }

    /// <summary>Commenter and above (FR-CM-07); a viewer gets forbidden, someone who can't read the page not_found.</summary>
    private async Task RequireAsync(Actor actor, Guid pageId, PageAction action, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var caller = await db.ActorAsync(actor.UserId, actor.CredentialId, ct) ?? throw new AtharError("unauthorized", "The user is inactive or the credential is revoked.");
        if (action != PageAction.Read && caller.Agent && !actor.Scopes.Contains("write")) throw new AtharError("forbidden", "The token lacks the 'write' scope.");
        if (await db.CanAsync(caller, pageId, action, ct)) return;
        throw action != PageAction.Read && await db.CanAsync(caller, pageId, PageAction.Read, ct)
            ? new AtharError("forbidden", "You can read this page but not comment on it.")
            : new AtharError("not_found", "Page not found.");
    }

    private static string Body(string body) =>
        string.IsNullOrWhiteSpace(body) ? throw new AtharError("invalid_input", "A comment can't be empty.") : body.Trim();

    private static string Status(ThreadStatus s) => s switch
    {
        ThreadStatus.PendingAnchor => "pending_anchor",
        ThreadStatus.AnchorFailed => "anchor_failed",
        _ => s.ToString().ToLowerInvariant(),
    };

    private static ThreadView View(CommentThread t, IEnumerable<Comment> comments, IReadOnlyDictionary<Guid, string> names) => new(
        t.Id, Status(t.Status), t.Quote, t.AnchorId is not null && t.Status is ThreadStatus.PendingAnchor or ThreadStatus.Open or ThreadStatus.Resolved, t.ResolvedBy,
        [.. comments.Select(c => new CommentView(c.Id, c.AuthorId, names.GetValueOrDefault(c.AuthorId, "?"), c.DeletedAt is null ? c.BodyMd : null, c.CreatedAt, c.EditedAt, c.DeletedAt is not null))]);
}

/// <summary>Runs <see cref="CommentService.ReconcileAnchorsAsync"/> every 30 seconds (FR-CM-01).</summary>
public sealed class AnchorReconciler(IServiceScopeFactory scopes, ILogger<AnchorReconciler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var failed = await scope.ServiceProvider.GetRequiredService<CommentService>().ReconcileAnchorsAsync(ct);
                if (failed > 0) log.LogInformation("Anchor reconciler failed {Count} stale inline threads", failed);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Anchor reconciler pass failed");
            }
        }
    }
}
