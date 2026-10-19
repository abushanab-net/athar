using Athar.Data;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athar.Worker.Jobs;

/// <summary>One job kind. Its effects go into <paramref name="db"/> inside the completion transaction (docs/03 §7).</summary>
public interface IJob
{
    string Kind { get; }
    Task RunAsync(Job job, AtharDbContext db, CancellationToken ct);
}

/// <summary>Thrown when a job can't run yet (a model still downloading): it's rescheduled without using up an attempt.</summary>
public sealed class JobDeferred(string reason, TimeSpan delay) : Exception(reason)
{
    public TimeSpan Delay { get; } = delay;
}

/// <summary>A claimed job: only the holder of this lease may renew or complete it.</summary>
public readonly record struct JobClaim(Guid Id, Guid LeaseId);

/// <summary>
/// The Postgres job queue (docs/03 §7, D11).
/// <list type="number">
/// <item>Claim: one due job (ready, or running with an expired lease) <c>FOR UPDATE SKIP LOCKED</c>, then a new
/// <c>lease_id</c>, <c>lease_until</c> and <c>running</c> on the tracked entity, committed.</item>
/// <item>Run: the job's work runs in a transaction, so even its immediate SQL (<c>ExecuteDelete</c>) is undone if the lease
/// is lost, but the job row isn't locked meanwhile: a renewal loop extends the lease every third of it, and a lost lease
/// cancels the work.</item>
/// <item>Complete: the same transaction locks the job <c>FOR UPDATE</c> and commits only if the lease is still ours and
/// unexpired. The job's effects and its <c>done</c> status commit together, so a superseded worker persists nothing.
/// <c>xmin</c> is the concurrency token on every save.</item>
/// <item>Failure: back off exponentially; after <see cref="MaxAttempts"/> the job is <c>dead</c>.</item>
/// </list>
/// Jobs run across orgs (system work); each one runs with its own org as the context's tenant. <see cref="Concurrency"/>
/// loops claim in parallel; SKIP LOCKED gives them different jobs.
/// </summary>
public sealed class JobRunner(IServiceScopeFactory scopes, IEnumerable<IJob> jobs, TimeProvider clock, ILogger<JobRunner> log) : BackgroundService
{
    public const int MaxAttempts = 6;
    /// <summary>docs/03 §2: one inference queue with bounded concurrency 2.</summary>
    public const int Concurrency = 2;
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    /// <summary>How long a claim or renewal holds a job. Tests shorten it.</summary>
    public TimeSpan Lease { get; init; } = DefaultLease;

