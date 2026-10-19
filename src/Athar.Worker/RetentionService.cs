using Athar.Data;

namespace Athar.Worker;

/// <summary>
/// Once a day: FR-AU-04 deletes audit events older than Athar:Audit:RetentionDays (default 90), and FR-PG-01 purges pages
/// trashed more than 30 days ago in every org. FR-AG-12 rolls usage up into daily_agent_stats, then purges raw usage after 90
/// days and rollups after 400. Each runs on its own, so one failing doesn't stop the others.
/// ponytail: "nightly" means every 24 h from worker start; the rollup catches up on missed days, so the hour doesn't matter.
/// </summary>
public sealed class RetentionService(IServiceScopeFactory scopes, IConfiguration config, ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var days = config.GetValue("Athar:Audit:RetentionDays", AuditRetention.DefaultDays);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            // The usage rollup runs first, so it reads the raw rows before they're deleted.
            await RunAsync("Usage rollup", db => Jobs.Retention.UsageRollup.RunAsync(db, DateTimeOffset.UtcNow, stop), stop);
            await RunAsync("Usage retention", db => Jobs.Retention.UsageRetention.PurgeAsync(db, DateTimeOffset.UtcNow, stop), stop);
            await RunAsync("Audit retention", db => AuditRetention.PurgeAsync(db, DateTimeOffset.UtcNow, days, stop), stop);
            await RunAsync("Trash purge", db => TrashRetention.PurgeAllAsync(db, DateTimeOffset.UtcNow, stop), stop);
            await RunAsync("Transcript purge", db => SessionRetention.PurgeAllAsync(db, DateTimeOffset.UtcNow, stop), stop);
        }
        while (await timer.WaitForNextTickAsync(stop));
    }

    private async Task RunAsync(string name, Func<AtharDbContext, Task<int>> purge, CancellationToken stop)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var deleted = await purge(scope.ServiceProvider.GetRequiredService<AtharDbContext>());
            log.LogInformation("{Name}: deleted {Count} rows", name, deleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ponytail: retried on the next daily tick. Moves onto the job queue (T049) if it needs retries or leases.
            log.LogError(ex, "{Name} failed", name);
        }
    }
}
