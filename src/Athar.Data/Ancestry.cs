using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.Data;

/// <summary>
/// <see cref="Page.AncestorIds"/> follows <see cref="Page.ParentId"/>: access predicates check restrictions on a page and
/// every ancestor in one query (FR-AC-01), without a recursive SQL copy of the rule.
/// </summary>
public partial class AtharDbContext
{
    /// <summary>
    /// For writes that bypass change tracking (ExecuteUpdate moves): recomputes every page of the given spaces from the
    /// parent pointers. Call it in the same transaction, after the move. Needs <see cref="CurrentOrgId"/>.
    /// </summary>
    public async Task RecomputeAncestryAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct = default)
    {
        var pages = await Pages.Where(p => spaceIds.Contains(p.SpaceId)).ToDictionaryAsync(p => p.Id, ct);
        Guid[] PathOf(Page p, int depth) =>
            depth > pages.Count ? throw new InvalidOperationException($"Page {p.Id} is in a parent cycle.")
            : p.ParentId is { } parent && pages.TryGetValue(parent, out var up) ? [.. PathOf(up, depth + 1), parent] : [];
        foreach (var page in pages.Values)
            if (PathOf(page, 0) is var path && !path.SequenceEqual(page.AncestorIds)) page.AncestorIds = path;
        await SaveChangesAsync(ct);
    }

    // Tracked creates and moves. A moved page's descendants get the new prefix too.
    private async Task MaintainAncestryAsync(CancellationToken ct)
    {
        var changed = ChangeTracker.Entries<Page>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified && e.Property(p => p.ParentId).IsModified)
            .Select(e => e.Entity).ToList();
        if (changed.Count == 0) return;

        var paths = new Dictionary<Guid, Guid[]>();
        var visiting = new HashSet<Guid>();
        async Task<Guid[]> PathOf(Page page)
        {
            if (page.ParentId is not { } parentId) return [];
            if (paths.TryGetValue(page.Id, out var known)) return known;
            if (!visiting.Add(page.Id)) throw new InvalidOperationException($"Page {page.Id} is in a parent cycle.");
            var parent = ChangeTracker.Entries<Page>().Select(e => e.Entity).FirstOrDefault(p => p.OrgId == page.OrgId && p.Id == parentId);
            var parentPath = parent is null
                ? await Pages.IgnoreQueryFilters().Where(p => p.OrgId == page.OrgId && p.Id == parentId).Select(p => p.AncestorIds).SingleOrDefaultAsync(ct) ?? []
                : changed.Contains(parent) ? await PathOf(parent) : parent.AncestorIds;
            return paths[page.Id] = [.. parentPath, parentId];
        }

        foreach (var page in changed)
        {
            var path = await PathOf(page);
            if (path.SequenceEqual(page.AncestorIds)) continue;
            page.AncestorIds = path;
            if (Entry(page).State != EntityState.Modified) continue;
            var descendants = await Pages.IgnoreQueryFilters().Where(p => p.OrgId == page.OrgId && p.AncestorIds.Contains(page.Id)).ToListAsync(ct);
            foreach (var d in descendants.Where(d => !changed.Contains(d)))
                d.AncestorIds = [.. path, page.Id, .. d.AncestorIds.SkipWhile(a => a != page.Id).Skip(1)];
        }
    }
}
