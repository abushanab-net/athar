using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

/// <summary>Who is acting on the wiki.</summary>
public sealed record WikiActor(Guid UserId, Guid OrgId);

/// <summary>Where a dragged page lands relative to the page it is dropped on.</summary>
public enum DropPlace { Before, After, Inside }

public sealed record PageNode(Guid Id, Guid SpaceId, Guid? ParentId, string Title, double Position, DateTimeOffset? DeletedAt, PageStatus Status = PageStatus.Published);
public sealed record RecentPage(Guid Id, string Title, Guid SpaceId, string Space, DateTimeOffset UpdatedAt);
public sealed record PageBrief(string Title, string Space, DateTimeOffset UpdatedAt);

/// <summary>
/// Page tree use cases (FR-PG-01). Content never changes here: it goes through collab (docs/03 §4.2).
/// Access goes through <see cref="Athar.Domain.Access"/> (T012): page rights for reading and changing a page (restrictions and
/// shares included), the space role only for creating a top-level page. Every mutation adds its audit row
/// (<see cref="AuditWriter"/>) to the same SaveChanges.
/// </summary>
public sealed class PageService(AtharDbContext db, TimeProvider clock)
{
    /// <summary>The actor's effective role in a space (direct or through a team), or null.</summary>
    public Task<SpaceRole?> RoleAsync(WikiActor a, Guid spaceId, CancellationToken ct = default)
    {
        Who(a);
        return db.SpaceRoleAsync(a.UserId, spaceId, ct);
    }

    /// <summary>Spaces the actor can see (FR-WS-01), with their workspace name and the actor's role (null when only a page is shared).</summary>
    public async Task<List<(Space Space, string Workspace, SpaceRole? Role)>> SpacesAsync(WikiActor a, CancellationToken ct = default)
    {
        var spaces = await db.SpacesFor(Who(a)).OrderBy(s => s.Name).ToListAsync(ct);
        var workspaces = await db.Workspaces.ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var result = new List<(Space, string, SpaceRole?)>();
        foreach (var s in spaces) result.Add((s, workspaces[s.WorkspaceId], await RoleAsync(a, s.Id, ct)));
        return result;
    }

