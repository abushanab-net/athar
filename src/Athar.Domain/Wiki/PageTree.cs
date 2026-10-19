namespace Athar.Domain.Wiki;

/// <summary>Page tree rules (FR-PG-01). Plain C#: callers load the tree, the rules decide.</summary>
public static class PageTree
{
    /// <summary>Trashed pages can be restored for this long, then they're purged.</summary>
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// A page can't move under itself or under one of its descendants. <paramref name="parentOf"/> maps every page in
    /// the target space to its parent (null for a root).
    /// </summary>
    public static bool IsCycle(Guid page, Guid? newParent, IReadOnlyDictionary<Guid, Guid?> parentOf)
    {
        // Walk up from the new parent; meeting the page means the new parent is the page or one of its descendants.
        // The step bound stops a walk around a corrupt loop.
        for (var (at, steps) = (newParent, 0); at is { } id && steps <= parentOf.Count; at = parentOf.GetValueOrDefault(id), steps++)
            if (id == page) return true;
        return false;
    }

    /// <summary>The page and every descendant, parents before children.</summary>
    public static List<Guid> Subtree(Guid root, IEnumerable<(Guid Id, Guid? ParentId)> pages)
    {
        var children = pages.Where(p => p.ParentId is not null).ToLookup(p => p.ParentId!.Value, p => p.Id);
        var result = new List<Guid> { root };
        for (var i = 0; i < result.Count; i++) result.AddRange(children[result[i]]);
        return result;
    }

    /// <summary>A position between two neighbours (null = no neighbour on that side). Fractional, so a reorder updates one row.</summary>
    public static double PositionBetween(double? before, double? after) => (before, after) switch
    {
        (null, null) => 1,
        ({ } b, null) => b + 1,
        (null, { } a) => a - 1,
        ({ } b, { } a) => (b + a) / 2,
    };

    public static bool CanRestore(DateTimeOffset deletedAt, DateTimeOffset now) => now - deletedAt < TrashRetention;
}
