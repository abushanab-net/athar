using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Athar.IntegrationTests;

/// <summary>T060 (FR-AG-04): session capture on the server. The hook's own half of AT-29 is in athar-hook.test.mjs.</summary>
[Collection(AtharCollection.Name)]
public class SessionTests(AtharApp athar)
{
    [Fact]
    public async Task AT_29_start_and_commit_are_idempotent_and_redacted()
    {
        var ct = AtharApp.Timeout();
        using var member = Bearer(QaSeed.MemberPat);
        var ext = Guid.NewGuid().ToString();
        var id = await Start(member, ext, ct);
        Assert.Equal(id, await Start(member, ext, ct)); // startup, resume and compact map to one session
        await using (var mcp = await Mcp.Connect(athar.Web(), new Uri(athar.App.GetEndpoint("web", "https"), "/mcp"), QaSeed.MemberPat, ct))
        {
            var viaMcp = await Mcp.Call(mcp, "start_session", new() { ["client"] = "claude-code", ["external_session_id"] = ext }, ct);
            Assert.Equal(id.ToString(), viaMcp.GetProperty("session_id").GetString());
        }

        var secret = "ghp_" + new string('a', 30);
        object[] first = [Rec(0, new { role = "user", text = $"push with {secret}" }), Rec(1, new { token = "abc", nested = new { api_key = 42, note = "password=hunter2" } }), Rec(3, new { text = "line 2 was skipped" })];
        Assert.Equal(3, await Commit(member, id, "c1", 0, 3, 1, first, ct));
        Assert.Equal(3, await Commit(member, id, "c1", 0, 3, 1, first, ct)); // a lost response, re-sent from the outbox
        Assert.Equal(4, await Commit(member, id, "c2", 3, 4, 0, [Rec(3, new { text = "changed on resend" }), Rec(4, new { text = "four" })], ct));

        // Bad input and somebody else's session
        using (var bad = await member.PostAsJsonAsync($"/api/v1/sessions/{id}/commit", new { commit_id = "c3", from_index = 5, to_index = 4, skipped = 0, records = Array.Empty<object>() }, ct))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var admin = Bearer(QaSeed.AdminPat);
        using (var foreign = await admin.PostAsJsonAsync($"/api/v1/sessions/{id}/commit", new { commit_id = "x", from_index = 5, to_index = 5, skipped = 0, records = new[] { Rec(5, new { text = "x" }) } }, ct))
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        await using var db = athar.Db(QaSeed.Org);
        var records = await db.SessionRecords.Where(r => r.SessionId == id).OrderBy(r => r.RecordIndex).ToListAsync(ct);
        Assert.Equal([0, 1, 3, 4], records.Select(r => r.RecordIndex));
        Assert.DoesNotContain(secret, records[0].RedactedRecord);
        using (var rec1 = JsonDocument.Parse(records[1].RedactedRecord))
        {
            Assert.Equal("[REDACTED]", rec1.RootElement.GetProperty("token").GetString());
            Assert.Equal("[REDACTED]", rec1.RootElement.GetProperty("nested").GetProperty("api_key").GetString());
            Assert.Equal("password=[REDACTED]", rec1.RootElement.GetProperty("nested").GetProperty("note").GetString());
        }
        Assert.Contains("line 2 was skipped", records[2].RedactedRecord); // the held record 3 wasn't replaced
        Assert.All(records, r => Assert.True(r.PurgeAfter > DateTimeOffset.UtcNow.AddDays(29)));
        Assert.Equal(2, await db.SessionCommits.CountAsync(c => c.SessionId == id, ct));
        Assert.Equal(4, (await db.AgentSessions.SingleAsync(s => s.Id == id, ct)).AckedIndex);
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "session.commit" && e.ResourceId == id.ToString(), ct));
    }

    [Fact]
    public async Task AT_53_transcripts_are_owner_only_and_purged()
    {
        var ct = AtharApp.Timeout();
        using var owner = Bearer(QaSeed.CaptureUserPat);
        var id = await Start(owner, Guid.NewGuid().ToString(), ct);
        await Commit(owner, id, "c1", 0, 2, 0, [Rec(0, new { text = "zero" }), Rec(1, new { text = "one" }), Rec(2, new { text = "two" })], ct);

        // Owner only: an admin can neither read nor list it
        var mine = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}", ct);
        Assert.Equal(3, mine.GetProperty("records").GetArrayLength());
        using var admin = Bearer(QaSeed.AdminPat);
        using (var read = await admin.GetAsync($"/api/v1/sessions/{id}", ct)) Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.DoesNotContain(id.ToString(), await admin.GetStringAsync("/api/v1/sessions", ct));

        // 30 days after receipt, whatever the extraction status
        await using (var db = athar.Db(QaSeed.Org))
            await db.SessionRecords.Where(r => r.SessionId == id && r.RecordIndex == 0).ExecuteUpdateAsync(s => s.SetProperty(r => r.PurgeAfter, DateTimeOffset.UtcNow.AddMinutes(-1)), ct);
        await using (var db = athar.Db(Guid.Empty)) await SessionRetention.PurgeAllAsync(db, DateTimeOffset.UtcNow, ct);
        await using (var db = athar.Db(QaSeed.Org))
        {
            Assert.Equal([1, 2], await db.SessionRecords.Where(r => r.SessionId == id).OrderBy(r => r.RecordIndex).Select(r => r.RecordIndex).ToListAsync(ct));
            Assert.Equal(3, await db.SessionRecords.CountAsync(r => r.SessionId == QaSeed.Session, ct)); // unexpired records stay
        }

        // Deactivation purges at once, in the same transaction
        var orgAdmin = await OrgAdmin(ct);
        Assert.Equal(OrgDecision.Allow, await orgAdmin.SetDeactivatedAsync(QaSeed.Org, QaSeed.Owner, QaSeed.CaptureUser, true, ct));
        await using (var db = athar.Db(QaSeed.Org))
        {
            Assert.False(await db.SessionRecords.AnyAsync(r => r.SessionId == id, ct));
            Assert.True(await db.AgentSessions.AnyAsync(s => s.Id == id, ct)); // the session row itself holds no transcript
        }
        Assert.Equal(OrgDecision.Allow, await orgAdmin.SetDeactivatedAsync(QaSeed.Org, QaSeed.Owner, QaSeed.CaptureUser, false, ct));
    }

    private static object Rec(int index, object data) => new { index, data };

    private static async Task<Guid> Start(HttpClient http, string ext, CancellationToken ct)
    {
        using var r = await http.PostAsJsonAsync("/api/v1/sessions", new { client = "claude-code", external_session_id = ext, repo_remote = "git@github.com:abushanab-net/athar.git" }, ct);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync(ct));
        return (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("session_id").GetGuid();
    }

    private static async Task<int> Commit(HttpClient http, Guid id, string commitId, int from, int to, int skipped, object[] records, CancellationToken ct)
    {
        using var r = await http.PostAsJsonAsync($"/api/v1/sessions/{id}/commit", new { commit_id = commitId, from_index = from, to_index = to, skipped, records }, ct);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync(ct));
        return (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("acked_index").GetInt32();
    }

    private HttpClient Bearer(string token)
    {
        var http = athar.Web();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private async Task<OrgAdminService> OrgAdmin(CancellationToken ct)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:mailpit"] = await athar.App.GetConnectionStringAsync("mailpit", ct),
        }).Build();
        return new OrgAdminService(new Factory(athar), new Mailer(config));
    }
}
