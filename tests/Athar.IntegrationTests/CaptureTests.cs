using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

/// <summary>
/// T061 extraction into the capture inbox (FR-MM-06..07, AT-27, AT-28). Sessions are started and committed over REST like the
/// hooks do; the queued <c>extract-session</c> job then runs here with a fixed extractor, so the LLM's wording doesn't matter.
/// </summary>
[Collection(AtharCollection.Name)]
public class CaptureTests(AtharApp athar)
{
    private sealed class Fixed(params ExtractedCandidate[] candidates) : ICandidateExtractor
    {
        public Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtractedCandidate>>(candidates);
    }

    [Fact]
    public async Task AT_27_candidates_go_to_the_owners_inbox_with_checked_scopes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        await using var byHand = await ReviewByHandAsync(QaSeed.Member, ct); // the inbox path; auto-accept is AT-61
        using var http = athar.Web();
        var (session, job) = await CommitAsync(http, QaSeed.MemberPat, "https://github.com/abushanab-net/athar", ct);
        await RunAsync(job, new Fixed(
            new("Short PR titles", "I keep PR titles under 60 characters.", "preference", "personal", "keep PR titles short"),
            new("Pilot uses trunk-based branches", "The pilot merges small branches into develop daily.", "convention", "project"),
            new("Secrets live in the vault", "No secrets in the repo; use the secret store.", "policy", "company")), ct);

        var inbox = (await Mcp.Get(http, "/api/v1/inbox", QaSeed.MemberPat, ct)).Body.EnumerateArray().Where(c => c.Str("title") is "Short PR titles" or "Pilot uses trunk-based branches" or "Secrets live in the vault").ToList();
        Assert.Equal(3, inbox.Count);
        Assert.Equal("project", inbox.Single(c => c.Str("title") == "Pilot uses trunk-based branches").Str("suggested_scope")); // the session's repo → the pilot
        Assert.DoesNotContain((await Mcp.Get(http, "/api/v1/inbox", QaSeed.KnowledgeOwnerPat, ct)).Body.EnumerateArray(), c => c.Str("title") == "Short PR titles");

        // Accepting re-checks eligibility: company always proposes, project publishes for a member; discard is final.
        string Id(string title) => inbox.Single(c => c.Str("title") == title).Str("id");
        Assert.Equal("proposed", (await Mcp.Post(http, $"/api/v1/inbox/{Id("Secrets live in the vault")}/accept", QaSeed.MemberPat, null, ct)).Body.Str("result"));
        var published = await Mcp.Post(http, $"/api/v1/inbox/{Id("Pilot uses trunk-based branches")}/accept", QaSeed.MemberPat, new { title = "Pilot: trunk-based branches" }, ct);
        Assert.Equal("published", published.Body.Str("result"));
        Assert.Equal("discarded", (await Mcp.Post(http, $"/api/v1/inbox/{Id("Short PR titles")}/discard", QaSeed.MemberPat, null, ct)).Body.Str("status"));
        Assert.Equal(HttpStatusCode.Conflict, (await Mcp.Post(http, $"/api/v1/inbox/{Id("Short PR titles")}/accept", QaSeed.MemberPat, null, ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Mcp.Post(http, $"/api/v1/inbox/{Id("Secrets live in the vault")}/discard", QaSeed.KnowledgeOwnerPat, null, ct)).Status);

        await using (var db = athar.Db(QaSeed.Org))
        {
            var memory = await db.Memories.SingleAsync(m => m.Id == Guid.Parse(published.Body.Str("memory_id")), ct);
            Assert.Equal((Scope.Project, QaSeed.PilotProject, "Pilot: trunk-based branches"), (memory.Scope, memory.OwnerProjectId!.Value, memory.Title));
            var editedId = Guid.Parse(Id("Pilot uses trunk-based branches"));
            Assert.Equal(CandidateStatus.Edited, (await db.CaptureCandidates.SingleAsync(c => c.Id == editedId, ct)).Status);
            Assert.Equal(2, (await db.AgentSessions.SingleAsync(s => s.Id == session, ct)).ExtractedUpto);
        }

        // A scope the owner can neither publish nor propose falls back to personal: no project resolves for this repo.
        var (_, unlinked) = await CommitAsync(http, QaSeed.MemberPat, "https://github.com/someone/else", ct);
        await RunAsync(unlinked, new Fixed(new ExtractedCandidate("Else uses tabs", "Tabs in the else repo.", "convention", "project")), ct);
        Assert.Equal("personal", (await Mcp.Get(http, "/api/v1/inbox", QaSeed.MemberPat, ct)).Body.EnumerateArray().Single(c => c.Str("title") == "Else uses tabs").Str("suggested_scope"));

        // The worker re-checks the owner: with the session's credential revoked, nothing is extracted.
        var token = await Mcp.Post(http, "/api/v1/tokens", QaSeed.MemberPat, new { name = "capture probe", scopes = new[] { "read", "memory:write" }, expires_in_days = 0 }, ct);
        var (revokedSession, revokedJob) = await CommitAsync(http, token.Body.Str("token"), null, ct);
        using (var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/tokens/{token.Body.Str("id")}"))
        {
            revoke.Headers.Add("Authorization", $"Bearer {QaSeed.MemberPat}");
            Assert.True((await http.SendAsync(revoke, ct)).IsSuccessStatusCode);
        }
        await RunAsync(revokedJob, new Fixed(new ExtractedCandidate("Should not appear", "Never extracted.", "fact", "personal")), ct);
        await using (var db = athar.Db(QaSeed.Org))
        {
            Assert.False(await db.CaptureCandidates.AnyAsync(c => c.SessionId == revokedSession, ct));
            Assert.Equal(-1, (await db.AgentSessions.SingleAsync(s => s.Id == revokedSession, ct)).ExtractedUpto);
        }
    }

    [Fact]
    public async Task AT_28_similar_items_stay_separate_and_exact_matches_become_evidence()
    {
        // Similarity needs vectors: the first run may still be pulling bge-m3.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        await using var byHand = await ReviewByHandAsync(QaSeed.Member, ct);
        using var http = athar.Web();
        await using var member = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.MemberPat, ct);
        await using var owner = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.KnowledgeOwnerPat, ct);
        var mine = await RememberAsync(member, "Builds use the dotnet 10 SDK.", ct);
        await RememberAsync(owner, "Deploys go out on Mondays.", ct); // someone else's personal memory

