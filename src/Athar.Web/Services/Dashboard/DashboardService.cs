using System.Reflection;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Athar.Web.Services.Dashboard;

/// <summary>
/// FR-AG-11: every dashboard widget for one scope. <c>org: false</c> is the caller's own activity (their user ID, so their
/// credentials and OAuth grants); <c>org: true</c> is the whole org and needs Owner or Admin. Today comes from the raw rows
/// (<c>mcp_calls</c>, <c>model_usage</c>, audit events), earlier days from the nightly rollup (<c>daily_agent_stats</c>).
/// Memory and page titles appear only if the caller can read them now; otherwise only counts (as FR-AU-03).
/// Days are UTC days, the same days the rollup uses.
/// Each call opens its own unit of work: a page's circuit shares one DbContext with the layout, which queries at the same time.
/// </summary>
public sealed class DashboardService(IDbContextFactory<AtharDbContext> dbs, TimeProvider clock)
{
    public const int WindowDays = 30, ChartDays = 14, HeatmapDays = 364, TopRecalled = 10, AttentionItems = 5;
    public const string Other = "Other";

    /// <summary>The writes the heatmap counts: memories, session commits, pages and sources.</summary>
    public static readonly string[] WriteKinds = ["memory.create", "memory.revise", "session.commit", "page.create", "page.revision", "source.add"];
    private static readonly string[] IndexKinds = [IndexJobs.EmbedPage, IndexJobs.EmbedVectors];
    private static readonly string[] Retrievals = ["recall", "search"];

    /// <summary>MCP tools marked read-only; every other tool counts as a write.</summary>
    public static readonly IReadOnlySet<string> ReadTools = typeof(DashboardService).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
        .Where(a => a is { ReadOnly: true, Name: not null }).Select(a => a!.Name!).ToHashSet();

    public async Task<DashboardView> GetAsync(Actor actor, bool org, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = actor.OrgId;
        return await GetAsync(db, actor, org, ct);
    }

