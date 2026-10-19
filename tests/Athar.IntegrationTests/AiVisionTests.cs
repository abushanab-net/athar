using System.Text.Json;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using Athar.Web.Services;
using Athar.Worker.Jobs;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Upload;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

[Collection(AtharCollection.Name)]
public sealed class AiVisionTests(AtharApp app)
{
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAHAAAAAsCAIAAAAyzquQAAAAtklEQVR4nO3QMRaEMAzEUN//0lDgkiLBMoE8qWad2R+HocXqAbslKJygcILCCQonKJygcEOgMVDlt7M3K3XvERTeIyi8R1B4zxDE7NCO71clKJygcILCCQpU2SboTYLCCQrXDjpb9+iOqD2CZoLCCQr3KmjHoC+AdmwQVFAuQeEEhRMUTlA4QeF+CRqFqn/u6ebSTeoxQfMm9ZigeZN6TNC8SR2yK0HhBIUTFE5QOEHhBIUTFO4EnqZRYD1jbn8AAAAASUVORK5CYII=");

    [Fact]
    public async Task New_supported_images_transcribe_inline_and_become_permission_filtered_page_chunks()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var store = new AttachmentStore(data.Configuration);
        await using var db = data.CreateDbContext();
        var uploads = new AttachmentService(db, store, TimeProvider.System, data.Resolver);
        var uploaded = await uploads.UploadAsync(AiTestDatabase.Admin, QaSeed.DeployRunbookPage, "approval.png", "image/png", new MemoryStream(Png), ct);
        Assert.Equal("pending", uploaded.ExtractStatus);
        var job = await db.Jobs.SingleAsync(j => j.Kind == AttachmentRules.ExtractKind && j.Key == uploaded.AttachmentId.ToString(), ct);
        await new ExtractAttachmentJob(store, TimeProvider.System, NullLogger<ExtractAttachmentJob>.Instance, data.Resolver).RunAsync(job, db, ct);
        await db.SaveChangesAsync(ct);
        Assert.Equal(ExtractStatus.Done, (await db.Attachments.SingleAsync(a => a.Id == uploaded.AttachmentId, ct)).ExtractStatus);
        var request = gateway.Requests.Single(r => r.Path == "chat/completions");
        Assert.StartsWith("data:image/png;base64,", request.Body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        var indexJob = await db.Jobs.SingleAsync(j => j.Kind == IndexJobs.EmbedPage && j.Key.EndsWith($":attachment:{uploaded.AttachmentId}"), ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Athar:Index:PageEvery"] = "00:00:00" }).Build();
        await new EmbedPageJob(TimeProvider.System, config).RunAsync(indexJob, db, ct);
        await db.SaveChangesAsync(ct);
        var chunk = await db.Chunks.SingleAsync(c => c.Body.Contains("zephyr approval"), ct);
        Assert.Equal((ChunkOwner.Page, QaSeed.DeployRunbookPage, QaSeed.EngWiki), (chunk.OwnerKind, chunk.OwnerId, chunk.SpaceId));
        var search = new SearchService(db, data.Embedder);
        Assert.Contains((await search.SearchAsync(AiTestDatabase.Person(QaSeed.Member), new("zephyr", Kinds: ["page"]), ct)).Hits, h => h.Id == QaSeed.DeployRunbookPage);
        Assert.DoesNotContain((await search.SearchAsync(AiTestDatabase.Person(QaSeed.OtherOrgOwner, QaSeed.OtherOrg), new("zephyr"), ct)).Hits, h => h.Id == QaSeed.DeployRunbookPage);
        db.CurrentOrgId = QaSeed.Org;
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "attachment.upload" && a.ResourceId == uploaded.AttachmentId.ToString(), ct));
        var visionAudit = await db.AuditEvents.SingleAsync(a => a.Action == "attachment.vision" && a.ResourceId == uploaded.AttachmentId.ToString(), ct);
        Assert.Contains("Done", visionAudit.After!);
        Assert.DoesNotContain(gateway.VisionText, visionAudit.After!);
        File.Delete(store.PathOf((await db.Attachments.SingleAsync(a => a.Id == uploaded.AttachmentId, ct)).StorageKey));
    }

    [Fact]
    public async Task Disabled_unsupported_deleted_and_failed_image_paths_do_not_claim_success()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        var store = new AttachmentStore(data.Configuration);
        await using var db = data.CreateDbContext();
        var uploads = new AttachmentService(db, store, TimeProvider.System, data.Resolver);
        var created = new List<Guid>();
        async Task<AttachmentView> Upload(string mime = "image/png", byte[]? bytes = null)
        {
            var result = await uploads.UploadAsync(AiTestDatabase.Admin, QaSeed.DeployRunbookPage, "probe", mime, new MemoryStream(bytes ?? Png), ct);
            created.Add(result.AttachmentId);
            return result;
        }
        Assert.Equal("skipped", (await Upload()).ExtractStatus);
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var policyOff = await uploads.UploadAsync(AiTestDatabase.Admin, QaSeed.HandbookPage, "policy-off.png", "image/png", new MemoryStream(Png), ct);
        created.Add(policyOff.AttachmentId);
        Assert.Equal("skipped", policyOff.ExtractStatus);
        Assert.Equal("skipped", (await Upload("image/gif", "GIF89a"u8.ToArray())).ExtractStatus);
        var view = await Upload();
        var job = await db.Jobs.SingleAsync(j => j.Kind == AttachmentRules.ExtractKind && j.Key == view.AttachmentId.ToString(), ct);
        var runner = new ExtractAttachmentJob(store, TimeProvider.System, NullLogger<ExtractAttachmentJob>.Instance, data.Resolver);
        gateway.StatusCode = 503;
        job.Attempts = 1;
        Assert.Equal("provider_unavailable", (await Assert.ThrowsAsync<JobDeferred>(() => runner.RunAsync(job, db, ct))).Message);
        Assert.Equal(ExtractStatus.Pending, (await db.Attachments.SingleAsync(a => a.Id == view.AttachmentId, ct)).ExtractStatus);
        gateway.StatusCode = 200;
        gateway.MalformedChat = true;
        job.Attempts = JobRunner.MaxAttempts;
        await runner.RunAsync(job, db, ct);
        await db.SaveChangesAsync(ct);
        Assert.Equal(ExtractStatus.Failed, (await db.Attachments.SingleAsync(a => a.Id == view.AttachmentId, ct)).ExtractStatus);
        var failedAudit = await db.AuditEvents.SingleAsync(a => a.Action == "attachment.vision" && a.ResourceId == view.AttachmentId.ToString(), ct);
        Assert.Contains("Failed", failedAudit.After!);
        gateway.StatusCode = 200;
        gateway.MalformedChat = false;
        var later = await Upload();
        var laterJob = await db.Jobs.SingleAsync(j => j.Kind == AttachmentRules.ExtractKind && j.Key == later.AttachmentId.ToString(), ct);
        gateway.BeforeResponse = async _ =>
        {
            await using var changed = data.CreateDbContext();
            (await changed.Pages.SingleAsync(p => p.Id == QaSeed.DeployRunbookPage, ct)).DeletedAt = DateTimeOffset.UtcNow;
            await changed.SaveChangesAsync(ct);
        };
        await runner.RunAsync(laterJob, db, ct);
        await db.SaveChangesAsync(ct);
        var skipped = await db.Attachments.SingleAsync(a => a.Id == later.AttachmentId, ct);
        Assert.Equal(ExtractStatus.Skipped, skipped.ExtractStatus);
        Assert.Null(skipped.TextContent);
        var skippedAudit = await db.AuditEvents.SingleAsync(a => a.Action == "attachment.vision" && a.ResourceId == later.AttachmentId.ToString(), ct);
        Assert.Contains("Skipped", skippedAudit.After!);
        foreach (var key in await db.Attachments.Where(a => created.Contains(a.Id)).Select(a => a.StorageKey).ToArrayAsync(ct))
            File.Delete(store.PathOf(key));
    }

    [Fact]
    public async Task Copying_a_pending_attachment_queues_the_copys_own_extraction_job()
    {
        var ct = AtharApp.Timeout();
        var store = new AttachmentStore(new ConfigurationBuilder().Build());
        await using var db = app.Db(QaSeed.Org);
        var ops = await app.OpsAsync(db: db);
        var actor = AiTestDatabase.Admin;
        var page = (await ops.CreatePageAsync(actor, QaSeed.EngWiki, null, "Pending copy source", "Copy attachment state", Guid.NewGuid().ToString(), ct)).PageId;
        var upload = await new AttachmentService(db, store, TimeProvider.System).UploadAsync(actor, page, "pending.png", "image/png", new MemoryStream(Png), ct);
        // Simulate an extraction in progress without racing the shared worker on the source job.
        (await db.Attachments.SingleAsync(a => a.Id == upload.AttachmentId, ct)).ExtractStatus = ExtractStatus.Pending;
        await db.SaveChangesAsync(ct);
        var result = await new PageCopyService(db, ops, new PageService(db, TimeProvider.System), store).DuplicateAsync(actor, page, Guid.NewGuid().ToString(), ct: ct);
        var copy = await db.Attachments.SingleAsync(a => a.PageId == result.PageId, ct);
        Assert.NotEqual(upload.AttachmentId, copy.Id);
        var job = await db.Jobs.SingleAsync(j => j.Kind == AttachmentRules.ExtractKind && j.Key == copy.Id.ToString(), ct);
        Assert.Equal(copy.Id, JsonSerializer.Deserialize<AttachmentRules.ExtractPayload>(job.Payload, IndexJobs.Json)!.AttachmentId);
        Assert.Equal(QaSeed.Org, job.OrgId);
    }
    [Fact]
    public async Task Empty_vision_text_finishes_done_without_reindexing_or_fabricated_text()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        gateway.VisionText = "";
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var store = new AttachmentStore(data.Configuration);
        await using var db = data.CreateDbContext();
        var upload = await new AttachmentService(db, store, TimeProvider.System, data.Resolver).UploadAsync(
            AiTestDatabase.Admin, QaSeed.DeployRunbookPage, "empty.png", "image/png", new MemoryStream(Png), ct);
        var attachment = await db.Attachments.SingleAsync(a => a.Id == upload.AttachmentId, ct);
        try
        {
            var job = await db.Jobs.SingleAsync(j => j.Kind == AttachmentRules.ExtractKind && j.Key == upload.AttachmentId.ToString(), ct);
            await new ExtractAttachmentJob(store, TimeProvider.System, NullLogger<ExtractAttachmentJob>.Instance, data.Resolver).RunAsync(job, db, ct);
            await db.SaveChangesAsync(ct);
            Assert.Equal(ExtractStatus.Done, attachment.ExtractStatus);
            Assert.Equal("", attachment.TextContent);
            Assert.False(await db.Jobs.AnyAsync(j => j.Kind == IndexJobs.EmbedPage && j.Key.EndsWith($":attachment:{upload.AttachmentId}"), ct));
            Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "attachment.vision" && a.ResourceId == upload.AttachmentId.ToString(), ct));
        }
        finally { File.Delete(store.PathOf(attachment.StorageKey)); }
    }

    [Theory]
    [InlineData(11 * 1024 * 1024)]
    [InlineData(25 * 1024 * 1024)]
    public async Task Supported_images_up_to_the_attachment_limit_reach_vision(int size)
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var store = new AttachmentStore(data.Configuration);
        await using var db = data.CreateDbContext();
        // Generated padding tests the byte boundary without a large checked-in fixture.
        var bytes = new byte[size];
        Array.Fill(bytes, (byte)0xfb); // base64 includes many + characters: JSON must not inflate them beyond the request cap
        Png.CopyTo(bytes, 0);
        var upload = await new AttachmentService(db, store, TimeProvider.System, data.Resolver).UploadAsync(
            AiTestDatabase.Admin, QaSeed.DeployRunbookPage, "large.png", "image/png", new MemoryStream(bytes), ct);
        var attachment = await db.Attachments.SingleAsync(a => a.Id == upload.AttachmentId, ct);
        try
        {
            Assert.Equal("pending", upload.ExtractStatus);
            var job = await db.Jobs.SingleAsync(j => j.Kind == AttachmentRules.ExtractKind && j.Key == upload.AttachmentId.ToString(), ct);
            await new ExtractAttachmentJob(store, TimeProvider.System, NullLogger<ExtractAttachmentJob>.Instance, data.Resolver).RunAsync(job, db, ct);
            await db.SaveChangesAsync(ct);
            Assert.Equal(ExtractStatus.Done, attachment.ExtractStatus);
            Assert.Equal(gateway.VisionText, attachment.TextContent);
            Assert.Single(gateway.Requests, r => r.Path == "chat/completions");
        }
        finally { File.Delete(store.PathOf(attachment.StorageKey)); }
    }

    [Fact]
    public async Task Upload_over_25MiB_is_rejected_before_persistence_or_inference()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        await using var db = data.CreateDbContext();
        var before = await db.Attachments.CountAsync(ct);
        var bytes = new byte[AttachmentRules.MaxBytes + 1];
        Png.CopyTo(bytes, 0);
        var uploads = new AttachmentService(db, new AttachmentStore(data.Configuration), TimeProvider.System, data.Resolver);
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<AtharError>(() => uploads.UploadAsync(
            AiTestDatabase.Admin, QaSeed.DeployRunbookPage, "too-large.png", "image/png", new MemoryStream(bytes), ct))).Code);
        Assert.Equal(before, await db.Attachments.CountAsync(ct));
        Assert.Empty(gateway.Requests);
    }

}
