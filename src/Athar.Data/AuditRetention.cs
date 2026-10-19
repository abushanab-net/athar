using Microsoft.EntityFrameworkCore;

namespace Athar.Data;

/// <summary>
/// FR-AU-04: the only code path that deletes audit rows (a CI check enforces it). Rows older than the retention period go,
/// across every org, in one statement; recent rows stay. Runs daily in the worker.
/// </summary>
public static class AuditRetention
{
    public const int DefaultDays = 90;

    public static Task<int> PurgeAsync(AtharDbContext db, DateTimeOffset now, int days = DefaultDays, CancellationToken ct = default)
    {
        var cutoff = now.AddDays(-days);
        return db.AuditEvents.IgnoreQueryFilters().Where(e => e.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