    private async Task<DashboardView> GetAsync(AtharDbContext db, Actor actor, bool org, CancellationToken ct)
    {
        var admin = OrgRules.IsAdmin(await OrgAdminService.ActorRoleAsync(db, actor.UserId, ct));
        if (org && !admin) throw new AtharError("forbidden", "Only org owners and admins see the whole org.");
        Guid? user = org ? null : actor.UserId;
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var dayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var since = now.AddDays(-WindowDays);
        var who = await db.MemoryActorAsync(actor, ct);

        var calls = db.McpCalls.AsNoTracking().Where(c => user == null || c.ActorUserId == user);
        var audit = db.AuditEvents.AsNoTracking().Where(e => user == null || e.ActorUserId == user);
        var stats = db.DailyAgentStats.AsNoTracking().Where(s => user == null || s.UserId == user);
        var proposals = db.MemoryProposals.AsNoTracking().Where(p => p.Kind != ProposalKind.Skill && (user == null || p.ProposedBy == user));
        var conflicts = db.MemoryConflicts.AsNoTracking().Where(c => c.Status == ConflictStatus.Open && (user == null || c.OpenedBy == user));

        // (a) Knowledge stored, always within the caller's own read access.
        var projects = who.ProjectIds.ToArray();
        var knowledge = new KnowledgeCounts(
            await db.PagesFor(actor.Caller).CountAsync(p => p.DeletedAt == null, ct),
            await db.Memories.Where(MemoryRules.Visible(who)).CountAsync(m => m.Status == MemoryStatus.Active, ct),
            await db.Skills.Where(SkillService.Visible(who)).CountAsync(s => s.Status == SkillStatus.Active, ct),
            await db.KnowledgeSources.CountAsync(s => projects.Contains(s.ProjectId), ct));

        // (b), (c) Today's agent calls and retrievals.
        var todayTools = await calls.Where(c => c.CreatedAt >= dayStart).GroupBy(c => c.Tool)
            .Select(g => new { Tool = g.Key, Count = g.Count(), Empty = g.Count(c => c.ResultCount == 0) }).ToListAsync(ct);
        var reads = todayTools.Where(t => ReadTools.Contains(t.Tool)).Sum(t => t.Count);
        var retrieved = todayTools.Where(t => Retrievals.Contains(t.Tool)).ToList();
        var today_ = new TodayCounts(reads, todayTools.Sum(t => t.Count) - reads, retrieved.Sum(t => t.Count), Rate(retrieved.Sum(t => t.Empty), retrieved.Sum(t => t.Count)));

        // (d) Agents and tokens.
        var agents = await calls.Where(c => c.CreatedAt >= now.AddDays(-1))
            .Select(c => new { c.CredentialId, c.OauthClientId, c.ClaimedClient }).Distinct().CountAsync(ct);
        var tokens = await TokensAsync(db, user, now, ct);

        // (e) The brain over the window.
        var promoted = await proposals.CountAsync(p => p.Status == ProposalStatus.Approved && p.DecidedAt >= since, ct);
        var newMemories = await db.Memories.AsNoTracking().CountAsync(m => m.CreatedAt >= since && (user == null || m.CreatedByUser == user), ct);
        var open = proposals.Where(p => p.Status == ProposalStatus.Pending || p.Status == ProposalStatus.NeedsRereview);
        var waiting = await open.CountAsync(ct);
        var oldest = await open.MinAsync(p => (DateTimeOffset?)p.CreatedAt, ct);
        var recallCalls = calls.Where(c => c.Tool == "recall" && c.CreatedAt >= since);
        var recalls = await recallCalls.CountAsync(ct);
        var hits = await recallCalls.CountAsync(c => c.ResultCount > 0, ct);
        var openConflicts = await conflicts.CountAsync(ct);
        var brain = new BrainCounts(promoted, Rate(promoted, newMemories),
            await proposals.CountAsync(p => p.Status == ProposalStatus.Rejected && p.DecidedAt >= since, ct), openConflicts,
            waiting, now - oldest, Rate(hits, recalls),
            admin ? await JobCountsAsync(db.Jobs.Where(j => IndexKinds.Contains(j.Kind)), ct) : null);

        // (f) Agent calls per day by client: the rollup, then today from the raw rows.
        var firstDay = today.AddDays(1 - ChartDays);
        var history = await stats.Where(s => s.Date >= firstDay && s.Date < today && s.Kind.StartsWith("mcp:"))
            .GroupBy(s => new { s.Date, s.Client }).Select(g => new ClientDay(g.Key.Date, g.Key.Client, g.Sum(s => s.Count))).ToListAsync(ct);
        var raw = await calls.Where(c => c.CreatedAt >= dayStart).GroupBy(c => new { c.OauthClientId, c.ClaimedClient })
            .Select(g => new { g.Key.OauthClientId, g.Key.ClaimedClient, Count = g.LongCount() }).ToListAsync(ct);
        var names = await ClientNamesAsync(db, raw.Select(r => r.OauthClientId), ct);
        var callsByDay = history.Concat(raw.GroupBy(r => Client(names, r.OauthClientId, r.ClaimedClient))
            .Select(g => new ClientDay(today, g.Key, g.Sum(r => r.Count)))).OrderBy(d => d.Date).ThenBy(d => d.Client).ToList();

        // (g) Write heatmap.
        var heatStart = today.AddDays(1 - HeatmapDays);
        var writeDays = (await stats.Where(s => s.Date >= heatStart && s.Date < today && WriteKinds.Contains(s.Kind))
            .GroupBy(s => s.Date).Select(g => new DayCount(g.Key, g.Sum(s => s.Count))).ToListAsync(ct)).ToList();
        var writesToday = await audit.CountAsync(e => e.CreatedAt >= dayStart && WriteKinds.Contains(e.Action), ct);
        if (writesToday > 0) writeDays.Add(new(today, writesToday));
        writeDays = [.. writeDays.Where(d => d.Count > 0).OrderBy(d => d.Date)];
        var heatmap = new Heatmap(writeDays, writeDays.Count, writeDays.OrderByDescending(d => d.Count).ThenByDescending(d => d.Date).FirstOrDefault(),
            writeDays.LastOrDefault()?.Date);

        // (h) Capture funnel over the window.
        var candidates = db.CaptureCandidates.AsNoTracking().Where(c => c.CreatedAt >= since && (user == null || c.OwnerUserId == user));
        var funnel = new Funnel(
            await db.AgentSessions.CountAsync(s => s.CreatedAt >= since && (user == null || s.UserId == user), ct),
            await candidates.CountAsync(c => c.MemoryId != null, ct),
            await candidates.CountAsync(c => c.ProposalId != null, ct),
            await candidates.Join(db.MemoryProposals, c => c.ProposalId, p => (Guid?)p.Id, (c, p) => p)
                .CountAsync(p => p.Status == ProposalStatus.Approved || p.Status == ProposalStatus.Rejected, ct),
            await candidates.Join(db.MemoryProposals, c => c.ProposalId, p => (Guid?)p.Id, (c, p) => p).CountAsync(p => p.Status == ProposalStatus.Approved, ct));

        // (i), (j) Recall results: the most recalled memories, memories nobody recalls, and repeated empty recalls.
        var recallEvents = await RecallsAsync(audit.Where(e => e.CreatedAt >= since), ct);
        var recalledByAnyone = (await RecallsAsync(db.AuditEvents.AsNoTracking(), ct)).SelectMany(r => r.Ids).ToHashSet();
        var counted = recallEvents.SelectMany(r => r.Ids).GroupBy(id => id).Select(g => (Id: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Id).Take(TopRecalled).ToList();
        var titles = await TitlesAsync(db, who, counted.Select(x => x.Id), ct);
        var top = counted.Select(x => titles.TryGetValue(x.Id, out var t) ? new Recalled(x.Id, t.Title, t.Scope, x.Count) : new Recalled(x.Id, null, null, x.Count)).ToList();

        var stale = db.Memories.AsNoTracking().Where(m => m.Status == MemoryStatus.Active && m.CreatedAt < since && (user == null || m.CreatedByUser == user))
            .Select(m => m.Id);
        var staleIds = (await stale.ToListAsync(ct)).Where(id => !recalledByAnyone.Contains(id)).ToList();
        var staleTitles = await TitlesAsync(db, who, staleIds, ct);
        var emptyRepeats = recallEvents.Where(r => r.Ids.Count == 0 && r.Query is { Length: > 0 })
            .GroupBy(r => r.Query!.Trim().ToLowerInvariant()).Where(g => g.Count() > 1)
            // Other people's queries stay hidden, as in the audit trail (FR-AU-03): the org view shows counts only.
            .Select(g => new EmptyRecall(user is null ? null : g.First().Query!.Trim(), g.Count())).OrderByDescending(e => e.Count).Take(AttentionItems).ToList();
        var attention = new Attention(staleIds.Count,
            [.. staleTitles.OrderBy(t => t.Value.Title).Take(AttentionItems).Select(t => new MemoryRef(t.Key, t.Value.Title, t.Value.Scope))],
            openConflicts, emptyRepeats);

        // (k) MCP tools: always the caller's own calls.
        var own = await db.McpCalls.AsNoTracking().Where(c => c.ActorUserId == actor.UserId && c.CreatedAt >= since)
            .Select(c => new { c.Tool, c.DurationMs, c.Outcome }).ToListAsync(ct);
        var tools = own.GroupBy(c => c.Tool).Select(g => new ToolStat(g.Key, g.Count(), P95([.. g.Select(c => c.DurationMs)]), Rate(g.Count(c => c.Outcome != "ok"), g.Count())))
            .OrderByDescending(t => t.Calls).ThenBy(t => t.Tool).ToList();

        // (l), (m) Admin-only: model tokens per day by role and the job queue. Token health is the caller's own.
        List<TokenDay>? modelTokens = null;
        JobCounts? queue = null;
        if (admin)
        {
            var tokenHistory = await db.DailyAgentStats.AsNoTracking().Where(s => s.Date >= firstDay && s.Date < today && s.Kind.StartsWith("tokens:"))
                .GroupBy(s => new { s.Date, s.Kind }).Select(g => new { g.Key.Date, g.Key.Kind, Count = g.Sum(s => s.Count) }).ToListAsync(ct);
            var tokenToday = await db.ModelUsage.AsNoTracking().Where(u => u.CreatedAt >= dayStart).GroupBy(u => u.Role)
                .Select(g => new { Role = g.Key, Count = g.Sum(u => (long)u.InputTokens + u.OutputTokens) }).ToListAsync(ct);
            modelTokens = [.. tokenHistory.Select(t => new TokenDay(t.Date, t.Kind["tokens:".Length..], t.Count))
                .Concat(tokenToday.Select(t => new TokenDay(today, t.Role.ToString().ToLowerInvariant(), t.Count)))
                .OrderBy(t => t.Date).ThenBy(t => t.Role)];
            queue = await JobCountsAsync(db.Jobs, ct);
        }

        return new(org, admin, now, knowledge, today_, agents, tokens, brain, callsByDay, heatmap, funnel, top, attention, tools,
            modelTokens, queue, user is null ? await TokensAsync(db, actor.UserId, now, ct) : tokens);
    }

    private static async Task<TokenHealth> TokensAsync(AtharDbContext db, Guid? user, DateTimeOffset now, CancellationToken ct)
    {
        var live = db.AccessTokens.AsNoTracking().Where(t => t.RevokedAt == null && (user == null || t.UserId == user));
        var unusedSince = now.AddDays(-30);
        return new(
            await live.CountAsync(t => t.ExpiresAt == null || t.ExpiresAt > now, ct),
            await live.CountAsync(t => t.ExpiresAt > now && t.ExpiresAt <= now.AddDays(7), ct),
            await live.CountAsync(t => (t.ExpiresAt == null || t.ExpiresAt > now) && (t.LastUsedAt ?? t.CreatedAt) < unusedSince, ct),
            await live.CountAsync(t => t.ExpiresAt <= now, ct));
    }

    private static async Task<JobCounts> JobCountsAsync(IQueryable<Job> jobs, CancellationToken ct)
    {
        var counts = await jobs.AsNoTracking().Where(j => j.Status != JobStatus.Done).GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(JobStatus s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;
        var lastError = await jobs.AsNoTracking().Where(j => j.LastError != null && j.Status != JobStatus.Done)
            .OrderByDescending(j => j.UpdatedAt).Select(j => new { j.Kind, j.LastError, j.UpdatedAt }).FirstOrDefaultAsync(ct);
        return new(Of(JobStatus.Ready), Of(JobStatus.Running), Of(JobStatus.Dead),
            lastError is null ? null : new JobError(lastError.Kind, lastError.LastError!.Length <= 300 ? lastError.LastError : lastError.LastError[..300], lastError.UpdatedAt));
    }

    private static async Task<Dictionary<Guid, string>> ClientNamesAsync(AtharDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0 ? [] : await db.OauthApplications.AsNoTracking().Where(a => wanted.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.DisplayName ?? a.ClientId ?? Other, ct);
    }

    /// <summary>The rollup's client rule: the OAuth client's name, else the claimed label, else "Other".</summary>
    private static string Client(Dictionary<Guid, string> names, Guid? oauth, string? claimed) =>
        (oauth is { } id ? names.GetValueOrDefault(id) : null) ?? claimed ?? Other;

    private sealed record Recall(string? Query, List<Guid> Ids);

    // ponytail: reads recall payloads in .NET, capped at the newest 20,000; a jsonb aggregate in SQL if recall volume outgrows it.
    private static async Task<List<Recall>> RecallsAsync(IQueryable<AuditEvent> events, CancellationToken ct)
    {
        var payloads = await events.Where(e => e.Action == "memory.recall" && e.After != null)
            .OrderByDescending(e => e.CreatedAt).Take(20_000).Select(e => e.After!).ToListAsync(ct);
        var result = new List<Recall>(payloads.Count);
        foreach (var json in payloads)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var ids = new List<Guid>();
                if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                        if (g.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array)
                            foreach (var id in list.EnumerateArray())
                                if (id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var guid)) ids.Add(guid);
                result.Add(new(root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null, ids));
            }
            catch (JsonException) { }
        }
        return result;
    }

    /// <summary>Titles of the memories the caller can read now; the others stay out (counts only).</summary>
    private static async Task<Dictionary<Guid, (string Title, string Scope)>> TitlesAsync(AtharDbContext db, MemoryActor who, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Memories.AsNoTracking().Where(MemoryRules.Visible(who)).Where(m => wanted.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => (m.Title, m.Scope.ToString().ToLowerInvariant()), ct);
    }

    private static double? Rate(int part, int whole) => whole == 0 ? null : (double)part / whole;

    /// <summary>Nearest-rank 95th percentile.</summary>
    public static int P95(int[] values)
    {
        Array.Sort(values);
        return values[Math.Max(0, (int)Math.Ceiling(values.Length * 0.95) - 1)];
    }
}

