using System.Text.Json;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Services;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

[Collection(AtharCollection.Name)]
public sealed class AiRuntimeTests(AtharApp app)
{
    [Fact]
    public async Task Saved_override_works_with_incomplete_environment_and_query_keeps_its_identity_during_inference()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        data.Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Athar:Ai:BaseUrl"] = "http://127.0.0.1:1/v1", ["Athar:Ai:ApiKey"] = null }).Build();
        var draft = AiTestDatabase.Draft(gateway);
        await data.Settings.SaveAsync(AiTestDatabase.Admin, draft, ct);
        var original = await data.Resolver.ResolveAsync(QaSeed.Org, ct);
        await using var db = data.CreateDbContext();
        var search = new SearchService(db, data.Embedder);
        Assert.True((await search.RankAsync(QaSeed.Org, "deployment", [], [QaSeed.MemoryProject], 10, ct)).Degraded);
        var revision = await db.Chunks.Where(c => c.OwnerId == QaSeed.MemoryProject).Select(c => c.OwnerRevision).FirstAsync(ct);
        var job = IndexJobs.Vectors(ChunkOwner.Memory, QaSeed.MemoryProject, revision, DateTimeOffset.UtcNow, original.EmbeddingIdentity);
        job.OrgId = QaSeed.Org;
        await new EmbedVectorsJob(data.Clients).RunAsync(job, db, ct);
        await db.SaveChangesAsync(ct);
        db.Jobs.Add(new Job { OrgId = QaSeed.Org, Kind = IndexJobs.EmbedVectors, Key = "done-history", Status = JobStatus.Done,
            Payload = JsonSerializer.Serialize("historical payload that must not be read"), RunAfter = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        var status = await data.Settings.IndexStatusAsync(AiTestDatabase.Admin, ct);
        Assert.True(status.EmbeddedChunks > 0 && status.EmbeddedChunks < status.TotalChunks);
        Assert.False((await search.RankAsync(QaSeed.Org, "deployment", [], [QaSeed.MemoryProject], 10, ct)).Degraded); // partial coverage is not a new degraded contract
        gateway.BeforeResponse = async r =>
        {
            if (r.Path != "embeddings") return;
            gateway.BeforeResponse = null;
            await data.Settings.SaveAsync(AiTestDatabase.Admin, draft with { ExpectedRevision = 1, EmbeddingModel = "qa-next" }, ct);
        };
        Assert.False((await search.RankAsync(QaSeed.Org, "deployment", [], [QaSeed.MemoryProject], 10, ct)).Degraded);
        Assert.NotEqual(original.EmbeddingIdentity, (await data.Resolver.ResolveAsync(QaSeed.Org, ct)).EmbeddingIdentity);
        Assert.True((await search.RankAsync(QaSeed.Org, "deployment", [], [QaSeed.MemoryProject], 10, ct)).Degraded);
        var calls = gateway.Requests.Count;
        await new EmbedVectorsJob(data.Clients).RunAsync(job, db, ct); // obsolete targeted job
        Assert.Equal(calls, gateway.Requests.Count);
        Assert.True(await db.ChunkEmbeddings.AnyAsync(e => e.ModelId == original.EmbeddingIdentity, ct)); // old vectors preserved
        // A frozen recall snapshot is explicitly reusable by later ranking/top-up calls.
        Assert.False((await search.RankAsync(QaSeed.Org, "deployment", [], [QaSeed.MemoryProject], 10, ct, snapshot: original)).Degraded);
    }

    [Fact]
    public async Task Sweep_is_per_org_generation_specific_and_does_not_resurrect_dead_jobs()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var services = new ServiceCollection().AddScoped(_ => data.CreateDbContext()).BuildServiceProvider();
        await using var provider = services;
        var sweep = new ReembedSweep(provider.GetRequiredService<IServiceScopeFactory>(), data.Resolver, TimeProvider.System, NullLogger<ReembedSweep>.Instance);
        Assert.True(await sweep.SweepAsync(ct) > 0);
        var original = await data.Resolver.ResolveAsync(QaSeed.Org, ct);
        await using (var db = data.CreateDbContext())
        {
            var jobs = await db.Jobs.Where(j => j.Kind == IndexJobs.EmbedVectors && j.Key.EndsWith(original.EmbeddingIdentity)).OrderBy(j => j.Id).ToListAsync(ct);
            Assert.True(jobs.Count > 1);
            jobs[0].Status = JobStatus.Dead;
            jobs[1].Status = JobStatus.Running;
            await db.SaveChangesAsync(ct);
        }
        await sweep.SweepAsync(ct);
        await using (var check = data.CreateDbContext())
            Assert.True(await check.Jobs.AnyAsync(j => j.Kind == IndexJobs.EmbedVectors && j.Key.EndsWith(original.EmbeddingIdentity) && j.Status == JobStatus.Dead, ct));
        await data.Settings.ReindexAsync(AiTestDatabase.Admin, 1, ct);
        await sweep.SweepAsync(ct);
        var next = await data.Resolver.ResolveAsync(QaSeed.Org, ct);
        await using var final = data.CreateDbContext();
        Assert.True(await final.Jobs.AnyAsync(j => j.Key.EndsWith(next.EmbeddingIdentity) && j.Status == JobStatus.Ready, ct));
        final.CurrentOrgId = QaSeed.OtherOrg;
        Assert.False(await final.AiSettings.AnyAsync(ct));
        Assert.Equal("bge-m3", (await data.Resolver.ResolveAsync(QaSeed.OtherOrg, ct)).EmbeddingIdentity);
    }

