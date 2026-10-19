using Athar.Data;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.Worker.Jobs.Retention;

/// <summary>
/// FR-AG-12c: rolls one UTC day of audit events, <c>mcp_calls</c> and <c>model_usage</c> up into <c>daily_agent_stats</c>,
/// one count per org, user, client and kind. Kinds: the audit action (<c>page.update</c>), <c>mcp:&lt;tool&gt;</c> and
/// <c>mcp-error:&lt;tool&gt;</c> per call, <c>tokens:&lt;role&gt;</c> (input plus output tokens, no user, client is the model ID).
/// Client is the OAuth client's name, else the claimed label, else "Other". Re-running a day overwrites its counts in place.
/// </summary>
public static class UsageRollup
{
    public const string OtherClient = "Other";

    /// <summary>Every day from the last rolled-up day (re-rolled, it may have been partial) to yesterday, within raw retention.</summary>
    public static async Task<int> RunAsync(AtharDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
        var floor = yesterday.AddDays(-UsageRetention.RawDays);
        var last = await db.DailyAgentStats.IgnoreQueryFilters().MaxAsync(s => (DateOnly?)s.Date, ct);
        var day = last is { } l && l > floor ? l : floor;
        var rows = 0;
        for (; day <= yesterday; day = day.AddDays(1)) rows += await RollUpDayAsync(db, day, ct);
        return rows;
    }

    /// <summary>Writes the rollup for one day in every org and returns the number of stat rows written.</summary>
    public static async Task<int> RollUpDayAsync(AtharDbContext db, DateOnly day, CancellationToken ct = default)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);

        var audit = await db.AuditEvents.IgnoreQueryFilters().Where(e => e.CreatedAt >= from && e.CreatedAt < to)
            .GroupBy(e => new { e.OrgId, e.ActorUserId, e.OauthClientId, e.ClaimedClient, Kind = e.Action })
            .Select(g => new Raw(g.Key.OrgId, g.Key.ActorUserId, g.Key.OauthClientId, g.Key.ClaimedClient, g.Key.Kind, g.Count()))
            .ToListAsync(ct);
        var mcp = await db.McpCalls.IgnoreQueryFilters().Where(c => c.CreatedAt >= from && c.CreatedAt < to)
            .GroupBy(c => new { c.OrgId, c.ActorUserId, c.OauthClientId, c.ClaimedClient, c.Tool, Ok = c.Outcome == "ok" })
            .Select(g => new Raw(g.Key.OrgId, g.Key.ActorUserId, g.Key.OauthClientId, g.Key.ClaimedClient, g.Key.Tool, g.Count(), g.Key.Ok))
            .ToListAsync(ct);
        var tokens = await db.ModelUsage.IgnoreQueryFilters().Where(u => u.CreatedAt >= from && u.CreatedAt < to)
            .GroupBy(u => new { u.OrgId, u.ModelId, u.Role })
            .Select(g => new { g.Key.OrgId, g.Key.ModelId, g.Key.Role, Count = g.Sum(u => (long)u.InputTokens + u.OutputTokens) })
            .ToListAsync(ct);

        var clientIds = audit.Concat(mcp).Select(r => r.OauthClientId).OfType<Guid>().Distinct().ToList();
        var names = await db.OauthApplications.Where(a => clientIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.DisplayName ?? a.ClientId, ct);
        string Client(Raw r) => (r.OauthClientId is { } id ? names.GetValueOrDefault(id) : null) ?? r.ClaimedClient ?? OtherClient;

        var counts = new Dictionary<(Guid Org, Guid? User, string Client, string Kind), long>();
        void Add(Guid org, Guid? user, string client, string kind, long n)
        {
            var key = (org, user, Trim(client, 200), Trim(kind, 64));
            counts[key] = counts.GetValueOrDefault(key) + n;
        }
        foreach (var r in audit) Add(r.OrgId, r.UserId, Client(r), r.Kind, r.Count);
        foreach (var r in mcp)
        {
            Add(r.OrgId, r.UserId, Client(r), "mcp:" + r.Kind, r.Count);
            if (r.Ok == false) Add(r.OrgId, r.UserId, Client(r), "mcp-error:" + r.Kind, r.Count);
        }
        foreach (var t in tokens) Add(t.OrgId, null, t.ModelId, "tokens:" + t.Role.ToString().ToLowerInvariant(), t.Count);

        // Upsert, so a re-run updates the day in place and never deletes a rollup (only UsageRetention does).
        var existing = (await db.DailyAgentStats.IgnoreQueryFilters().Where(s => s.Date == day).ToListAsync(ct))
            .ToDictionary(s => (s.OrgId, s.UserId, s.Client, s.Kind));
        foreach (var ((org, user, client, kind), n) in counts)
        {
            if (existing.TryGetValue((org, user, client, kind), out var stat)) stat.Count = n;
            else db.DailyAgentStats.Add(new DailyAgentStat { OrgId = org, Date = day, UserId = user, Client = client, Kind = kind, Count = n });
        }
        await db.SaveChangesAsync(ct);
        return counts.Count;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record Raw(Guid OrgId, Guid? UserId, Guid? OauthClientId, string? ClaimedClient, string Kind, int Count, bool? Ok = null);
}

/// <summary>FR-AG-12: raw usage rows go after 90 days and rollups after 400, in every org. The only code that deletes them.</summary>
public static class UsageRetention
{
    public const int RawDays = 90, RollupDays = 400;

    public static async Task<int> PurgeAsync(AtharDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var raw = now.AddDays(-RawDays);
        var rollup = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-RollupDays);
        return await db.McpCalls.IgnoreQueryFilters().Where(c => c.CreatedAt < raw).ExecuteDeleteAsync(ct)
            + await db.ModelUsage.IgnoreQueryFilters().Where(u => u.CreatedAt < raw).ExecuteDeleteAsync(ct)
            + await db.DailyAgentStats.IgnoreQueryFilters().Where(s => s.Date < rollup).ExecuteDeleteAsync(ct);
    }
}
