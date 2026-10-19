using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Athar.Web.Services.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T178: the dashboard service (FR-AG-11) for a member and an admin.</summary>
[Collection(AtharCollection.Name)]
public class DashboardTests(AtharApp athar)
{
    private static readonly Guid Org = QaSeed.Org;
    private static readonly Guid ClaudeCodeClient = Guid.Parse("00000000-0000-7000-8000-0000000c0de1"); // seeded as "Claude Code"
    private static readonly string[] All = ["read", "write", "memory:write", "pages:delete"];

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Actor Person(Guid user) => new(user, Org, null, All.ToHashSet(), DisclosureClass.TrustedLocal, null);

    [Fact]
    public async Task AT_69_member_sees_own_activity_admin_sees_the_org_and_titles_follow_read_access()
    {
        var ct = AtharApp.Timeout();
        // Noon on a day far enough ahead that no other test writes to its window.
        var today = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(700 + Random.Shared.Next(300));
        var noon = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var at = noon.AddHours(-2);
        var yesterday = today.AddDays(-1);

        await using var db = athar.Db(Org);
        db.McpCalls.AddRange(
            new McpCall { ActorUserId = QaSeed.Member, OauthClientId = ClaudeCodeClient, Tool = "recall", DurationMs = 10, Outcome = "ok", ResultCount = 2, CreatedAt = at },
            new McpCall { ActorUserId = QaSeed.Member, OauthClientId = ClaudeCodeClient, Tool = "recall", DurationMs = 30, Outcome = "ok", ResultCount = 0, CreatedAt = at },
            new McpCall { ActorUserId = QaSeed.Member, ClaimedClient = "cursor", Tool = "remember", DurationMs = 50, Outcome = "forbidden", CreatedAt = at },
            new McpCall { ActorUserId = QaSeed.Admin, Tool = "search", DurationMs = 5, Outcome = "ok", ResultCount = 1, CreatedAt = at });
        db.ModelUsage.Add(new ModelUsage { Role = ModelRole.Query, ModelId = "test-dash-query", InputTokens = 70, OutputTokens = 5, CreatedAt = at });
        db.AuditEvents.AddRange(
            // Member recalls their own personal memory twice and a company memory once, and repeats one empty recall.
            Recall(QaSeed.Member, "handover", at, QaSeed.MemoryPersonal, QaSeed.MemoryCompanyFact),
            Recall(QaSeed.Member, "my prs", at, QaSeed.MemoryPersonal),
            Recall(QaSeed.Member, "Zebra rota", at.AddMinutes(1)), // the newest spelling is shown
            Recall(QaSeed.Member, "zebra rota ", at),
            new AuditEvent { ActorUserId = QaSeed.Member, Action = "memory.create", ResourceType = "memory", ResourceId = "test-dash", CreatedAt = at });
        db.AgentSessions.Add(new AgentSession { UserId = QaSeed.Member, Client = "claude-code", ExternalSessionId = $"test-dash-{today}", CreatedAt = at });
        var history = new[]
        {
            new DailyAgentStat { Date = yesterday, UserId = QaSeed.Member, Client = "Claude Code", Kind = "mcp:recall", Count = 4 },
            new DailyAgentStat { Date = yesterday, UserId = QaSeed.Admin, Client = "Other", Kind = "mcp:search", Count = 3 },
            new DailyAgentStat { Date = yesterday, UserId = QaSeed.Member, Client = "Claude Code", Kind = "mcp-error:recall", Count = 1 },
            new DailyAgentStat { Date = yesterday.AddDays(-20), UserId = QaSeed.Member, Client = "Claude Code", Kind = "page.revision", Count = 6 },
            new DailyAgentStat { Date = yesterday, Client = "test-dash-embed", Kind = "tokens:embedding", Count = 900 },
        };
        db.DailyAgentStats.AddRange(history);
        await db.SaveChangesAsync(ct);
        try
        {
            var service = new DashboardService(new Factory(athar), new Clock(noon));

            // A member: their own activity only, no admin widgets, and no org view.
            var mine = await service.GetAsync(Person(QaSeed.Member), org: false, ct);
            Assert.False(mine.Admin);
            Assert.Equal((2, 1, 2, (double?)0.5), (mine.Today.Reads, mine.Today.Writes, mine.Today.Retrievals, mine.Today.EmptyRate));
            Assert.Equal(2, mine.ActiveAgents); // Claude Code and the claimed "cursor"
            Assert.Null(mine.Brain.IndexJobs);
            Assert.Null(mine.ModelTokens);
            Assert.Null(mine.JobQueue);
            Assert.Equal(0.5, mine.Brain.RecallHitRate);
            Assert.Equal(
                new[] { new ClientDay(yesterday, "Claude Code", 4), new ClientDay(today, "Claude Code", 2), new ClientDay(today, "cursor", 1) },
                mine.CallsByDay);
            Assert.Equal(new[] { new DayCount(yesterday.AddDays(-20), 6), new DayCount(today, 1) }, mine.Writes.Days);
            Assert.Equal((2, (DayCount?)new DayCount(yesterday.AddDays(-20), 6), (DateOnly?)today), (mine.Writes.ActiveDays, mine.Writes.Peak, mine.Writes.LastWrite));
            Assert.Equal(1, mine.Funnel.Sessions);
            Assert.Equal(new Recalled(QaSeed.MemoryPersonal, "I prefer small PRs", "personal", 2), mine.MostRecalled[0]);
            Assert.Equal(QaSeed.MemoryCompanyFact, mine.MostRecalled[1].Id);
            Assert.Equal(new EmptyRecall("Zebra rota", 2), Assert.Single(mine.Attention.RepeatedEmptyRecalls));
            var remember = Assert.Single(mine.Tools, t => t.Tool == "remember");
            Assert.Equal((1, 50, (double?)1.0), (remember.Calls, remember.P95Ms, remember.ErrorRate));
            Assert.Equal((2, 30, 0.0), mine.Tools.Where(t => t.Tool == "recall").Select(t => (t.Calls, t.P95Ms, t.ErrorRate ?? -1)).Single());
            Assert.Equal(mine.Tokens, mine.OwnTokens);
            Assert.True(mine.Knowledge.Memories > 0);
            Assert.Equal("forbidden", (await Assert.ThrowsAsync<AtharError>(() => service.GetAsync(Person(QaSeed.Member), org: true, ct))).Code);

            // An admin's org view: everyone's calls, admin widgets, and only titles the admin can read.
            var all = await service.GetAsync(Person(QaSeed.Admin), org: true, ct);
            Assert.True(all.Admin);
            Assert.Equal((3, 1, 3), (all.Today.Reads, all.Today.Writes, all.Today.Retrievals));
            Assert.Equal(
                new[] { new ClientDay(yesterday, "Claude Code", 4), new ClientDay(yesterday, "Other", 3),
                    new ClientDay(today, "Claude Code", 2), new ClientDay(today, "cursor", 1), new ClientDay(today, "Other", 1) },
                all.CallsByDay);
            Assert.Equal(new[] { new TokenDay(yesterday, "embedding", 900), new TokenDay(today, "query", 75) },
                all.ModelTokens!.Where(t => t.Date >= yesterday).ToList());
            Assert.NotNull(all.JobQueue);
            Assert.NotNull(all.Brain.IndexJobs);
            Assert.Equal(new Recalled(QaSeed.MemoryPersonal, null, null, 2), all.MostRecalled[0]); // the member's personal memory
            Assert.Equal("Session transcripts are kept 30 days", all.MostRecalled[1].Title);
            Assert.Equal(new EmptyRecall(null, 2), Assert.Single(all.Attention.RepeatedEmptyRecalls)); // nobody else's query text
            Assert.DoesNotContain(all.Tools, t => t.Tool is "recall" or "remember"); // MCP tools are always the caller's own
            Assert.Equal((1, 5), all.Tools.Where(t => t.Tool == "search").Select(t => (t.Calls, t.P95Ms)).Single());

            // The admin's own view leaves the member out.
            var adminMine = await service.GetAsync(Person(QaSeed.Admin), org: false, ct);
            Assert.Equal((1, 0), (adminMine.Today.Reads, adminMine.Today.Writes));
            Assert.Empty(adminMine.MostRecalled);
        }
        finally
        {
            // Rollup rows on future days would hold back the real nightly rollup, which resumes from the newest day.
            var dates = history.Select(h => h.Date).Distinct().ToArray();
            await db.DailyAgentStats.Where(s => dates.Contains(s.Date)).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private static AuditEvent Recall(Guid user, string query, DateTimeOffset at, params Guid[] ids) => new()
    {
        ActorUserId = user, Action = "memory.recall", ResourceType = "memory", ResourceId = "*", CreatedAt = at,
        After = System.Text.Json.JsonSerializer.Serialize(new { query, degraded = false, groups = new[] { new { project_id = (Guid?)null, ids } } }),
    };

    [Fact]
    public void P95_is_the_nearest_rank()
    {
        Assert.Equal(7, DashboardService.P95([7]));
        Assert.Equal(95, DashboardService.P95([.. Enumerable.Range(1, 100).Reverse()]));
        Assert.Contains("recall", DashboardService.ReadTools);
        Assert.DoesNotContain("remember", DashboardService.ReadTools);
    }
}
