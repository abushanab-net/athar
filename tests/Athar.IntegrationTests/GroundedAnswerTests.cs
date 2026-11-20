using System.Text.Json;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>Conversation turns and research reports: scope before ranking, every dependency kept, context refused once it changed.</summary>
[Collection(AtharCollection.Name)]
public sealed class GroundedAnswerTests(AtharApp app)
{
    private sealed class Commit(bool fail = false) : IGroundedCommit
    {
        public int Calls; public bool InTransaction;
        public Task WriteAsync(AtharDbContext db, GroundedResult result, CancellationToken ct)
        {
            Calls++; InTransaction = db.Database.CurrentTransaction is not null;
            return fail ? throw new InvalidOperationException("synthetic commit failure") : Task.CompletedTask;
        }
    }

    private static string[] Evidence(FakeAiGateway gateway)
    {
        using var prompt = JsonDocument.Parse(gateway.Requests.Last(r => r.Path == "chat/completions").Body.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return [.. prompt.RootElement.GetProperty("evidence").EnumerateArray().Select(e => e.GetProperty("text").GetString()!)];
    }

    [Fact]
    public async Task A_turn_stays_inside_its_scope_is_saved_with_its_audit_row_and_its_context_is_refused_once_a_source_changes()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var scope = new GroundingScope(Owners: new Dictionary<string, Guid[]> { ["page"] = [QaSeed.DeployRunbookPage] });
        var commit = new Commit();
        var first = await data.Answers.GroundedAsync(AiTestDatabase.Admin, new("How do deploys run?", scope), commit, ct: ct);
        Assert.NotEmpty(first.Text);
        Assert.Equal(1, Assert.Single(first.Citations).Ordinal);
        Assert.All(first.Citations, c => Assert.Equal(QaSeed.DeployRunbookPage, c.Hit.Id));
        // Every grounding chunk is a dependency, cited or not, and none carries text.
        Assert.All(first.Dependencies, d => { Assert.Equal("page", d.Kind); Assert.Equal(QaSeed.DeployRunbookPage, d.OwnerId); });
        Assert.Contains(first.Citations[0].Ref, first.Dependencies);
        Assert.True(commit is { Calls: 1, InTransaction: true });
        Assert.All(Evidence(gateway), text => Assert.DoesNotContain("pnpm", text));
        await using (var audit = data.CreateDbContext())
        {
            var row = await audit.AuditEvents.SingleAsync(a => a.Action == "ai.chat", ct);
            Assert.DoesNotContain(first.Text, row.After!);
            Assert.DoesNotContain("deploys", row.After!);
        }

        // A failing save takes the audit row with it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Answers.GroundedAsync(AiTestDatabase.Admin, new("How do deploys run?", scope), new Commit(fail: true), ct: ct));
        await using (var audit = data.CreateDbContext()) Assert.Equal(1, await audit.AuditEvents.CountAsync(a => a.Action == "ai.chat", ct));

        // An empty selection reads nothing and calls no model.
        var before = gateway.Requests.Count;
        Assert.Equal("insufficient_evidence", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.GroundedAsync(AiTestDatabase.Admin,
            new("How do deploys run?", new(Owners: new Dictionary<string, Guid[]> { ["page"] = [] })), ct: ct))).Code);
        Assert.Equal(before, gateway.Requests.Count);

        // A follow-up is rewritten with the conversation, then grounded on its own.
        GroundedTurn[] context = [new("How do deploys run?", first.Text)];
        var second = await data.Answers.GroundedAsync(AiTestDatabase.Admin, new("And how do I roll back?", scope, Context: context, ContextDependencies: first.Dependencies), ct: ct);
        var chats = gateway.Requests.Where(r => r.Path == "chat/completions").Skip(2).ToArray();
        Assert.True(chats[^2].Body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("properties").TryGetProperty("query", out _));
        Assert.Contains("roll back", chats[^2].Body.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Superset(first.Dependencies.ToHashSet(), second.Dependencies.ToHashSet());
        Assert.Equal(new[] { true }, await data.Answers.CurrentAsync(AiTestDatabase.Admin, scope, [second.Dependencies], ct));
        // Outside the conversation's scope the same references do not hold.
        Assert.Equal(new[] { false }, await data.Answers.CurrentAsync(AiTestDatabase.Admin, new(Owners: new Dictionary<string, Guid[]> { ["page"] = [QaSeed.HomePage] }), [second.Dependencies], ct));

        // The page moves on: the old answers may no longer be shown to a model, or to the reader.
        // It moves while the follow-up is being rewritten: the rewritten query may carry the old answer, so it is not even embedded.
        gateway.BeforeResponse = async r =>
        {
            if (r.Path != "chat/completions") return;
            gateway.BeforeResponse = null;
            await using var changed = data.CreateDbContext();
            (await changed.Pages.SingleAsync(p => p.Id == QaSeed.DeployRunbookPage, ct)).Revision++;
            await changed.SaveChangesAsync(ct);
        };
        var embedded = gateway.Requests.Count(r => r.Path == "embeddings");
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.GroundedAsync(AiTestDatabase.Admin,
            new("And after that?", scope, Context: context, ContextDependencies: first.Dependencies), ct: ct))).Code);
        Assert.Equal(embedded, gateway.Requests.Count(r => r.Path == "embeddings"));
        before = gateway.Requests.Count;
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.GroundedAsync(AiTestDatabase.Admin,
            new("And after that?", scope, Context: context, ContextDependencies: first.Dependencies), ct: ct))).Code);
        Assert.Equal(before, gateway.Requests.Count);
        Assert.Equal(new[] { false, true }, await data.Answers.CurrentAsync(AiTestDatabase.Admin, scope, [first.Dependencies, []], ct));
    }

    [Fact]
    public async Task A_project_scope_reads_only_what_belongs_to_the_project_and_nothing_for_someone_outside_it()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        var scope = new GroundingScope(ProjectIds: [QaSeed.PilotProject]);
        // "pnpm" is a company memory and "handbook" a page of an unlinked space: neither belongs to the project.
        var result = await data.Answers.GroundedAsync(AiTestDatabase.Admin, new("pnpm handbook MudBlazor deploy", scope), ct: ct);
        Assert.All(Evidence(gateway), text => { Assert.DoesNotContain("pnpm", text); Assert.DoesNotContain("Shifts, escalation", text); });
        Assert.All(result.Dependencies, d => Assert.Contains(d.Kind, new[] { "page", "memory", "source" }));
        await using (var db = data.CreateDbContext())
        {
            var pages = result.Dependencies.Where(d => d.Kind == "page").Select(d => d.OwnerId).ToArray();
            Assert.True(await db.Pages.Where(p => pages.Contains(p.Id)).AllAsync(p => p.SpaceId == QaSeed.EngWiki, ct));
            var memories = result.Dependencies.Where(d => d.Kind == "memory").Select(d => d.OwnerId).ToArray();
            Assert.True(await db.Memories.Where(m => memories.Contains(m.Id)).AllAsync(m => m.OwnerProjectId == QaSeed.PilotProject, ct));
        }
        // The knowledge owner reads the linked wiki but is not in the project: the project scope gives them nothing.
        var before = gateway.Requests.Count;
        Assert.Equal("insufficient_evidence", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.GroundedAsync(
            AiTestDatabase.Person(QaSeed.KnowledgeOwner), new("deploy", scope), ct: ct))).Code);
        Assert.Equal(before, gateway.Requests.Count);
    }

    [Fact]
    public async Task Research_runs_at_most_three_searches_and_returns_a_report_whose_findings_are_all_cited()
    {
        var ct = AtharApp.Timeout();
        await using var data = new AiTestDatabase(app);
        await data.InitializeAsync(ct);
        await using var gateway = await FakeAiGateway.StartAsync();
        await data.Settings.SaveAsync(AiTestDatabase.Admin, AiTestDatabase.Draft(gateway), ct);
        gateway.ResearchQueries = ["deploy", "rollback", "incident", "leave", "release"];
        var phases = new List<GroundingProgress>();
        var report = await data.Answers.GroundedAsync(AiTestDatabase.Admin, new("How do we ship and recover?", GroundingScope.Everything, GroundingFormat.Report),
            progress: p => { phases.Add(p); return Task.CompletedTask; }, ct: ct);
        Assert.Equal(3, report.Searches);
        Assert.Equal(3, gateway.Requests.Count(r => r.Path == "embeddings"));
        Assert.Equal([1, 2, 3], phases.Where(p => p.Phase == GroundingPhase.Searching).Select(p => p.Step));
        Assert.Equal(GroundingPhase.Preparing, phases[0].Phase);
        Assert.NotEmpty(report.Text);
        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, f => Assert.All(f.Citations, i => Assert.Contains(report.Citations, c => c.Ordinal == i)));
        Assert.InRange(report.Dependencies.Count, 1, 12);
        await using (var audit = data.CreateDbContext()) Assert.True(await audit.AuditEvents.AnyAsync(a => a.Action == "ai.research", ct));
        // A report with a finding that cites nothing is refused.
        gateway.ChatOverride = "{\"queries\":[\"deploy\"],\"summary\":\"s\",\"gaps\":\"\",\"findings\":[{\"title\":\"t\",\"text\":\"x\",\"citations\":[]}]}";
        Assert.Equal("provider_unavailable", (await Assert.ThrowsAsync<AtharError>(() => data.Answers.GroundedAsync(AiTestDatabase.Admin,
            new("How do we ship?", GroundingScope.Everything, GroundingFormat.Report), ct: ct))).Code);
    }
}
