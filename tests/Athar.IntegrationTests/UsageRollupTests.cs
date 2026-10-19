using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Worker.Jobs.Retention;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T177: the nightly usage rollup and usage retention (FR-AG-12c).</summary>
[Collection(AtharCollection.Name)]
public class UsageRollupTests(AtharApp athar)
{
    private static readonly Guid Org = QaSeed.Org;
    private static readonly Guid ClaudeCodeClient = Guid.Parse("00000000-0000-7000-8000-0000000c0de1"); // seeded as "Claude Code"

    [Fact]
    public async Task AT_68_rollup_counts_a_day_per_user_client_and_kind_reruns_in_place_and_retention_keeps_90_and_400_days()
    {
        var ct = AtharApp.Timeout();
        var now = DateTimeOffset.UtcNow;
        // A day no other test writes to, inside raw retention.
        var day = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-60);
        var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        await using var db = athar.Db(Org);

        db.AuditEvents.AddRange(
            new AuditEvent { OrgId = Org, ActorUserId = QaSeed.Member, OauthClientId = ClaudeCodeClient, Action = "test.rollup_write", ResourceType = "page", ResourceId = "a", CreatedAt = at },
            new AuditEvent { OrgId = Org, ActorUserId = QaSeed.Member, OauthClientId = ClaudeCodeClient, Action = "test.rollup_write", ResourceType = "page", ResourceId = "b", CreatedAt = at },
            new AuditEvent { OrgId = Org, ActorUserId = QaSeed.Admin, Action = "test.rollup_write", ResourceType = "page", ResourceId = "c", CreatedAt = at },
            // The next day: not in this day's rollup.
            new AuditEvent { OrgId = Org, ActorUserId = QaSeed.Member, OauthClientId = ClaudeCodeClient, Action = "test.rollup_write", ResourceType = "page", ResourceId = "d", CreatedAt = at.AddDays(1) });
        db.McpCalls.AddRange(
            new McpCall { OrgId = Org, ActorUserId = QaSeed.Member, ClaimedClient = "cursor", Tool = "test_rollup_recall", DurationMs = 12, Outcome = "ok", CreatedAt = at },
            new McpCall { OrgId = Org, ActorUserId = QaSeed.Member, ClaimedClient = "cursor", Tool = "test_rollup_recall", DurationMs = 30, Outcome = "forbidden", CreatedAt = at });
        db.ModelUsage.AddRange(
            new ModelUsage { OrgId = Org, Role = ModelRole.Embedding, ModelId = "test-rollup-embed", InputTokens = 100, OutputTokens = 0, CreatedAt = at },
            new ModelUsage { OrgId = Org, Role = ModelRole.Embedding, ModelId = "test-rollup-embed", InputTokens = 40, OutputTokens = 2, CreatedAt = at });
        await db.SaveChangesAsync(ct);

        async Task<Dictionary<(Guid?, string, string), long>> Stats() =>
            (await db.DailyAgentStats.AsNoTracking()
                .Where(s => s.Date == day && (s.Kind.Contains("test_rollup") || s.Kind == "test.rollup_write" || s.Client == "test-rollup-embed"))
                .ToListAsync(ct))
            .ToDictionary(s => (s.UserId, s.Client, s.Kind), s => s.Count);

        await UsageRollup.RollUpDayAsync(db, day, ct);
        var first = await Stats();
        Assert.Equal(2L, first[(QaSeed.Member, "Claude Code", "test.rollup_write")]); // OAuth client name
        Assert.Equal(1L, first[(QaSeed.Admin, UsageRollup.OtherClient, "test.rollup_write")]); // no client at all
        Assert.Equal(2L, first[(QaSeed.Member, "cursor", "mcp:test_rollup_recall")]); // claimed label
        Assert.Equal(1L, first[(QaSeed.Member, "cursor", "mcp-error:test_rollup_recall")]);
        Assert.Equal(142L, first[(null, "test-rollup-embed", "tokens:embedding")]); // input plus output tokens
        Assert.Equal(5, first.Count);

        // Re-runnable: a late row is counted and nothing doubles.
        db.AuditEvents.Add(new AuditEvent { OrgId = Org, ActorUserId = QaSeed.Admin, Action = "test.rollup_write", ResourceType = "page", ResourceId = "e", CreatedAt = at });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await UsageRollup.RollUpDayAsync(db, day, ct);
        var second = await Stats();
        Assert.Equal(2L, second[(QaSeed.Admin, UsageRollup.OtherClient, "test.rollup_write")]);
        Assert.Equal(2L, second[(QaSeed.Member, "Claude Code", "test.rollup_write")]);
        Assert.Equal(5, second.Count);

        // Retention: raw rows past 90 days and rollups past 400 days go; newer ones stay.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        db.McpCalls.AddRange(
            new McpCall { OrgId = Org, Tool = "test_retention_old", Outcome = "ok", CreatedAt = now.AddDays(-91) },
            new McpCall { OrgId = QaSeed.OtherOrg, Tool = "test_retention_old", Outcome = "ok", CreatedAt = now.AddDays(-200) },
            new McpCall { OrgId = Org, Tool = "test_retention_recent", Outcome = "ok", CreatedAt = now.AddDays(-89) });
        db.ModelUsage.AddRange(
            new ModelUsage { OrgId = Org, ModelId = "test-retention-old", CreatedAt = now.AddDays(-91) },
            new ModelUsage { OrgId = Org, ModelId = "test-retention-recent", CreatedAt = now.AddDays(-89) });
        db.DailyAgentStats.AddRange(
            new DailyAgentStat { OrgId = Org, Date = today.AddDays(-401), Client = "test", Kind = "test.retention_old", Count = 1 },
            new DailyAgentStat { OrgId = Org, Date = today.AddDays(-399), Client = "test", Kind = "test.retention_recent", Count = 1 });
        await db.SaveChangesAsync(ct);

        await UsageRetention.PurgeAsync(db, now, ct);
        Assert.False(await db.McpCalls.IgnoreQueryFilters().AnyAsync(c => c.Tool == "test_retention_old", ct));
        Assert.True(await db.McpCalls.AnyAsync(c => c.Tool == "test_retention_recent", ct));
        Assert.False(await db.ModelUsage.AnyAsync(u => u.ModelId == "test-retention-old", ct));
        Assert.True(await db.ModelUsage.AnyAsync(u => u.ModelId == "test-retention-recent", ct));
        Assert.False(await db.DailyAgentStats.AnyAsync(s => s.Kind == "test.retention_old", ct));
        Assert.True(await db.DailyAgentStats.AnyAsync(s => s.Kind == "test.retention_recent", ct));
        Assert.Equal(5, (await Stats()).Count); // the 60-day-old rollup stays
    }
}
