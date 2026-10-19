using Microsoft.EntityFrameworkCore;

namespace Athar.Data;

/// <summary>
/// FR-AG-04 / AT-53: transcript records go 30 days after receipt, whatever the extraction status, and at once when
/// their owner is deactivated. The worker runs <see cref="PurgeAllAsync"/> daily; deactivation calls
/// <see cref="PurgeOwnerAsync"/> in its own transaction. Sessions, commits and candidates stay (no transcript content).
/// </summary>
public static class SessionRetention
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    public static Task<int> PurgeOwnerAsync(AtharDbContext db, Guid userId, CancellationToken ct = default) =>
        db.SessionRecords.Where(r => db.AgentSessions.Any(s => s.Id == r.SessionId && s.UserId == userId)).ExecuteDeleteAsync(ct);

    /// <summary>Expired records, and any record whose owner is deactivated, in every org.</summary>
    public static async Task<int> PurgeAllAsync(AtharDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var org in await db.Organizations.Select(o => o.Id).ToListAsync(ct))
        {
            db.CurrentOrgId = org;
            var deleted = await db.SessionRecords
                .Where(r => r.PurgeAfter <= now
                    || db.AgentSessions.Any(s => s.Id == r.SessionId && db.Users.Any(u => u.Id == s.UserId && u.DeactivatedAt != null)))
                .ExecuteDeleteAsync(ct);
            if (deleted == 0) continue;
            db.Audit(org, null, "session.purge", "session_record", "*", after: new { records = deleted });
            await db.SaveChangesAsync(ct);
            total += deleted;
        }
        return total;
    }
}