    /// <summary>Live pages the actor can read, most recently changed first, with their space (the signed-in home).</summary>
    public async Task<List<RecentPage>> RecentAsync(WikiActor a, int limit = 8, CancellationToken ct = default) =>
        await db.PagesFor(Who(a)).AsNoTracking().Where(p => p.DeletedAt == null).OrderByDescending(p => p.UpdatedAt).Take(limit)
            .Join(db.Spaces, p => p.SpaceId, s => s.Id, (p, s) => new RecentPage(p.Id, p.Title, s.Id, s.Name, p.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>Title, space and last change of the live pages the actor can read, by id (search results, proposal evidence).</summary>
    public async Task<Dictionary<Guid, PageBrief>> BriefsAsync(WikiActor a, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        return await db.PagesFor(Who(a)).AsNoTracking().Where(p => p.DeletedAt == null && list.Contains(p.Id))
            .Join(db.Spaces, p => p.SpaceId, s => s.Id, (p, s) => new { p.Id, p.Title, Space = s.Name, p.UpdatedAt })
            .ToDictionaryAsync(x => x.Id, x => new PageBrief(x.Title, x.Space, x.UpdatedAt), ct);
    }

    /// <summary>What the actor may do on one page (read, comment, edit).</summary>
    public Task<PageRights> RightsAsync(WikiActor a, Guid pageId, CancellationToken ct = default) => db.RightsAsync(Who(a), pageId, ct);

    /// <summary>The pages of a space the actor can read (<paramref name="trash"/> = the trashed ones instead).</summary>
    public async Task<List<PageNode>> TreeAsync(WikiActor a, Guid spaceId, bool trash = false, CancellationToken ct = default)
    {
        var caller = Who(a);
        if (!await db.SpacesFor(caller).AnyAsync(s => s.Id == spaceId, ct)) throw new AtharError("not_found", "Space not found.");
        return await db.PagesFor(caller).Where(p => p.SpaceId == spaceId && (p.DeletedAt != null) == trash)
            .OrderBy(p => p.Position)
            .Select(p => new PageNode(p.Id, p.SpaceId, p.ParentId, p.Title, p.Position, p.DeletedAt, p.Status))
            .ToListAsync(ct);
    }

    /// <summary>Live pages whose title contains <paramref name="query"/>, across the spaces the actor can read (the page-link picker).</summary>
    public async Task<List<PageNode>> SearchAsync(WikiActor a, string query, int limit = 8, CancellationToken ct = default)
    {
        var like = "%" + query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        return await db.PagesFor(Who(a)).Where(p => p.DeletedAt == null && EF.Functions.ILike(p.Title, like, @"\"))
            .OrderBy(p => p.Title).Take(limit)
            .Select(p => new PageNode(p.Id, p.SpaceId, p.ParentId, p.Title, p.Position, p.DeletedAt, p.Status))
            .ToListAsync(ct);
    }

    public async Task<Page> GetAsync(WikiActor a, Guid pageId, CancellationToken ct = default)
    {
        var page = await db.PagesFor(Who(a)).AsNoTracking().SingleOrDefaultAsync(p => p.Id == pageId && p.DeletedAt == null, ct)
            ?? throw new AtharError("not_found", "Page not found.");
        return page;
    }

    /// <param name="id">The new page's id, when the caller chose it (a durable create records its op first).</param>
    /// <param name="draft">U-1: pages made in the wiki start as drafts; agents' pages (PageOps) are published.</param>
    public async Task<Guid> CreateAsync(WikiActor a, Guid spaceId, Guid? parentId, string title, CancellationToken ct = default, Guid? id = null, bool draft = false)
    {
        title = Title(title);
        await RequireCanPlaceAsync(a, spaceId, parentId, ct);
        var last = await db.Pages.Where(x => x.SpaceId == spaceId && x.ParentId == parentId).MaxAsync(x => (double?)x.Position, ct);
        var page = new Page
        {
            Id = id ?? Guid.CreateVersion7(), SpaceId = spaceId, ParentId = parentId, Title = title, Position = PageTree.PositionBetween(last, null),
            CreatedBy = a.UserId, Status = draft ? PageStatus.Draft : PageStatus.Published,
        };
        db.Pages.Add(page);
        // U-3 (T118): the creator watches the page. ponytail: NotificationService is a thin wrapper over this db, so no new ctor dependency.
        await new NotificationService(db).WatchAsync(a.OrgId, page.Id, a.UserId, ct);
        Audit(a, "page.create", page.Id, new { space_id = spaceId, parent_id = parentId, title, status = page.Status });
        await db.SaveChangesAsync(ct);
        return page.Id;
    }

    /// <summary>U-1: makes a draft visible to everyone who can read the page (and to agents, per the space's AI policy).</summary>
    /// <summary>Display names for a page's byline (created by, published by), T115.</summary>
    public async Task<Dictionary<Guid, string>> NamesAsync(WikiActor a, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        db.CurrentOrgId = a.OrgId;
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <summary>The byline's author: who made the page, else who published it, else the first revision's first contributor
    /// (pages made before Phase U have no CreatedBy).</summary>
    public async Task<Guid?> AuthorAsync(WikiActor a, Page page, CancellationToken ct = default)
    {
        if ((page.CreatedBy ?? page.PublishedBy) is { } known) return known;
        db.CurrentOrgId = a.OrgId;
        var first = await db.PageRevisions.AsNoTracking().Where(r => r.PageId == page.Id).OrderBy(r => r.Revision)
            .Select(r => r.Contributors).FirstOrDefaultAsync(ct);
        return first is { Length: > 0 } ? first[0] : null;
    }

    public Task PublishAsync(WikiActor a, Guid pageId, CancellationToken ct = default) => SetStatusAsync(a, pageId, PageStatus.Published, ct);

    /// <summary>U-1: back to a draft, visible only to people who can edit it.</summary>
    public Task UnpublishAsync(WikiActor a, Guid pageId, CancellationToken ct = default) => SetStatusAsync(a, pageId, PageStatus.Draft, ct);

    private async Task SetStatusAsync(WikiActor a, Guid pageId, PageStatus status, CancellationToken ct)
    {
        var page = await LiveAsync(a, pageId, ct); // edit rights
        if (page.Status == status) return;
        var before = page.Status;
        page.Status = status;
        if (status == PageStatus.Published) (page.PublishedAt, page.PublishedBy) = (clock.GetUtcNow(), a.UserId);
        Audit(a, status == PageStatus.Published ? "page.publish" : "page.unpublish", page.Id, new { status }, new { status = before });
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(WikiActor a, Guid pageId, string title, CancellationToken ct = default)
    {
        var page = await LiveAsync(a, pageId, ct);
        var before = page.Title;
        page.Title = Title(title);
        Audit(a, "page.rename", page.Id, new { title = page.Title }, new { title = before });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves a page and its subtree under <paramref name="newParentId"/> (null = top level) in a space of the same workspace, as the last child.</summary>
    public async Task MoveAsync(WikiActor a, Guid pageId, Guid targetSpaceId, Guid? newParentId, CancellationToken ct = default)
    {
        var page = await LiveAsync(a, pageId, ct);
        await RequireCanPlaceAsync(a, targetSpaceId, newParentId, ct);
        var sourceSpaceId = page.SpaceId;
        var spaces = await db.Spaces.Where(s => s.Id == page.SpaceId || s.Id == targetSpaceId).Select(s => s.WorkspaceId).Distinct().CountAsync(ct);
        if (spaces != 1) throw new AtharError("invalid_input", "Pages move only between spaces of the same workspace.");

        var target = await db.Pages.Where(p => p.SpaceId == targetSpaceId).Select(p => new { p.Id, p.ParentId, p.DeletedAt }).ToListAsync(ct);
        if (PageTree.IsCycle(pageId, newParentId, target.ToDictionary(p => p.Id, p => p.ParentId)))
            throw new AtharError("invalid_input", "A page can't move under itself or one of its descendants.");

        var last = await db.Pages.Where(x => x.SpaceId == targetSpaceId && x.ParentId == newParentId && x.Id != pageId).MaxAsync(x => (double?)x.Position, ct);
        var from = new { space_id = page.SpaceId, parent_id = page.ParentId };
        page.Position = PageTree.PositionBetween(last, null);
        Audit(a, "page.move", page.Id, new { space_id = targetSpaceId, parent_id = newParentId }, from);

        if (targetSpaceId == page.SpaceId)
        {
            page.ParentId = newParentId;
            await db.SaveChangesAsync(ct);
            return;
        }

        // Across spaces the whole subtree changes space. SpaceId is part of the key the parent FK points at, so EF can't
        // update it on tracked rows: detach the subtree's parent links, move the rows, then relink, in one transaction.
        var source = await db.Pages.Where(p => p.SpaceId == page.SpaceId).Select(p => new { p.Id, p.ParentId }).ToListAsync(ct);
        var subtree = PageTree.Subtree(pageId, source.Select(p => (p.Id, p.ParentId)));
        var parents = source.Where(p => subtree.Contains(p.Id) && p.Id != pageId).ToLookup(p => p.ParentId!.Value, p => p.Id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct); // position and audit row
        await db.Pages.Where(p => subtree.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.ParentId, (Guid?)null), ct);
        await db.Pages.Where(p => subtree.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.SpaceId, targetSpaceId), ct);
        await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(s => s.SetProperty(p => p.ParentId, newParentId), ct);
        foreach (var g in parents)
        {
            Guid? parent = g.Key;
            var children = g.ToList();
            await db.Pages.Where(p => children.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.ParentId, parent), ct);
        }
        // ExecuteUpdate bypasses change tracking: drop the stale tracked rows, then rebuild the ancestry the access rules
        // read (T012) from the new parent pointers.
        db.ChangeTracker.Clear();
        await db.RecomputeAncestryAsync([sourceSpaceId, targetSpaceId], ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Drag and drop in the tree: puts a page (with its subtree) <paramref name="where"/> relative to <paramref name="targetId"/>:
    /// inside it as the last child, or just before or after it as a sibling, which also moves a page out of its parent.
    /// The parent change goes through <see cref="MoveAsync"/> (edit rights, cycles, audit); then the position.
    /// </summary>
    public async Task DropAsync(WikiActor a, Guid pageId, Guid targetId, DropPlace where, CancellationToken ct = default)
    {
        if (pageId == targetId) return;
        var caller = Who(a);
        var target = await db.Pages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == targetId && p.DeletedAt == null, ct)
            ?? throw new AtharError("not_found", "Page not found.");
        await RequirePageAsync(caller, targetId, PageAction.Read, ct);
        var parent = where == DropPlace.Inside ? target.Id : target.ParentId;
        var page = await LiveAsync(a, pageId, ct);
        if (page.ParentId != parent || page.SpaceId != target.SpaceId)
        {
            await MoveAsync(a, pageId, target.SpaceId, parent, ct); // as the last child, audited
            db.ChangeTracker.Clear();
            page = await LiveAsync(a, pageId, ct);
        }
        if (where == DropPlace.Inside) return;

        var siblings = await db.Pages.Where(p => p.SpaceId == target.SpaceId && p.ParentId == parent && p.DeletedAt == null && p.Id != pageId)
            .OrderBy(p => p.Position).Select(p => new { p.Id, p.Position }).ToListAsync(ct);
        var i = siblings.FindIndex(p => p.Id == targetId);
        double? Pos(int k) => k >= 0 && k < siblings.Count ? siblings[k].Position : null;
        page.Position = where == DropPlace.Before ? PageTree.PositionBetween(Pos(i - 1), Pos(i)) : PageTree.PositionBetween(Pos(i), Pos(i + 1));
        Audit(a, "page.reorder", page.Id, new { position = page.Position });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves a page one place up (-1) or down (+1) among its siblings.</summary>
    public async Task ReorderAsync(WikiActor a, Guid pageId, int direction, CancellationToken ct = default)
    {
        var page = await LiveAsync(a, pageId, ct);
        var siblings = await db.Pages.Where(p => p.SpaceId == page.SpaceId && p.ParentId == page.ParentId && p.DeletedAt == null)
            .OrderBy(p => p.Position).Select(p => p.Position).ToListAsync(ct);
        var i = siblings.IndexOf(page.Position);
        var j = i + Math.Sign(direction);
        if (j < 0 || j >= siblings.Count) return;
        // Land on the far side of the neighbour: between it and the one after it.
        var beyond = j + Math.Sign(direction);
        double? far = beyond >= 0 && beyond < siblings.Count ? siblings[beyond] : null;
        page.Position = direction < 0 ? PageTree.PositionBetween(far, siblings[j]) : PageTree.PositionBetween(siblings[j], far);
        Audit(a, "page.reorder", page.Id, new { position = page.Position });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Trashes a page and its live descendants together; they can be restored for 30 days.</summary>
    public async Task<DateTimeOffset> TrashAsync(WikiActor a, Guid pageId, CancellationToken ct = default)
    {
        var page = await LiveAsync(a, pageId, ct);
        var now = clock.GetUtcNow();
        var pages = await db.Pages.Where(p => p.SpaceId == page.SpaceId && p.DeletedAt == null).ToListAsync(ct);
        var subtree = PageTree.Subtree(pageId, pages.Select(p => (p.Id, p.ParentId))).ToHashSet();
        foreach (var p in pages.Where(p => subtree.Contains(p.Id))) p.DeletedAt = now;
        // U-3 (T118): watchers of every trashed page hear about it (not the person who trashed it).
        var notifications = new NotificationService(db);
        foreach (var trashed in subtree) await notifications.NotifyWatchersAsync(a.OrgId, trashed, "page_trashed", $"page:{trashed}", a.UserId, ct: ct);
        Audit(a, "page.trash", page.Id, new { pages = subtree.Count, restore_until = now + PageTree.TrashRetention });
        await db.SaveChangesAsync(ct);
        return now + PageTree.TrashRetention;
    }

    /// <summary>Restores a trashed page with the descendants trashed alongside it. If its parent is gone, it returns at the top level.</summary>
    public async Task RestoreAsync(WikiActor a, Guid pageId, CancellationToken ct = default)
    {
        var caller = Who(a);
        var page = await db.Pages.SingleOrDefaultAsync(p => p.Id == pageId && p.DeletedAt != null, ct)
            ?? throw new AtharError("not_found", "No trashed page with that id.");
        await RequirePageAsync(caller, pageId, PageAction.Edit, ct);
        var deletedAt = page.DeletedAt!.Value;
        if (!PageTree.CanRestore(deletedAt, clock.GetUtcNow())) throw new AtharError("not_found", "The page was purged.");

        var pages = await db.Pages.Where(p => p.SpaceId == page.SpaceId).ToListAsync(ct);
        var subtree = PageTree.Subtree(pageId, pages.Select(p => (p.Id, p.ParentId))).ToHashSet();
        foreach (var p in pages.Where(p => subtree.Contains(p.Id) && p.DeletedAt == deletedAt)) p.DeletedAt = null;
        if (page.ParentId is { } parent && pages.Single(p => p.Id == parent).DeletedAt is not null) page.ParentId = null;
        Audit(a, "page.restore", page.Id, new { pages = subtree.Count });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes pages trashed more than 30 days ago in the org, children first. Returns how many went.</summary>
    public Task<int> PurgeAsync(Guid orgId, CancellationToken ct = default) => TrashRetention.PurgeAsync(db, orgId, clock.GetUtcNow(), ct);

    private Caller Who(WikiActor a)
    {
        db.CurrentOrgId = a.OrgId;
        return new Caller(a.UserId);
    }

    /// <summary>A live page the actor may edit, tracked for the change.</summary>
    private async Task<Page> LiveAsync(WikiActor a, Guid pageId, CancellationToken ct)
    {
        var caller = Who(a);
        var page = await db.Pages.SingleOrDefaultAsync(p => p.Id == pageId && p.DeletedAt == null, ct)
            ?? throw new AtharError("not_found", "Page not found.");
        await RequirePageAsync(caller, pageId, PageAction.Edit, ct);
        return page;
    }

    /// <summary>Not found when the actor can't even read the page, so ids don't leak; forbidden when they can read it.</summary>
    private async Task RequirePageAsync(Caller caller, Guid pageId, PageAction action, CancellationToken ct)
    {
        if (await db.CanAsync(caller, pageId, action, ct)) return;
        throw action != PageAction.Read && await db.CanAsync(caller, pageId, PageAction.Read, ct)
            ? new AtharError("forbidden", "You can't change this page.")
            : new AtharError("not_found", "Page not found.");
    }

    /// <summary>
    /// Creating or moving a page under <paramref name="parentId"/> needs edit rights on that parent (its restrictions apply
    /// to the new child); at the top level it needs the editor role in the space.
    /// </summary>
    public async Task RequireCanPlaceAsync(WikiActor a, Guid spaceId, Guid? parentId, CancellationToken ct)
    {
        var caller = Who(a);
        if (parentId is { } parent)
        {
            if (!await db.Pages.AnyAsync(p => p.Id == parent && p.SpaceId == spaceId && p.DeletedAt == null, ct))
                throw new AtharError("not_found", "Parent page not found in that space.");
            await RequirePageAsync(caller, parent, PageAction.Edit, ct);
            return;
        }
        if (!await db.SpacesFor(caller).AnyAsync(s => s.Id == spaceId, ct)) throw new AtharError("not_found", "Space not found.");
        if (await db.SpaceRoleAsync(a.UserId, spaceId, ct) is not (SpaceRole.Editor or SpaceRole.Admin))
            throw new AtharError("forbidden", "This needs the editor role in the space.");
    }

    private static string Title(string title) =>
        string.IsNullOrWhiteSpace(title) ? throw new AtharError("invalid_input", "A page needs a title.") : title.Trim();

    private void Audit(WikiActor a, string action, Guid pageId, object after, object? before = null) =>
        db.Audit(a.OrgId, a.UserId, action, "page", pageId, before, after);
}
