using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

/// <summary>
/// AT-57 and T049 on the real job runner, against the fixture's database. Each test uses its own job kind, which the
/// AppHost's worker doesn't know, so only the runners built here claim those jobs.
/// </summary>
[Collection(AtharCollection.Name)]
public class JobLeaseTests(AtharApp athar)
{
    [Fact]
    public async Task AT_57_a_superseded_worker_can_neither_renew_nor_persist()
    {
        var ct = AtharApp.Timeout();
        var job = new GatedJob();
        var (a, b) = (Runner(job), Runner(job));
        var id = await AddJobAsync(job.Kind, ct);

        var claimA = Assert.NotNull(await a.ClaimAsync(ct));
        var runA = a.RunClaimedAsync(claimA, ct);
        await job.Started.Task.WaitAsync(ct); // A is mid-run, its effects staged

        await using (var db = athar.Db(QaSeed.Org)) // A's lease runs out
            await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)), ct);
        var claimB = Assert.NotNull(await b.ClaimAsync(ct));
        Assert.Equal(id, claimB.Id);
        Assert.NotEqual(claimA.LeaseId, claimB.LeaseId);

        Assert.False(await a.RenewAsync(claimA, ct));
        job.Release.SetResult();
        Assert.False(await runA); // A's completion is refused...
        Assert.Equal((1, 0), await EffectsAsync(id, ct)); // ...and its delete and its insert were rolled back

        Assert.True(await b.RunClaimedAsync(claimB, ct));
        Assert.Equal((0, 1), await EffectsAsync(id, ct));
        await using var check = athar.Db(QaSeed.Org);
        Assert.Equal(JobStatus.Done, (await check.Jobs.SingleAsync(j => j.Id == id, ct)).Status);
    }

    [Fact]
    public async Task AT_57_workers_claiming_at_once_get_different_jobs()
    {
        var ct = AtharApp.Timeout();
        var job = new GatedJob();
        var runners = Enumerable.Range(0, 4).Select(_ => Runner(job)).ToList();
        var ids = new HashSet<Guid>();
        for (var i = 0; i < runners.Count; i++) ids.Add(await AddJobAsync(job.Kind, ct));

        var claims = await Task.WhenAll(runners.Select(r => r.ClaimAsync(ct)));
        Assert.All(claims, c => Assert.NotNull(c));
        Assert.Equal(ids.Order(), claims.Select(c => c!.Value.Id).Order());
    }

    [Fact]
    public async Task T049_renewal_keeps_a_job_that_outlives_its_lease()
    {
        var ct = AtharApp.Timeout();
        var job = new GatedJob();
        var lease = TimeSpan.FromSeconds(2);
        var (a, b) = (Runner(job, lease), Runner(job, lease));
        var id = await AddJobAsync(job.Kind, ct);

        var claim = Assert.NotNull(await a.ClaimAsync(ct));
        var run = a.RunClaimedAsync(claim, ct);
        await job.Started.Task.WaitAsync(ct);
        for (var waited = TimeSpan.Zero; waited < lease * 2; waited += TimeSpan.FromMilliseconds(500))
        {
            Assert.Null(await b.ClaimAsync(ct)); // renewed every lease/3, so never claimable
            await Task.Delay(500, ct);
        }
        job.Release.SetResult();
        Assert.True(await run);
        Assert.Equal((0, 1), await EffectsAsync(id, ct));
    }

    private JobRunner Runner(IJob job, TimeSpan? lease = null) =>
        new(Scopes(), [job], TimeProvider.System, NullLogger<JobRunner>.Instance) { Lease = lease ?? JobRunner.DefaultLease };

    private IServiceScopeFactory Scopes() => new ServiceCollection()
        .AddDbContext<AtharDbContext>(o => o.UseAthar(athar.ConnectionString(), new SeedOptions("qa")))
        .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    /// <summary>A job plus a marker its run deletes with immediate SQL, like EmbedPageJob's chunk delete. The marker is an inert job row (done, an unknown kind).</summary>
    private async Task<Guid> AddJobAsync(string kind, CancellationToken ct)
    {
        await using var db = athar.Db(QaSeed.Org);
        var job = new Job { Kind = kind, Key = Guid.NewGuid().ToString(), Status = JobStatus.Ready, RunAfter = DateTimeOffset.UtcNow.AddSeconds(-1) };
        db.Jobs.Add(job);
        db.Jobs.Add(new Job { Kind = kind + ".marker", Key = job.Id.ToString(), Status = JobStatus.Done, RunAfter = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return job.Id;
    }

    /// <summary>(markers left, effect rows written) for one job.</summary>
    private async Task<(int Markers, int Effects)> EffectsAsync(Guid id, CancellationToken ct)
    {
        await using var db = athar.Db(QaSeed.Org);
        return (await db.Jobs.CountAsync(j => j.Kind.EndsWith(".marker") && j.Key == id.ToString(), ct),
                await db.AuditEvents.CountAsync(e => e.Action == "test.job_effect" && e.ResourceId == id.ToString(), ct));
    }

    /// <summary>Deletes its marker at once, stages an audit row, then waits for <see cref="Release"/> unless it's already released.</summary>
    private sealed class GatedJob : IJob
    {
        public string Kind { get; } = $"test.lease.{Guid.NewGuid():N}";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(Job job, AtharDbContext db, CancellationToken ct)
        {
            await db.Jobs.Where(j => j.Kind == Kind + ".marker" && j.Key == job.Id.ToString()).ExecuteDeleteAsync(ct);
            db.Audit(job.OrgId, null, "test.job_effect", "job", job.Id);
            Started.TrySetResult();
            if (!Release.Task.IsCompleted) await Release.Task.WaitAsync(ct);
        }
    }
}
