using System.Text.Json;
using System.Text.RegularExpressions;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

/// <summary>A notification for the bell. <paramref name="Where"/> is the page title, when the person can still read the page; <paramref name="Who"/> is who caused it.</summary>
public sealed record NotificationView(Guid Id, string Kind, string Ref, string? Href, DateTimeOffset CreatedAt, bool Read, string? Where = null, string? Who = null);

public sealed record MentionCandidate(Guid Id, string Name);

/// <summary>
/// In-app notifications (FR-NT-01) and mentions (FR-CM-04). A mention or reply notifies only people who can see the page
/// right now (AT-17); notifications are added to the caller's unit of work, so they commit with the change that caused them.
/// </summary>
public sealed partial class NotificationService(AtharDbContext db)
{
    /// <summary>`@[Name](user:uuid)` (docs/06 §6.7).</summary>
    [GeneratedRegex(@"@\[(?<name>[^\]]*)\]\(user:(?<id>[0-9a-fA-F-]{36})\)")]
    public static partial Regex MentionPattern();

    public static HashSet<Guid> MentionsIn(string? markdown) =>
        markdown is null ? [] : [.. MentionPattern().Matches(markdown).Select(m => Guid.Parse(m.Groups["id"].Value))];

    /// <summary>User ids of the `mention` nodes in a ProseMirror JSON document.</summary>
    public static HashSet<Guid> MentionsIn(JsonElement doc)
    {
        var found = new HashSet<Guid>();
        void Walk(JsonElement n)
        {
            if (n.ValueKind != JsonValueKind.Object) return;
            if (n.TryGetProperty("type", out var t) && t.GetString() == "mention" && n.TryGetProperty("attrs", out var a)
                && a.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var user))
                found.Add(user);
            if (n.TryGetProperty("content", out var c))
                foreach (var child in c.EnumerateArray()) Walk(child);
        }
        Walk(doc);
        return found;
    }

    /// <summary>
    /// Queues one notification per recipient who can read <paramref name="pageId"/>, except <paramref name="except"/>.
    /// The caller saves. Returns who was notified.
    /// </summary>
    public async Task<List<Guid>> NotifyAsync(Guid orgId, IEnumerable<Guid> recipients, string kind, string reference, Guid pageId, Guid? except, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        var notified = new List<Guid>();
        foreach (var user in recipients.Distinct().Where(u => u != except))
        {
            if (!await db.CanAsync(new Caller(user), pageId, PageAction.Read, ct)) continue;
            db.Notifications.Add(new Notification { OrgId = orgId, UserId = user, Kind = kind, Ref = reference, ActorUserId = except });
            notified.Add(user);
        }
        return notified;
    }

    // ---------- watching (U-3, T118) ----------

    /// <summary>Watchers of an edited page get at most one update per page in this window (U-3).</summary>
    public static readonly TimeSpan UpdateWindow = TimeSpan.FromMinutes(30);

    /// <summary>Adds a watch if there isn't one (creating a page, a first comment). The caller saves.</summary>
    public async Task WatchAsync(Guid orgId, Guid pageId, Guid userId, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        if (db.PageWatches.Local.Any(w => w.PageId == pageId && w.UserId == userId)
            || await db.PageWatches.AnyAsync(w => w.PageId == pageId && w.UserId == userId, ct)) return;
        db.PageWatches.Add(new PageWatch { OrgId = orgId, PageId = pageId, UserId = userId });
    }

    public Task<bool> IsWatchingAsync(Guid orgId, Guid userId, Guid pageId, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        return db.PageWatches.AnyAsync(w => w.PageId == pageId && w.UserId == userId, ct);
    }

    /// <summary>Watch or unwatch from the page menu. Watching needs read access; both are idempotent. Saves.</summary>
    public async Task<bool> SetWatchAsync(Guid orgId, Guid userId, Guid pageId, bool watch, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        if (!watch)
        {
            await db.PageWatches.Where(w => w.PageId == pageId && w.UserId == userId).ExecuteDeleteAsync(ct);
            return true;
        }
        if (!await db.CanAsync(new Caller(userId), pageId, PageAction.Read, ct)) return false;
        await WatchAsync(orgId, pageId, userId, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Notifies the watchers of <paramref name="pageId"/>, except <paramref name="actor"/> and <paramref name="skip"/> (people who
    /// were already told about the same event, or who made the change). Visibility is checked now, through
    /// <see cref="NotifyAsync"/>, so a watcher who lost access (restricted page, a draft) gets nothing. With
    /// <paramref name="window"/>, a watcher who got this kind for this ref within it isn't notified again. The caller saves.
    /// </summary>
    public async Task<List<Guid>> NotifyWatchersAsync(Guid orgId, Guid pageId, string kind, string reference, Guid? actor,
        IEnumerable<Guid>? skip = null, TimeSpan? window = null, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        var exclude = new HashSet<Guid>(skip ?? []);
        if (actor is { } a) exclude.Add(a);
        var watchers = await db.PageWatches.Where(w => w.PageId == pageId).Select(w => w.UserId).ToListAsync(ct);
        watchers.RemoveAll(exclude.Contains);
        if (window is { } span && watchers.Count > 0)
        {
            var since = DateTimeOffset.UtcNow - span;
            var recent = await db.Notifications.Where(n => watchers.Contains(n.UserId) && n.Kind == kind && n.Ref == reference && n.CreatedAt > since)
                .Select(n => n.UserId).Distinct().ToListAsync(ct);
            watchers.RemoveAll(recent.Contains);
        }
        return await NotifyAsync(orgId, watchers, kind, reference, pageId, actor, ct);
    }

    /// <summary>People the mention picker offers: org members whose name matches and who can see the page.</summary>
    public async Task<List<MentionCandidate>> CandidatesAsync(Guid orgId, Guid pageId, string query, int limit = 8, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        var like = "%" + query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var users = await db.Users.Where(u => u.DeactivatedAt == null && EF.Functions.ILike(u.DisplayName, like, @"\"))
            .OrderBy(u => u.DisplayName).Take(limit * 3).Select(u => new MentionCandidate(u.Id, u.DisplayName)).ToListAsync(ct);
        var result = new List<MentionCandidate>();
        foreach (var u in users)
        {
            if (result.Count == limit) break;
            if (await db.CanAsync(new Caller(u.Id), pageId, PageAction.Read, ct)) result.Add(u);
        }
        return result;
    }

    public async Task<List<NotificationView>> ListAsync(Guid userId, Guid orgId, int limit = 30, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        var rows = await db.Notifications.AsNoTracking().Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt).Take(limit).ToListAsync(ct);
        // Thread refs link to their page; memory and source notifications link to the memory or the project (T150, T151).
        var threadIds = rows.Select(n => n.Ref).Where(r => r.StartsWith("thread:")).Select(r => Guid.TryParse(r[7..], out var g) ? g : Guid.Empty).ToList();
        var threadPages = await db.CommentThreads.AsNoTracking().Where(t => threadIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.PageId, ct);
        Guid? PageOf(string reference) => reference.Split(':', 2) switch
        {
            ["thread", var id] when Guid.TryParse(id, out var t) && threadPages.TryGetValue(t, out var page) => page,
            ["page", var id] when Guid.TryParse(id, out var p) => p,
            _ => null,
        };
        // The bell names the page ("… on Deploy runbook"), only for pages the person can still read.
        var titles = new Dictionary<Guid, string>();
        foreach (var page in rows.Select(n => PageOf(n.Ref)).OfType<Guid>().Distinct())
            if (await db.CanAsync(new Caller(userId), page, PageAction.Read, ct)
                && await db.Pages.AsNoTracking().Where(p => p.Id == page).Select(p => p.Title).FirstOrDefaultAsync(ct) is { } title)
                titles[page] = title;
        // A paused GitHub source leads to its project (T151), where the admin sets a new token.
        var sourceIds = rows.Where(n => n.Kind == "source_paused").Select(n => Guid.TryParse(n.Ref, out var g) ? g : Guid.Empty).ToList();
        var sourceProjects = await db.KnowledgeSources.AsNoTracking().Where(s => sourceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.ProjectId, ct);
        string? Target(Notification n) => n.Kind switch
        {
            "shadow_stale" when Guid.TryParse(n.Ref, out var memory) => $"/memories/{memory}",
            "source_paused" when Guid.TryParse(n.Ref, out var source) && sourceProjects.TryGetValue(source, out var project) => $"/projects/{project}",
            _ => null,
        };
        var actorIds = rows.Select(n => n.ActorUserId).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? Who(Notification n) => n.ActorUserId is { } a ? names.GetValueOrDefault(a) : null;
        return [.. rows.Select(n => PageOf(n.Ref) is { } page
            ? new NotificationView(n.Id, n.Kind, n.Ref, $"/wiki/p/{page}", n.CreatedAt, n.ReadAt is not null, titles.GetValueOrDefault(page), Who(n))
            : new NotificationView(n.Id, n.Kind, n.Ref, Target(n), n.CreatedAt, n.ReadAt is not null, Who: Who(n)))];
    }

    public Task<int> UnreadCountAsync(Guid userId, Guid orgId, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        return db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);
    }

    /// <summary>Marks the user's notifications read (one, or all when <paramref name="id"/> is null).</summary>
    public Task<int> MarkReadAsync(Guid userId, Guid orgId, Guid? id = null, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        return db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null && (id == null || n.Id == id))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
    }
}
