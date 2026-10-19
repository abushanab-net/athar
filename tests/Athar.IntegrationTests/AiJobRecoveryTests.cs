using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using Athar.Web.Services;
using Athar.Worker.Jobs;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;
using Athar.Worker.Jobs.Upload;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

[Collection(AtharCollection.Name)]
public sealed class AiJobRecoveryTests(AtharApp app)
{
    [Theory]
    [InlineData("embedding")]
    [InlineData("extraction")]
    [InlineData("vision")]
    public async Task Repeated_outages_and_bad_keys_defer_real_jobs_then_recover_without_rebuilding(string role)
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var original = await data.Resolver.ResolveAsync(QaSeed.Org, ct);
        var store = new AttachmentStore(data.Configuration);
        string? storageKey = null;
        Guid? attachmentId = null;
        Job queued;
        IJob handler;
        await using (var db = data.CreateDbContext())
        {
            // Only this runner owns jobs in this isolated database.
            await db.Jobs.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
            if (role == "embedding")
            {
                var revision = await db.Chunks.Where(c => c.OwnerId == QaSeed.MemoryProject).Select(c => c.OwnerRevision).FirstAsync(ct);
                queued = IndexJobs.Vectors(ChunkOwner.Memory, QaSeed.MemoryProject, revision, DateTimeOffset.UtcNow, original.EmbeddingIdentity);
                handler = new EmbedVectorsJob(data.Clients);
                db.Jobs.Add(queued);
            }
            else if (role == "extraction")
            {
                (await db.AgentSessions.SingleAsync(s => s.Id == QaSeed.Session, ct)).ExtractedUpto = -1;
                queued = IndexJobs.Extract(QaSeed.Session, 2, DateTimeOffset.UtcNow);
                handler = new ExtractSessionJob(data.Clients);
                db.Jobs.Add(queued);
            }
            else
            {
                var upload = await new AttachmentService(db, store, TimeProvider.System, data.Resolver).UploadAsync(
                    AiTestDatabase.Admin, QaSeed.DeployRunbookPage, "recovery.png", "image/png", new MemoryStream(AiVisionTests.Png), ct);
                attachmentId = upload.AttachmentId;
                storageKey = (await db.Attachments.SingleAsync(a => a.Id == attachmentId, ct)).StorageKey;
                queued = await db.Jobs.SingleAsync(j => j.Kind == AttachmentRules.ExtractKind && j.Key == attachmentId.ToString(), ct);
                handler = new ExtractAttachmentJob(store, TimeProvider.System, NullLogger<ExtractAttachmentJob>.Instance, data.Resolver);
            }
            queued.OrgId = QaSeed.Org;
            await db.SaveChangesAsync(ct);
        }
        await using var provider = new ServiceCollection().AddScoped(_ => data.CreateDbContext()).BuildServiceProvider();
        using var runner = new JobRunner(provider.GetRequiredService<IServiceScopeFactory>(), [handler], TimeProvider.System, NullLogger<JobRunner>.Instance);
        try
        {
            foreach (var status in Enumerable.Repeat(503, JobRunner.MaxAttempts + 1).Concat([401, 403]))
            {
                gateway.StatusCode = status;
                Assert.True(await runner.RunOneAsync(ct));
                await AssertDeferredAndMakeDueAsync(status == 503 ? "provider_unavailable" : "authentication_unavailable");
            }
            gateway.StatusCode = 200;
            await using (var broken = data.CreateDbContext())
            {
                (await broken.AiSettings.SingleAsync(ct)).ProtectedApiKey = "unreadable-ciphertext";
                await broken.SaveChangesAsync(ct);
            }
            var calls = gateway.Requests.Count;
            Assert.True(await runner.RunOneAsync(ct));
            await AssertDeferredAndMakeDueAsync("key_unreadable");
            Assert.Equal(calls, gateway.Requests.Count);
            await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway, revision: 1), ct);
            Assert.Equal(original.EmbeddingIdentity, (await data.Resolver.ResolveAsync(QaSeed.Org, ct)).EmbeddingIdentity);
            var claim = Assert.NotNull(await runner.ClaimAsync(ct));
            Assert.Equal(queued.Id, claim.Id);
            Assert.True(await runner.RunClaimedAsync(claim, ct));
            await using var final = data.CreateDbContext();
            var done = await final.Jobs.SingleAsync(j => j.Id == queued.Id, ct);
            Assert.Equal(JobStatus.Done, done.Status);
            Assert.Equal(1, done.Attempts);
            if (role == "embedding")
                Assert.True(await final.ChunkEmbeddings.AnyAsync(e => e.ModelId == original.EmbeddingIdentity, ct));
            else if (role == "extraction")
                Assert.Equal(2, (await final.AgentSessions.SingleAsync(s => s.Id == QaSeed.Session, ct)).ExtractedUpto);
            else
            {
                var attachment = await final.Attachments.SingleAsync(a => a.Id == attachmentId, ct);
                Assert.Equal(ExtractStatus.Done, attachment.ExtractStatus);
                Assert.Equal(gateway.VisionText, attachment.TextContent);
                Assert.Equal(1, await final.AuditEvents.CountAsync(a => a.Action == "attachment.vision" && a.ResourceId == attachmentId.ToString(), ct));
            }
        }
        finally { if (storageKey is not null) File.Delete(store.PathOf(storageKey)); }

        async Task AssertDeferredAndMakeDueAsync(string reason)
        {
            await using var check = data.CreateDbContext();
            var pending = await check.Jobs.SingleAsync(j => j.Id == queued.Id, ct);
            Assert.Equal(JobStatus.Ready, pending.Status);
            Assert.Equal(0, pending.Attempts);
            Assert.Equal(reason, pending.LastError);
            Assert.True(pending.RunAfter > DateTimeOffset.UtcNow);
            if (attachmentId is { } id)
            {
                Assert.Equal(ExtractStatus.Pending, (await check.Attachments.SingleAsync(a => a.Id == id, ct)).ExtractStatus);
                Assert.False(await check.AuditEvents.AnyAsync(a => a.Action == "attachment.vision" && a.ResourceId == id.ToString(), ct));
            }
            pending.RunAfter = DateTimeOffset.UtcNow.AddSeconds(-1);
            await check.SaveChangesAsync(ct);
        }
    }
    [Theory]
    [InlineData("embedding", false)]
    [InlineData("embedding", true)]
    [InlineData("extraction", false)]
    [InlineData("extraction", true)]
    public async Task Permanent_rejections_and_malformed_responses_still_exhaust_bounded_attempts(string role, bool malformed)
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        gateway.StatusCode = malformed ? 200 : 400;
        if (malformed) { gateway.EmbeddingFault = "short"; gateway.MalformedChat = true; }
        Job queued;
        IJob handler;
        await using (var db = data.CreateDbContext())
        {
            await db.Jobs.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
            if (role == "embedding")
            {
                var revision = await db.Chunks.Where(c => c.OwnerId == QaSeed.MemoryProject).Select(c => c.OwnerRevision).FirstAsync(ct);
                queued = IndexJobs.Vectors(ChunkOwner.Memory, QaSeed.MemoryProject, revision, DateTimeOffset.UtcNow);
                handler = new EmbedVectorsJob(data.Clients);
            }
            else
            {
                (await db.AgentSessions.SingleAsync(s => s.Id == QaSeed.Session, ct)).ExtractedUpto = -1;
                queued = IndexJobs.Extract(QaSeed.Session, 2, DateTimeOffset.UtcNow);
                handler = new ExtractSessionJob(data.Clients);
            }
            queued.OrgId = QaSeed.Org;
            db.Jobs.Add(queued);
            await db.SaveChangesAsync(ct);
        }
        await using var provider = new ServiceCollection().AddScoped(_ => data.CreateDbContext()).BuildServiceProvider();
        using var runner = new JobRunner(provider.GetRequiredService<IServiceScopeFactory>(), [handler], TimeProvider.System, NullLogger<JobRunner>.Instance);
        for (var attempt = 1; attempt <= JobRunner.MaxAttempts; attempt++)
        {
            Assert.True(await runner.RunOneAsync(ct));
            await using var check = data.CreateDbContext();
            var failed = await check.Jobs.SingleAsync(j => j.Id == queued.Id, ct);
            Assert.Equal(attempt, failed.Attempts);
            Assert.Equal(attempt == JobRunner.MaxAttempts ? JobStatus.Dead : JobStatus.Ready, failed.Status);
            Assert.Equal(malformed ? "invalid_response" : "provider_rejected", failed.LastError);
            failed.RunAfter = DateTimeOffset.UtcNow.AddSeconds(-1);
            await check.SaveChangesAsync(ct);
        }
        Assert.False(await runner.RunOneAsync(ct));
    }

}
