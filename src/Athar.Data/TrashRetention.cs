using Athar.Domain.Wiki;
using Microsoft.EntityFrameworkCore;

namespace Athar.Data;

/// <summary>
/// FR-PG-01: pages trashed more than 30 days ago are deleted for good, children first, with one audit row per org and no
/// actor. Runs daily in the worker for every org; <c>PageService.PurgeAsync</c> calls the same code.
/// </summary>
public static class TrashRetention
{
    /// <summary>Purges one org; returns how many pages went.</summary>
    public static async Task<int> PurgeAsync(AtharDbContext db, Guid orgId, DateTimeOffset now, CancellationToken ct = default)
    {
        db.CurrentOrgId = orgId;
        var cutoff = now - PageTree.TrashRetention;
        var expired = await db.Pages.Where(p => p.DeletedAt != null && p.DeletedAt <= cutoff).Select(p => new { p.Id, p.ParentId }).ToListAsync(ct);
        if (expired.Count == 0) return 0;
        var ids = expired.Select(p => p.Id).ToHashSet();
        // Deepest first, so no row is deleted while a child still points at it.
        var roots = expired.Where(p => p.ParentId is null || !ids.Contains(p.ParentId.Value)).Select(p => p.Id);
        var ordered = roots.SelectMany(r => PageTree.Subtree(r, expired.Select(p => (p.Id, p.ParentId)))).Reverse().ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var id in ordered) await db.Pages.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        db.Audit(orgId, null, "page.purge", "page", string.Join(',', ordered));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ordered.Count;
    }

    /// <summary>Purges every org; returns how many pages went in total.</summary>
    public static async Task<int> PurgeAllAsync(AtharDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var org in await db.Organizations.Select(o => o.Id).ToListAsync(ct))
            total += await PurgeAsync(db, org, now, ct);
        return total;
    }
}
