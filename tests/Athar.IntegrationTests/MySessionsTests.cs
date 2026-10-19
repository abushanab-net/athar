using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T138 (AT-62): the My sessions read model and Delete now. The page itself is in the smoke tests.</summary>
[Collection(AtharCollection.Name)]
public class MySessionsTests(AtharApp athar)
{
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, new HashSet<string> { "read", "write", "memory:write" }, DisclosureClass.TrustedLocal, null);

    [Fact]
    public async Task AT_62_my_sessions_groups_by_project_shows_results_and_delete_now_removes_the_transcript()
    {
        var ct = AtharApp.Timeout();
        using var http = athar.Web();

        // A fresh hook session in the pilot repo: no project recorded, so it resolves through the repo link.
        var started = await Mcp.Post(http, "/api/v1/sessions", QaSeed.MemberPat, new { client = "codex", external_session_id = Guid.NewGuid().ToString(), repo_remote = "git@github.com:abushanab-net/athar.git" }, ct);
        var fresh = Guid.Parse(started.Body.Str("session_id"));
        Assert.Equal(HttpStatusCode.OK, (await Mcp.Post(http, $"/api/v1/sessions/{fresh}/commit", QaSeed.MemberPat, new
        {
            commit_id = "m1", from_index = 0, to_index = 1, skipped = 0,
            records = new[] { new { index = 0, data = new { role = "user", text = "hello" } }, new { index = 1, data = new { role = "assistant", text = "hi" } } },
        }, ct)).Status);
        Guid waiting;
        await using (var db = athar.Db(QaSeed.Org))
        {
            var c = new CaptureCandidate { OrgId = QaSeed.Org, SessionId = fresh, OwnerUserId = QaSeed.Member, SuggestedScope = Scope.Personal, Type = MemoryType.Fact, Title = "Says hello", BodyMd = "Says hello.", Status = CandidateStatus.New };
            db.CaptureCandidates.Add(c);
            await db.SaveChangesAsync(ct);
            waiting = c.Id;
        }

        IReadOnlyList<MySessionView> mine;
        await using (var db = athar.Db(QaSeed.Org)) mine = await new SessionService(db, TimeProvider.System).MySessionsAsync(Person(QaSeed.Member), ct);
        var chat = mine.Single(s => s.SessionId == QaSeed.ChatSession);
        Assert.Equal(("chat", "Release checklist", "Athar pilot", 0), (chat.Source, chat.Title, chat.ProjectName, chat.Records));
        var seeded = mine.Single(s => s.SessionId == QaSeed.Session);
        Assert.Equal(("hook", "Athar pilot", 3), (seeded.Source, seeded.ProjectName, seeded.Records));
        var auto = seeded.Candidates.Single(c => c.Id == QaSeed.CandidateAutoAccepted);
        Assert.Equal(("auto_accepted", QaSeed.MemoryPersonal), (auto.Status, auto.MemoryId!.Value));
        Assert.Contains(seeded.Candidates, c => c.Status == "new");
        var hook = mine.Single(s => s.SessionId == fresh);
        Assert.Equal((QaSeed.PilotProject, 2), (hook.ProjectId!.Value, hook.Records));
        Assert.NotNull(hook.PurgeAfter);

        // Nobody else sees them, admins included.
        await using (var db = athar.Db(QaSeed.Org))
            Assert.DoesNotContain(await new SessionService(db, TimeProvider.System).MySessionsAsync(Person(QaSeed.Admin), ct), s => s.SessionId == fresh);

        // Delete now: another person gets 404, a read token 403; the owner removes the records and the unreviewed candidate.
        using (var admin = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/sessions/{fresh}"))
        {
            admin.Headers.Add("Authorization", $"Bearer {QaSeed.AdminPat}");
            Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(admin, ct)).StatusCode);
        }
        using (var read = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/sessions/{fresh}"))
        {
            read.Headers.Add("Authorization", $"Bearer {QaSeed.MemberReadPat}");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(read, ct)).StatusCode);
        }
        using (var owner = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/sessions/{fresh}"))
        {
            owner.Headers.Add("Authorization", $"Bearer {QaSeed.MemberPat}");
            var r = await http.SendAsync(owner, ct);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        await using (var db = athar.Db(QaSeed.Org))
        {
            Assert.False(await db.SessionRecords.AnyAsync(r => r.SessionId == fresh, ct));
            Assert.False(await db.CaptureCandidates.AnyAsync(c => c.Id == waiting, ct));
            Assert.True(await db.AgentSessions.AnyAsync(s => s.Id == fresh, ct)); // the session row stays, without its transcript
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "session.delete_transcript" && e.ResourceId == fresh.ToString(), ct));
        }
    }
}
