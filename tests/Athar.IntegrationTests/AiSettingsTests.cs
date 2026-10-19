using System.Text.Json;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Athar.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Athar.IntegrationTests;

[Collection(AtharCollection.Name)]
public sealed class AiSettingsTests(AtharApp app)
{
    [Fact]
    public async Task Every_admin_operation_reloads_membership_and_refuses_agents_and_cross_org_callers()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        var service = data.Settings;
        var draft = AiTestDatabase.Draft(gateway);
        async Task Denied(Athar.Web.Mcp.Actor actor)
        {
            Func<Task>[] actions = [async () => await service.ReadAsync(actor, ct), () => service.SaveAsync(actor, draft, ct),
                () => service.ResetAsync(actor, 0, ct), () => service.ReindexAsync(actor, 0, ct),
                async () => await service.ListModelsAsync(actor, draft, ct), async () => await service.TestAsync(actor, draft, ct),
                async () => await service.IndexStatusAsync(actor, ct)];
            foreach (var action in actions) Assert.Equal("forbidden", (await Assert.ThrowsAsync<AtharError>(action)).Code);
        }
        foreach (var actor in new[] { AiTestDatabase.Person(QaSeed.Member), AiTestDatabase.Person(QaSeed.Deactivated),
            AiTestDatabase.Person(QaSeed.OtherOrgOwner), AiTestDatabase.Admin with { CredentialId = QaSeed.AdminPatId },
            AiTestDatabase.Admin with { CredentialId = QaSeed.AdminPatId, Disclosure = DisclosureClass.ExternalAgent } }) await Denied(actor);
        Assert.Empty(gateway.Requests);
        Assert.Equal(0, (await service.ReadAsync(AiTestDatabase.Owner, ct)).Revision);
        await using (var db = data.CreateDbContext())
        {
            (await db.OrgMembers.SingleAsync(m => m.UserId == QaSeed.Admin, ct)).Role = OrgRole.Member;
            await db.SaveChangesAsync(ct);
        }
        await Denied(AiTestDatabase.Admin);
        await using var check = data.CreateDbContext();
        Assert.False(await check.AiSettings.AnyAsync(ct));
        Assert.False(await check.AuditEvents.AnyAsync(a => a.Action.StartsWith("ai_settings."), ct));
    }

    [Fact]
    public async Task Index_status_counts_only_the_active_generation_and_preserves_legacy_default_jobs()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        var before = await data.Settings.IndexStatusAsync(AiTestDatabase.Admin, ct);
        var sequence = 1000;
        async Task Add(JobStatus state, string? identity, Guid? org = null)
        {
            await using var db = data.CreateDbContext();
            var job = IndexJobs.Vectors(ChunkOwner.Memory, QaSeed.MemoryProject, sequence++, DateTimeOffset.UtcNow, identity);
            job.OrgId = org ?? QaSeed.Org;
            job.Status = state;
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
        }
        await Add(JobStatus.Dead, null);
        Assert.Equal(before.FailedJobs + 1, (await data.Settings.IndexStatusAsync(AiTestDatabase.Admin, ct)).FailedJobs);

        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var active = (await data.Resolver.ResolveAsync(QaSeed.Org, ct)).EmbeddingIdentity;
        foreach (var state in new[] { JobStatus.Ready, JobStatus.Running, JobStatus.Dead })
        {
            await Add(state, active);
            await Add(state, "ai:previous-generation");
            await Add(state, null);
        }
        await Add(JobStatus.Done, active);
        await Add(JobStatus.Dead, active, QaSeed.OtherOrg);
        var current = await data.Settings.IndexStatusAsync(AiTestDatabase.Admin, ct);
        Assert.Equal((1L, 1L, 1L), (current.ReadyJobs, current.RunningJobs, current.FailedJobs));

        await data.Settings.ReindexAsync(AiTestDatabase.Admin, 1, ct);
        var rebuilt = await data.Settings.IndexStatusAsync(AiTestDatabase.Admin, ct);
        Assert.NotEqual(active, rebuilt.EmbeddingIdentity);
        Assert.Equal((0L, 0L, 0L), (rebuilt.ReadyJobs, rebuilt.RunningJobs, rebuilt.FailedJobs));
        await using var check = data.CreateDbContext();
        Assert.True(await check.Jobs.AnyAsync(j => j.Kind == IndexJobs.EmbedVectors && j.Status == JobStatus.Dead, ct));
    }

    [Fact]
    public async Task Revisions_survive_reset_recreate_and_environment_rebuild_and_keys_never_leak()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        var service = data.Settings;
        var actor = AiTestDatabase.Admin;
        const string secret = "synthetic-key-never-returned";
        Assert.Equal("bge-m3", (await data.Resolver.ResolveAsync(QaSeed.Org, ct)).EmbeddingIdentity);
        var first = AiTestDatabase.Draft(gateway, key: ApiKeyChange.Replace, secret: secret);
        // Two first writers serialize on the organization row; exactly one wins.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await service.SaveAsync(actor, first, ct); return "saved"; }
            catch (AtharError e) { return e.Code; }
        }));
        Assert.Equal(new[] { "conflict", "saved" }, attempts.Order().ToArray());
        var s1 = await data.Resolver.ResolveAsync(QaSeed.Org, ct);
        Assert.Equal(secret, s1.ApiKey);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(await service.ReadAsync(actor, ct)));
        await service.SaveAsync(actor, first with { ExpectedRevision = 1, QueryModel = "changed-query", VisionModel = null, KeyChange = ApiKeyChange.Clear, NewApiKey = null }, ct);
        var s2 = await data.Resolver.ResolveAsync(QaSeed.Org, ct);
        Assert.Equal(s1.EmbeddingIdentity, s2.EmbeddingIdentity);
        Assert.Null(s2.ApiKey);
        await service.ResetAsync(actor, 2, ct);
        var reset = await service.ReadAsync(actor, ct);
        Assert.True(reset.UsesEnvironment);
        Assert.Equal(3, reset.Revision);
        Assert.Equal("bge-m3", (await data.Resolver.ResolveAsync(QaSeed.Org, ct)).EmbeddingIdentity);
        await service.SaveAsync(actor, first with { ExpectedRevision = 3 }, ct);
        foreach (var stale in new long[] { 0, 1, 2, 3 })
        {
            Assert.Equal("conflict", (await Assert.ThrowsAsync<AtharError>(() => service.SaveAsync(actor, first with { ExpectedRevision = stale }, ct))).Code);
            Assert.Equal("conflict", (await Assert.ThrowsAsync<AtharError>(() => service.ResetAsync(actor, stale, ct))).Code);
            Assert.Equal("conflict", (await Assert.ThrowsAsync<AtharError>(() => service.ReindexAsync(actor, stale, ct))).Code);
        }
        await service.ResetAsync(actor, 4, ct);
        var before = await data.Resolver.ResolveAsync(QaSeed.Org, ct);
        await service.ReindexAsync(actor, 5, ct);
        var after = await data.Resolver.ResolveAsync(QaSeed.Org, ct);
        Assert.True(after.UsesEnvironment);
        Assert.NotEqual(before.EmbeddingIdentity, after.EmbeddingIdentity);
        Assert.Equal(after.EmbeddingIdentity, (await data.Resolver.ResolveAsync(QaSeed.Org, ct)).EmbeddingIdentity);
        await using var db = data.CreateDbContext();
        Assert.Equal(6, (await db.AiSettings.SingleAsync(ct)).Revision);
        var audit = await db.AuditEvents.Where(a => a.Action.StartsWith("ai_settings.")).ToListAsync(ct);
        Assert.Equal(6, audit.Count);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(audit));
        await service.SaveAsync(actor, first with { ExpectedRevision = 6 }, ct);
        await service.ResetAsync(actor, 7, ct);
        Assert.Equal(after.EmbeddingIdentity, (await data.Resolver.ResolveAsync(QaSeed.Org, ct)).EmbeddingIdentity);
        Assert.Equal(8, (await service.ReadAsync(actor, ct)).Revision);
    }

    [Fact]
    public async Task Key_keep_clear_endpoint_changes_and_unreadable_key_recovery_are_explicit()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        data.Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Athar:Ai:BaseUrl"] = gateway.BaseUrl, ["Athar:Ai:ApiKey"] = "environment-secret",
            ["Athar:Ai:EmbeddingModel"] = gateway.EmbeddingModel }).Build();
        var draft = AiTestDatabase.Draft(gateway, key: ApiKeyChange.Keep);
        await data.Settings.SaveAsync(AiTestDatabase.Admin, draft, ct);
        Assert.Equal("environment-secret", (await data.Resolver.ResolveAsync(QaSeed.Org, ct)).ApiKey);
        foreach (var operation in new Func<AiSettingsWrite, Task>[] {
            d => data.Settings.SaveAsync(AiTestDatabase.Admin, d, ct),
            async d => await data.Settings.ListModelsAsync(AiTestDatabase.Admin, d, ct),
            async d => await data.Settings.TestAsync(AiTestDatabase.Admin, d, ct) })
            Assert.Equal("invalid_input", (await Assert.ThrowsAsync<AtharError>(() => operation(draft with { ExpectedRevision = 1, BaseUrl = gateway.BaseUrl + "/different" }))).Code);
        Assert.Empty(gateway.Requests);
        var brokenResolver = new AiSettingsResolver(data, data.Configuration, new EphemeralDataProtectionProvider());
        var broken = new AiSettingsService(data, brokenResolver);
        var read = await broken.ReadAsync(AiTestDatabase.Admin, ct);
        Assert.True(read.KeyUnreadable);
        Assert.True(read.HasApiKey);
        await using (var ftsDb = data.CreateDbContext())
        {
            var embedder = new QueryEmbedder(data.Configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<QueryEmbedder>.Instance, brokenResolver);
            var fts = await new SearchService(ftsDb, embedder).SearchAsync(AiTestDatabase.Admin, new("deployment"), ct);
            Assert.True(fts.Degraded);
            Assert.NotEmpty(fts.Hits);
        }
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<AtharError>(() => broken.SaveAsync(AiTestDatabase.Admin, draft with { ExpectedRevision = 1 }, ct))).Code);
        await broken.SaveAsync(AiTestDatabase.Admin, draft with { ExpectedRevision = 1, KeyChange = ApiKeyChange.Clear }, ct);
        Assert.Null((await brokenResolver.ResolveAsync(QaSeed.Org, ct)).ApiKey); // never falls through to deployment key
        await broken.SaveAsync(AiTestDatabase.Admin, draft with { ExpectedRevision = 2, KeyChange = ApiKeyChange.Replace, NewApiKey = "replacement" }, ct);
        Assert.Equal("replacement", (await brokenResolver.ResolveAsync(QaSeed.Org, ct)).ApiKey);
        var wrongOrg = data.Resolver.Resolve(QaSeed.OtherOrg, new AiSettings { UsesEnvironment = false,
            BaseUrl = gateway.BaseUrl, EmbeddingModel = gateway.EmbeddingModel, QueryModel = gateway.QueryModel,
            ProtectedApiKey = data.Resolver.Protect(QaSeed.Org, "bound-to-org") });
        Assert.True(wrongOrg.KeyUnreadable);
        await data.Settings.ResetAsync(AiTestDatabase.Admin, 3, ct); // reset works even with unreadable ciphertext
        Assert.True((await data.Settings.ReadAsync(AiTestDatabase.Admin, ct)).UsesEnvironment);
    }

    [Fact]
    public async Task Synthetic_probes_test_every_selected_role_and_strict_query_output()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        var draft = AiTestDatabase.Draft(gateway);
        Assert.Equal(3, (await data.Settings.ListModelsAsync(AiTestDatabase.Admin, draft, ct)).Count);
        var discovery = draft with { EmbeddingModel = "", QueryModel = "", VisionModel = null };
        Assert.Equal(3, (await data.Settings.ListModelsAsync(AiTestDatabase.Admin, discovery, ct)).Count);
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<AtharError>(() => data.Settings.SaveAsync(AiTestDatabase.Admin, discovery, ct))).Code);
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<AtharError>(() => data.Settings.TestAsync(AiTestDatabase.Admin, discovery, ct))).Code);
        Assert.Equal("conflict", (await Assert.ThrowsAsync<AtharError>(() => data.Settings.ListModelsAsync(AiTestDatabase.Admin, discovery with { ExpectedRevision = 1 }, ct))).Code);
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<AtharError>(() => data.Settings.ListModelsAsync(AiTestDatabase.Admin, discovery with { BaseUrl = "http://169.254.169.254" }, ct))).Code);
        Assert.Equal("invalid_input", (await Assert.ThrowsAsync<AtharError>(() => data.Settings.ListModelsAsync(AiTestDatabase.Admin, discovery with { KeyChange = ApiKeyChange.Replace, NewApiKey = "" }, ct))).Code);
        Assert.All((await data.Settings.TestAsync(AiTestDatabase.Admin, draft, ct)).Roles, r => Assert.Equal((true, "ok"), (r.Success, r.Code)));
        var query = gateway.Requests.Last(r => r.Path == "chat/completions" && r.Body.GetProperty("model").GetString() == gateway.QueryModel);
        Assert.True(query.Body.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        gateway.ChatOverride = "{\"ok\":true,\"extra\":1}";
        var failed = await data.Settings.TestAsync(AiTestDatabase.Admin, draft, ct);
        Assert.Equal("invalid_response", failed.Roles.Single(r => r.Role == "query").Code);
        gateway.StatusCode = 503;
        Assert.All((await data.Settings.TestAsync(AiTestDatabase.Admin, draft, ct)).Roles, r => Assert.Equal("provider_unavailable", r.Code));
        Assert.DoesNotContain("SECRET", (await Assert.ThrowsAsync<AtharError>(() => data.Settings.ListModelsAsync(AiTestDatabase.Admin, draft, ct))).Message);
        await using var db = data.CreateDbContext();
        Assert.False(await db.AiSettings.AnyAsync(ct)); // probes never persist a draft
    }

    [Fact]
    public async Task A_failure_after_saving_rolls_back_settings_and_audit_together()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        var options = new DbContextOptionsBuilder<AtharDbContext>()
            .UseNpgsql(app.ConnectionString(data.Name), n => n.UseVector()).AddInterceptors(new FailAfterSave()).Options;
        var service = new AiSettingsService(new FaultingFactory(options), data.Resolver);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct));
        await using var db = data.CreateDbContext();
        Assert.False(await db.AiSettings.AnyAsync(ct));
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Action.StartsWith("ai_settings."), ct));
    }

    private sealed class FaultingFactory(DbContextOptions<AtharDbContext> options) : IDbContextFactory<AtharDbContext>
    {
        public AtharDbContext CreateDbContext() => new(options);
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic failure before transaction commit.");
    }
}