        var (session, job) = await CommitAsync(http, QaSeed.MemberPat, null, ct);
        await RunAsync(job, new Fixed(
            new("SDK", "builds use the   DOTNET 10 sdk.", "fact", "personal"),          // exact (normalized) → evidence on mine
            new("SDK again", "Builds use the .NET 10 SDK.", "fact", "personal"),        // similar → shown, not merged
            new("SDK never", "Builds never use the dotnet 10 SDK.", "fact", "personal"), // negation → separate
            new("SDK nine", "Builds use the dotnet 9 SDK.", "fact", "personal"),        // changed number → separate
            new("Mondays", "Deploys go out on Mondays.", "fact", "personal"),           // a different owner's → separate
            new("Retention", "Transcripts are deleted 30 days after receipt.", "fact", "company")), ct); // exact company → evidence proposal

        await using var db = athar.Db(QaSeed.Org);
        var candidates = await db.CaptureCandidates.Where(c => c.SessionId == session).ToListAsync(ct);
        Assert.Equal(["Mondays", "SDK again", "SDK never", "SDK nine"], candidates.Select(c => c.Title).Order());
        Assert.Contains(mine, candidates.Single(c => c.Title == "SDK again").SimilarIds);
        var original = await db.Memories.SingleAsync(m => m.Id == mine, ct);
        Assert.Equal((1, "Builds use the dotnet 10 SDK."), (original.Revision, original.BodyMd)); // never merged
        Assert.True(await db.MemoryLinks.AnyAsync(l => l.MemoryId == mine && l.TargetKind == "session" && l.TargetId == session.ToString(), ct));