    [Fact]
    public async Task Session_extraction_and_similarity_use_one_frozen_snapshot()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        var draft = AiTestDatabase.Draft(gateway);
        await data.Settings.SaveAsync(AiTestDatabase.Admin, draft, ct);
        gateway.Candidates = [new { evidence = "synthetic release rule", title = "Synthetic rule", body_md = "Every zephyr release requires a unique violet seal.", type = "fact", scope = "personal" }];
        await using var db = data.CreateDbContext();
        (await db.AgentSessions.SingleAsync(s => s.Id == QaSeed.Session, ct)).ExtractedUpto = -1;
        await db.SaveChangesAsync(ct);
        gateway.BeforeResponse = async r =>
        {
            if (r.Path != "chat/completions") return;
            gateway.BeforeResponse = null;
            await data.Settings.SaveAsync(AiTestDatabase.Admin, draft with { ExpectedRevision = 1, EmbeddingModel = "qa-after-extraction", QueryModel = "qa-after-query" }, ct);
        };
        var job = IndexJobs.Extract(QaSeed.Session, 2, DateTimeOffset.UtcNow);
        job.OrgId = QaSeed.Org;
        await new ExtractSessionJob(data.Clients).RunAsync(job, db, ct);
        await db.SaveChangesAsync(ct);
        Assert.Equal(gateway.QueryModel, gateway.Requests.First(r => r.Path == "chat/completions").Body.GetProperty("model").GetString());
        Assert.All(gateway.Requests.Where(r => r.Path == "embeddings"), r => Assert.Equal(gateway.EmbeddingModel, r.Body.GetProperty("model").GetString()));
        Assert.Contains(gateway.Requests, r => r.Path == "embeddings");
        Assert.True(await db.CaptureCandidates.AnyAsync(c => c.Title == "Synthetic rule", ct));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "session.extract", ct));
    }

    [Fact]
    public async Task Answer_uses_only_permitted_evidence_and_rechecks_policy_after_inference()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var input = new SearchInput("deployment", Kinds: ["page"], SpaceIds: [QaSeed.EngWiki]);
        var answer = await data.Answers.AnswerAsync(AiTestDatabase.Admin, input, ct);
        Assert.NotEmpty(answer.Text);
        Assert.All(answer.Sources, h => Assert.Equal(QaSeed.EngWiki, h.SpaceId));
        await using (var auditDb = data.CreateDbContext())
        {
            var audit = await auditDb.AuditEvents.SingleAsync(a => a.Action == "ai.query", ct);
            Assert.Equal(AiTestDatabase.Admin.UserId, audit.ActorUserId);
            Assert.DoesNotContain(answer.Text, audit.After!);
            Assert.DoesNotContain(input.Query, audit.After!);
            Assert.Contains(answer.Sources[0].Id.ToString(), audit.After!);
        }
        Assert.Contains("language of the question", gateway.Requests.Last(r => r.Path == "chat/completions").Body.GetProperty("messages")[0].GetProperty("content").GetString());
        var prompt = gateway.Requests.Last(r => r.Path == "chat/completions").Body.GetProperty("messages")[1].GetProperty("content").GetString()!;
        using var payload = JsonDocument.Parse(prompt);
        Assert.InRange(payload.RootElement.GetProperty("evidence").GetArrayLength(), 1, 6);
        Assert.All(payload.RootElement.GetProperty("evidence").EnumerateArray(), e => Assert.True(e.GetProperty("text").GetString()!.Length <= 2000));
        Assert.DoesNotContain("salary", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canary", prompt, StringComparison.OrdinalIgnoreCase);
        var before = gateway.Requests.Count;
        Assert.Equal("insufficient_evidence", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.AnswerAsync(AiTestDatabase.Admin,
            input with { SpaceIds = [QaSeed.OpsHandbook] }, ct))).Code);
        Assert.Equal(before, gateway.Requests.Count);
        gateway.ChatOverride = "{\"answer\":\"Unsupported citation\",\"citations\":[999]}";
        Assert.Equal("provider_unavailable", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.AnswerAsync(AiTestDatabase.Admin, input, ct))).Code);
        gateway.ChatOverride = "{\"answer\":\"The evidence does not answer this question.\",\"citations\":[]}";
        Assert.Equal("insufficient_evidence", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.AnswerAsync(AiTestDatabase.Admin, input, ct))).Code);
        gateway.ChatOverride = null;
        gateway.BeforeResponse = async r =>
        {
            if (r.Path != "chat/completions") return;
            await using var changed = data.CreateDbContext();
            (await changed.Spaces.SingleAsync(s => s.Id == QaSeed.EngWiki, ct)).AiPolicy = new(Read: false);
            await changed.SaveChangesAsync(ct);
        };
        Assert.Equal("insufficient_evidence", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.AnswerAsync(AiTestDatabase.Admin, input, ct))).Code);
        gateway.BeforeResponse = null;
        gateway.StatusCode = 503;
        await using var db = data.CreateDbContext();
        var search = await new SearchService(db, data.Embedder).SearchAsync(AiTestDatabase.Admin, input, ct);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Action == "ai.query", ct));
        Assert.True(search.Degraded);
        Assert.NotEmpty(search.Hits); // ordinary human FTS survives provider failure and AI-read off
    }
}