public sealed record DashboardView(bool Org, bool Admin, DateTimeOffset At, KnowledgeCounts Knowledge, TodayCounts Today, int ActiveAgents,
    TokenHealth Tokens, BrainCounts Brain, IReadOnlyList<ClientDay> CallsByDay, Heatmap Writes, Funnel Funnel,
    IReadOnlyList<Recalled> MostRecalled, Attention Attention, IReadOnlyList<ToolStat> Tools, IReadOnlyList<TokenDay>? ModelTokens,
    JobCounts? JobQueue, TokenHealth OwnTokens);

public sealed record KnowledgeCounts(int Pages, int Memories, int Skills, int Sources);
/// <summary>EmptyRate is null when there were no retrievals.</summary>
public sealed record TodayCounts(int Reads, int Writes, int Retrievals, double? EmptyRate);
public sealed record TokenHealth(int Active, int ExpiringIn7Days, int UnusedFor30Days, int Expired);
/// <summary>IndexJobs is null for non-admins.</summary>
public sealed record BrainCounts(int Promoted, double? PromotedShare, int Rejected, int OpenConflicts, int WaitingForReview,
    TimeSpan? OldestWaiting, double? RecallHitRate, JobCounts? IndexJobs);
public sealed record JobCounts(int Ready, int Running, int Dead, JobError? LastError);
public sealed record JobError(string Kind, string Message, DateTimeOffset At);
public sealed record ClientDay(DateOnly Date, string Client, long Count);
public sealed record DayCount(DateOnly Date, long Count);
public sealed record Heatmap(IReadOnlyList<DayCount> Days, int ActiveDays, DayCount? Peak, DateOnly? LastWrite);
public sealed record Funnel(int Sessions, int Memories, int Proposals, int Reviewed, int Promoted);
/// <summary>Title and scope are null when the caller can't read the memory now.</summary>
public sealed record Recalled(Guid Id, string? Title, string? Scope, int Count);
public sealed record MemoryRef(Guid Id, string Title, string Scope);
/// <summary>Query is null in the org view.</summary>
public sealed record EmptyRecall(string? Query, int Count);
public sealed record Attention(int NeverRecalled, IReadOnlyList<MemoryRef> NeverRecalledReadable, int OpenConflicts, IReadOnlyList<EmptyRecall> RepeatedEmptyRecalls);
public sealed record ToolStat(string Tool, int Calls, int P95Ms, double? ErrorRate);
public sealed record TokenDay(DateOnly Date, string Role, long Tokens);