        var evidence = (await db.MemoryProposals.Where(p => p.TargetMemoryId == QaSeed.MemoryCompanyFact && p.Kind == ProposalKind.Evidence).ToListAsync(ct))
            .Single(p => p.Evidence!.Contains(session.ToString()));
        Assert.Equal((QaSeed.Member, ProposalStatus.Pending), (evidence.ProposedBy, evidence.Status));
        Assert.Equal(1, (await db.Memories.SingleAsync(m => m.Id == QaSeed.MemoryCompanyFact, ct)).Revision); // a proposal, not a change
    }

    [Fact]
    public async Task AT_61_auto_accept_publishes_personal_by_default_shared_only_when_switched_on_never_company_and_undo_archives()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        using var http = athar.Web();
        const string repo = "https://github.com/abushanab-net/athar";
        ExtractedCandidate[] Found(string tag) =>
        [
            new($"Mine {tag}", $"I like terse commit messages ({tag}).", "preference", "personal"),
            new($"Pilot {tag}", $"The pilot tags releases from develop ({tag}).", "convention", "project"),
            new($"Company {tag}", $"Every repo has a CODEOWNERS file ({tag}).", "policy", "company"),
        ];
        try
        {
            // Defaults (no saved settings): personal is published at once; project waits in the inbox; company always does.
            var (session, job) = await CommitAsync(http, QaSeed.MemberPat, repo, ct);
            await RunAsync(job, new Fixed(Found("a")), ct);
            await using (var db = athar.Db(QaSeed.Org))
            {
                var c = await db.CaptureCandidates.Where(x => x.SessionId == session).ToDictionaryAsync(x => x.Title, ct);
                Assert.Equal((CandidateStatus.AutoAccepted, CandidateStatus.New, CandidateStatus.New), (c["Mine a"].Status, c["Pilot a"].Status, c["Company a"].Status));
                Assert.Null(c["Company a"].MemoryId);
                var m = await db.Memories.SingleAsync(x => x.Id == c["Mine a"].MemoryId, ct);
                Assert.Equal((Scope.Personal, QaSeed.Member, MemoryStatus.Active, "capture"), (m.Scope, m.OwnerUserId!.Value, m.Status, m.Origin));
                Assert.True(await db.MemoryLinks.AnyAsync(l => l.MemoryId == m.Id && l.TargetKind == "session" && l.TargetId == session.ToString(), ct));
                Assert.True(await db.Chunks.AnyAsync(x => x.OwnerId == m.Id, ct)); // searchable like any memory
                Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "capture.auto_accept" && e.ResourceId == c["Mine a"].Id.ToString(), ct));
            }
            var inbox = (await Mcp.Get(http, "/api/v1/inbox", QaSeed.MemberPat, ct)).Body.EnumerateArray().Select(x => x.Str("title")).ToList();
            Assert.DoesNotContain("Mine a", inbox);
            Assert.Contains("Pilot a", inbox);

            // The shared switch on: a project candidate the owner can publish is published too. Company still never.
            await SetAsync(QaSeed.Member, personal: true, shared: true, ct);
            (session, job) = await CommitAsync(http, QaSeed.MemberPat, repo, ct);
            await RunAsync(job, new Fixed(Found("b")), ct);
            await using (var db = athar.Db(QaSeed.Org))
            {
                var c = await db.CaptureCandidates.Where(x => x.SessionId == session).ToDictionaryAsync(x => x.Title, ct);
                Assert.Equal((CandidateStatus.AutoAccepted, CandidateStatus.AutoAccepted, CandidateStatus.New), (c["Mine b"].Status, c["Pilot b"].Status, c["Company b"].Status));
                var m = await db.Memories.SingleAsync(x => x.Id == c["Pilot b"].MemoryId, ct);
                Assert.Equal((Scope.Project, QaSeed.PilotProject), (m.Scope, m.OwnerProjectId!.Value));
            }

            // Personal switched off: back to the inbox.
            await SetAsync(QaSeed.Member, personal: false, shared: false, ct);
            (session, job) = await CommitAsync(http, QaSeed.MemberPat, repo, ct);
            await RunAsync(job, new Fixed(Found("c")), ct);
            await using (var db = athar.Db(QaSeed.Org))
                Assert.All(await db.CaptureCandidates.Where(x => x.SessionId == session).ToListAsync(ct), x => Assert.Equal(CandidateStatus.New, x.Status));

            // Undo archives the memory and discards the candidate, once; only the owner can.
            Guid undo, memory;
            await using (var db = athar.Db(QaSeed.Org))
            {
                var c = await db.CaptureCandidates.SingleAsync(x => x.Title == "Mine a", ct);
                (undo, memory) = (c.Id, c.MemoryId!.Value);
            }
            Assert.Equal(HttpStatusCode.NotFound, (await Mcp.Post(http, $"/api/v1/inbox/{undo}/undo", QaSeed.KnowledgeOwnerPat, null, ct)).Status);
            Assert.Equal("undone", (await Mcp.Post(http, $"/api/v1/inbox/{undo}/undo", QaSeed.MemberPat, null, ct)).Body.Str("status"));
            Assert.Equal(HttpStatusCode.BadRequest, (await Mcp.Post(http, $"/api/v1/inbox/{undo}/undo", QaSeed.MemberPat, null, ct)).Status);
            await using (var db = athar.Db(QaSeed.Org))
            {
                Assert.Equal(MemoryStatus.Archived, (await db.Memories.SingleAsync(x => x.Id == memory, ct)).Status);
                Assert.Equal(CandidateStatus.Discarded, (await db.CaptureCandidates.SingleAsync(x => x.Id == undo, ct)).Status);
                Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "capture.undo" && e.ResourceId == undo.ToString(), ct));
            }
        }
        finally { await ClearSettingsAsync(QaSeed.Member, ct); }
    }

    /// <summary>Personal auto-accept off for <paramref name="user"/> while the returned handle lives (the inbox path).</summary>
    private async Task<IAsyncDisposable> ReviewByHandAsync(Guid user, CancellationToken ct)
    {
        await SetAsync(user, personal: false, shared: false, ct);
        return new Cleanup(() => ClearSettingsAsync(user, CancellationToken.None));
    }

    private async Task SetAsync(Guid user, bool personal, bool shared, CancellationToken ct)
    {
        await ClearSettingsAsync(user, ct);
        await using var db = athar.Db(QaSeed.Org);
        db.CaptureSettings.Add(new CaptureSettings { OrgId = QaSeed.Org, UserId = user, AutoAcceptPersonal = personal, AutoAcceptShared = shared });
        await db.SaveChangesAsync(ct);
    }

    private async Task ClearSettingsAsync(Guid user, CancellationToken ct)
    {
        await using var db = athar.Db(QaSeed.Org);
        await db.CaptureSettings.Where(s => s.UserId == user).ExecuteDeleteAsync(ct);
    }

    private sealed class Cleanup(Func<Task> run) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await run();
    }

    /// <summary>Starts a session and commits three records, as the capture hook does; returns the queued extraction job.</summary>
    private async Task<(Guid Session, Job Job)> CommitAsync(HttpClient http, string pat, string? repo, CancellationToken ct)
    {
        var start = await Mcp.Post(http, "/api/v1/sessions", pat, new { client = "claude-code", external_session_id = Guid.NewGuid().ToString(), repo_remote = repo }, ct);
        var id = start.Body.Str("session_id");
        var commit = await Mcp.Post(http, $"/api/v1/sessions/{id}/commit", pat, new
        {
            commit_id = Guid.NewGuid().ToString(), from_index = 0, to_index = 2, skipped = 0,
            records = new[] { "we keep PR titles short", "use develop for small branches", "secrets go in the vault" }
                .Select((t, i) => new { index = i, data = new { role = "user", text = t } }),
        }, ct);
        Assert.Equal(HttpStatusCode.OK, commit.Status);
        await using var db = athar.Db(QaSeed.Org);
        return (Guid.Parse(id), await db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == IndexJobs.ExtractSession && j.Key == $"{id}:2", ct));
    }

    private async Task RunAsync(Job job, ICandidateExtractor extractor, CancellationToken ct)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ollama"] = await athar.App.GetConnectionStringAsync("ollama", ct),
        }).Build();
        await using var db = athar.Db(QaSeed.Org);
        var embedder = new OllamaEmbedder(config, NullLogger<OllamaEmbedder>.Instance);
        await embedder.EnsureReadyAsync(ct); // the worker's job would defer until then
        await new ExtractSessionJob(extractor, embedder).RunAsync(job, db, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Guid> RememberAsync(ModelContextProtocol.Client.McpClient client, string body, CancellationToken ct)
    {
        var id = Guid.Parse((await Mcp.Call(client, "remember", new()
        {
            ["title"] = body, ["body_md"] = body, ["type"] = "fact", ["scope"] = "personal", ["op_id"] = Guid.NewGuid().ToString(),
        }, ct)).Str("memory_id"));
        while (true) // wait for its vectors (embed-vectors runs in the worker)
        {
            await using var db = athar.Db(QaSeed.Org);
            if (await db.ChunkEmbeddings.AnyAsync(e => db.Chunks.Any(c => c.Id == e.ChunkId && c.OwnerId == id), ct)) return id;
            await Task.Delay(500, ct);
        }
    }

    [Fact]
    public void Extraction_stays_in_the_sessions_language()
    {
        const string en = "user: We decided: releases go out on Tuesdays, never on Fridays.";
        const string ar = "user: قررنا أن تكون مدة الاحتفاظ ثلاثين يوماً.";
        Assert.Contains("in English", ExtractionPrompt.LanguageHint(en));
        Assert.Contains("mixes Arabic and English", ExtractionPrompt.LanguageHint(ar));

        static ExtractedCandidate C(string body) => new("Release day", body, "policy", "team");
        Assert.False(ExtractionPrompt.InSessionLanguage(C("所有发布将在星期二进行。"), en)); // what qwen returned in the T085 run
        Assert.False(ExtractionPrompt.InSessionLanguage(C("Релизы по вторникам."), en));
        Assert.True(ExtractionPrompt.InSessionLanguage(C("Releases go out on Tuesdays."), en));
        Assert.True(ExtractionPrompt.InSessionLanguage(C("مدة الاحتفاظ ثلاثون يوماً."), ar));
    }
}