    private readonly Dictionary<string, IJob> _byKind = jobs.ToDictionary(j => j.Kind);
    private volatile bool _reloadTypes;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await WaitForSchemaAsync(stop);
        await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => LoopAsync(stop)));
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (_reloadTypes) { await WaitForSchemaAsync(stop); _reloadTypes = false; }
                if (!await RunOneAsync(stop)) await Task.Delay(TimeSpan.FromSeconds(1), clock, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e)
            {
                // The database isn't migrated yet, or is restarting: try again shortly.
                log.LogWarning(e, "Job loop error");
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stop);
            }
        }
    }

    /// <summary>
    /// Web migrates the database; the worker waits for it. Npgsql caches type info per data source, and pgvector's
    /// <c>vector</c> is created by a migration, so the cache is reloaded once the schema is there.
    /// </summary>
    private async Task WaitForSchemaAsync(CancellationToken stop)
    {
        while (true)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
                if (!(await db.Database.GetPendingMigrationsAsync(stop)).Any())
                {
                    await db.Database.OpenConnectionAsync(stop);
                    await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync(stop);
                    NpgsqlConnection.ClearAllPools();
                    return;
                }
            }
            catch (Exception e) when (!stop.IsCancellationRequested) { log.LogInformation("Waiting for the database: {Reason}", e.Message); }
            await Task.Delay(TimeSpan.FromSeconds(2), clock, stop);
        }
    }

    /// <summary>Claims and runs one due job. False when there was none.</summary>
    public async Task<bool> RunOneAsync(CancellationToken ct)
    {
        if (await ClaimAsync(ct) is not { } claim) return false;
        await RunClaimedAsync(claim, ct);
        return true;
    }

    /// <summary>Runs a claimed job under its lease and completes it. False when the lease was lost and nothing persisted.</summary>
    public async Task<bool> RunClaimedAsync(JobClaim claim, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopRenewing = new CancellationTokenSource();
        var renewing = RenewWhileRunningAsync(claim, lost, stopRenewing.Token);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // A detached copy for the job: the completion lock below must read the row fresh (renewals moved its xmin).
            var job = await db.Jobs.AsNoTracking().IgnoreQueryFilters().SingleAsync(j => j.Id == claim.Id, ct);
            if (job.LeaseId != claim.LeaseId) return false;
            db.CurrentOrgId = job.OrgId;
            await _byKind[job.Kind].RunAsync(job, db, lost.Token);

            await stopRenewing.CancelAsync();
            await renewing;
            var locked = await Locked(db, claim, ct);
            if (locked is null) return false; // superseded: the transaction rolls back the job's effects
            locked.Status = JobStatus.Done;
            locked.LeaseId = null;
            locked.LeaseUntil = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The database was reset (`db reset` recreates pgvector's type with a new OID): reload the type cache.
            _reloadTypes |= StaleTypes(e);
            await FailAsync(claim, e, ct); // a no-op when the lease is gone
            return false;
        }
        finally
        {
            await stopRenewing.CancelAsync();
            await renewing;
        }
    }

    /// <summary>Extends the lease every third of it while the job runs; cancels the work once the lease is lost.</summary>
    private async Task RenewWhileRunningAsync(JobClaim claim, CancellationTokenSource lost, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Lease / 3, clock, stop);
                if (!await RenewAsync(claim, stop))
                {
                    log.LogWarning("Job {Id} lost its lease; its work is discarded", claim.Id);
                    await lost.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { } // the job finished
        catch (Exception e) { log.LogWarning(e, "Job {Id} lease renewal failed; retrying on the next tick", claim.Id); }
    }

    /// <summary>Extends the lease if it's still ours and unexpired (docs/03 §7). False once another worker holds the job.</summary>
    public async Task<bool> RenewAsync(JobClaim claim, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await Locked(db, claim, ct);
        if (job is null) return false;
        db.CurrentOrgId = job.OrgId;
        job.LeaseUntil = clock.GetUtcNow() + Lease;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Claims one due job of a kind this runner knows: ready, or running with an expired lease.</summary>
    public async Task<JobClaim?> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
        var kinds = _byKind.Keys.ToArray();
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await db.Jobs.FromSql($"""
                SELECT *, xmin FROM jobs
                WHERE kind = ANY({kinds})
                  AND ((status = 'ready' AND run_after <= {now}) OR (status = 'running' AND lease_until < {now}))
                ORDER BY run_after LIMIT 1 FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters().ToListAsync(ct) is [var claimed] ? claimed : null; // uncomposed: the SQL runs as written
        if (job is null) return null;
        var leaseId = Guid.NewGuid();
        job.LeaseId = leaseId;
        job.LeaseUntil = now + Lease;
        job.Status = JobStatus.Running;
        job.Attempts++;
        db.CurrentOrgId = job.OrgId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new JobClaim(job.Id, leaseId);
    }

    private static bool StaleTypes(Exception e) => e.GetBaseException() switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UndefinedObject or PostgresErrorCodes.InternalError } => true,
        var b => b.Message.Contains("datatype", StringComparison.OrdinalIgnoreCase) || b.Message.Contains("data type", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>The job, locked, if our lease still holds; otherwise null.</summary>
    private async Task<Job?> Locked(AtharDbContext db, JobClaim claim, CancellationToken ct)
    {
        var job = await db.Jobs.FromSql($"SELECT *, xmin FROM jobs WHERE id = {claim.Id} FOR UPDATE").IgnoreQueryFilters().ToListAsync(ct) is [var j] ? j : null;
        return job is { Status: JobStatus.Running } && job.LeaseId == claim.LeaseId && job.LeaseUntil > clock.GetUtcNow() ? job : null;
    }

    private async Task FailAsync(JobClaim claim, Exception error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await Locked(db, claim, ct);
        if (job is null) return;
        db.CurrentOrgId = job.OrgId;
        if (error is JobDeferred deferred)
        {
            job.Attempts--;
            job.Status = JobStatus.Ready;
            job.RunAfter = clock.GetUtcNow() + deferred.Delay;
        }
        else
        {
            job.Status = job.Attempts >= MaxAttempts ? JobStatus.Dead : JobStatus.Ready;
            job.RunAfter = clock.GetUtcNow() + TimeSpan.FromSeconds(5 * Math.Pow(2, job.Attempts - 1));
            log.LogWarning(error, "Job {Kind} {Key} failed (attempt {Attempt})", job.Kind, job.Key, job.Attempts);
        }
        var message = error.GetBaseException().Message; // the database's reason, not EF's wrapper
        job.LastError = message.Length > 2000 ? message[..2000] : message;
        job.LeaseId = null;
        job.LeaseUntil = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
