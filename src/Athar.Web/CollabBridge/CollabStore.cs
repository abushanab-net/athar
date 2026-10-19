using System.Text.Json;
using Athar.Data;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Athar.Web.Services;
using Npgsql;

namespace Athar.Web.CollabBridge;

public sealed record CollabBatch(string BatchId, Guid User, Guid? Credential, string[] OpIds);

/// <summary>Body of `PUT /internal/collab/pages/{id}` (docs/06 §6.4).</summary>
public sealed record CollabPersist(
    byte[] Ydoc, JsonElement Json, string Markdown, long BaseRevision, long LiveEpoch, long LiveSeq, CollabBatch[] Batches, string[] OpIds);

/// <summary>`OpIds`: ops this page persisted within the operations retention (7 days); collab never applies them again (FR-RT-07).
/// `LiveEpoch`/`LiveSeq`: the live token the stored state was persisted at (what `get_page` reports for an unloaded page).
/// `Json`/`Markdown`: only for a page stored without Yjs state (seeded or imported), so collab can build the live document from them.</summary>
public sealed record CollabState(byte[]? Ydoc, long Revision, string[]? OpIds = null, long LiveEpoch = 0, long LiveSeq = 0, JsonElement? Json = null, string? Markdown = null);

/// <summary>
/// Stored page state for collab: the `pages` row and its `page_revisions` (docs/03 §4.1 step 5).
/// </summary>
public sealed class CollabStore(AtharDbContext db, TimeProvider clock, NotificationService notifications)
{
    /// <summary>The live page with its org, whatever org the caller is in (collab knows only the page id).</summary>
    public Task<Page?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.Pages.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);

    /// <summary>Stores a new revision if <c>BaseRevision</c> matches; otherwise returns the stored state for the 409.</summary>
    public async Task<(long? Revision, CollabState? Stored)> PersistAsync(Guid id, CollabPersist r, CancellationToken ct = default)
    {
        var page = await db.Pages.IgnoreQueryFilters().SingleOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
        if (page is null) return (null, null);
        if (r.BaseRevision != page.Revision) return (null, new(page.Ydoc, page.Revision));
        db.CurrentOrgId = page.OrgId;

        page.Ydoc = r.Ydoc;
        page.ContentJson = r.Json.GetRawText();
        page.ContentMd = r.Markdown;
        page.Revision++;
        page.LiveEpoch = r.LiveEpoch;
        page.LiveSeq = r.LiveSeq;
        var contributors = r.Batches.Select(b => b.User).Distinct().ToArray();
        db.PageRevisions.Add(new PageRevision
        {
            PageId = id, Revision = page.Revision, LiveSeq = r.LiveSeq, Contributors = contributors,
            AgentCredentials = [.. r.Batches.Select(b => b.Credential).OfType<Guid>().Distinct()],
            BatchIds = [.. r.Batches.Select(b => b.BatchId)], OpIds = r.OpIds,
        });
        // A revision can have several contributors; they're all in `after`. A single one is also the actor.
        db.Audit(page.OrgId, contributors.Length == 1 ? contributors[0] : null, "page.revision", "page", id,
            after: new { revision = page.Revision, contributors, batch_ids = r.Batches.Select(b => b.BatchId), op_ids = r.OpIds });
        // The page ops this revision carries are done (FR-RT-07): a lost response or a collab restart can't leave them pending.
        if (r.OpIds.Length > 0)
        {
            var token = $"{r.LiveEpoch}:{r.LiveSeq}";
            var target = id.ToString();
            foreach (var op in await db.Operations.Where(o => r.OpIds.Contains(o.OpId) && o.Status == OperationStatus.Pending
                         && o.TargetKind == OperationTarget.Page && o.TargetId == target).ToListAsync(ct))
            {
                op.Status = OperationStatus.Done;
                op.Result = JsonSerializer.Serialize(new PageOpResult(id, page.Revision, token, []), CollabEndpoints.Json);
            }
        }
        // FR-CM-01/02: the persist holding an anchorComment op opens its thread; an open inline thread whose anchor is gone
        // from the persisted content is orphaned (it keeps its quote and becomes page-level), detected here without a browser.
        var anchored = Anchors(r.Json);
        foreach (var thread in await db.CommentThreads.Where(t => t.PageId == id && t.AnchorId != null
                     && (t.Status == ThreadStatus.Open || t.Status == ThreadStatus.PendingAnchor)).ToListAsync(ct))
        {
            if (thread.Status == ThreadStatus.PendingAnchor && thread.OpId is { } op && r.OpIds.Contains(op) && anchored.Contains(thread.Id.ToString()))
                thread.Status = ThreadStatus.Open;
            else if (thread.Status == ThreadStatus.Open && !anchored.Contains(thread.AnchorId!))
            {
                thread.Status = ThreadStatus.Orphaned;
                db.Audit(page.OrgId, null, "comment.orphaned", "comment_thread", thread.Id, new { status = "open" }, new { status = "orphaned" });
            }
        }
        // FR-CM-04: people newly @mentioned in the page are notified if they can see it; removed mentions are forgotten.
        var mentioned = NotificationService.MentionsIn(r.Json);
        var known = await db.Mentions.Where(m => m.SourceKind == MentionSource.Page && m.SourceId == id).ToListAsync(ct);
        db.Mentions.RemoveRange(known.Where(m => !mentioned.Contains(m.UserId)));
        var fresh = mentioned.Except(known.Select(m => m.UserId)).ToList();
        Guid? author = contributors.Length == 1 ? contributors[0] : null;
        List<Guid> toldOfMention = [];
        if (fresh.Count > 0)
        {
            toldOfMention = await notifications.NotifyAsync(page.OrgId, fresh, "mention", $"page:{id}", id, author, ct);
            foreach (var user in toldOfMention) db.Mentions.Add(new Mention { SourceKind = MentionSource.Page, SourceId = id, UserId = user });
        }
        // U-3 (T118): watchers hear the page changed, at most once per page every 30 minutes each, and never the people who
        // edited it. Persists come every few seconds while someone types; the window, not the persist, sets the pace.
        if (contributors.Length > 0)
            await notifications.NotifyWatchersAsync(page.OrgId, id, "page_updated", $"page:{id}", author, [.. contributors, .. toldOfMention], NotificationService.UpdateWindow, ct);

        // FR-PG-05: an automatic version at most every 5 minutes while the page is edited (restore and the versions panel use them).
        var recent = clock.GetUtcNow() - VersionInterval;
        if (!await db.PageVersions.AnyAsync(v => v.PageId == id && v.CreatedAt > recent, ct))
            db.PageVersions.Add(new PageVersion { PageId = id, Revision = page.Revision, ContentJson = page.ContentJson, ContentMd = page.ContentMd, Reason = VersionReason.Auto });
        // Re-embed this revision; the worker drops it if the page has moved on (docs/03 §7).
        db.Jobs.Add(new Job
        {
            Kind = "embed-page", Key = $"{id}:{page.Revision}", RunAfter = clock.GetUtcNow(),
            Payload = JsonSerializer.Serialize(new { page_id = id, revision = page.Revision }),
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return (page.Revision, null);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent persist took this revision number first: answer like any stale base.
            db.ChangeTracker.Clear();
            var stored = await db.Pages.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id, ct);
            return (null, new(stored.Ydoc, stored.Revision));
        }
    }

    /// <summary>The stored state collab loads, with the op ids it must not apply again.</summary>
    public async Task<CollabState?> StateAsync(Guid id, CancellationToken ct = default)
    {
        if (await FindAsync(id, ct) is not { } page) return null;
        var since = clock.GetUtcNow() - OpRetention;
        var opIds = await db.PageRevisions.IgnoreQueryFilters()
            .Where(r => r.OrgId == page.OrgId && r.PageId == id && r.CreatedAt >= since && r.OpIds.Length > 0)
            .SelectMany(r => r.OpIds).ToArrayAsync(ct);
        var noYjs = page.Ydoc is null;
        return new(page.Ydoc, page.Revision, opIds, page.LiveEpoch, page.LiveSeq,
            noYjs && page.ContentJson is { } json ? JsonSerializer.Deserialize<JsonElement>(json) : null, noYjs ? page.ContentMd : null);
    }

    public static readonly TimeSpan VersionInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long operations are kept (FR-RT-07).</summary>
    public static readonly TimeSpan OpRetention = TimeSpan.FromDays(7);

    /// <summary>Thread ids of every <c>commentAnchor</c> mark in a ProseMirror JSON document.</summary>
    private static HashSet<string> Anchors(JsonElement node)
    {
        var found = new HashSet<string>();
        void Walk(JsonElement n)
        {
            if (n.ValueKind != JsonValueKind.Object) return;
            if (n.TryGetProperty("marks", out var marks))
                foreach (var m in marks.EnumerateArray())
                    if (m.GetProperty("type").GetString() == "commentAnchor" && m.TryGetProperty("attrs", out var at) && at.TryGetProperty("threadId", out var t) && t.GetString() is { } id)
                        found.Add(id);
            if (n.TryGetProperty("content", out var content))
                foreach (var c in content.EnumerateArray()) Walk(c);
        }
        Walk(node);
        return found;
    }

    public Task<List<PageRevision>> RevisionsAsync(Guid id, CancellationToken ct = default) =>
        db.PageRevisions.IgnoreQueryFilters().AsNoTracking().Where(r => r.PageId == id).OrderBy(r => r.Revision).ToListAsync(ct);
}
