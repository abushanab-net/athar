using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Athar.Data.Seed;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T135 (FR-AG-04 amended): capture settings over REST (interface I-1) and the server side of Pause.</summary>
[Collection(AtharCollection.Name)]
public class CaptureSettingsTests(AtharApp athar)
{
    [Fact]
    public async Task AT_60_capture_settings_round_trip_and_pause_stops_capture_on_the_server()
    {
        var ct = AtharApp.Timeout();
        using var member = Bearer(QaSeed.MemberPat);
        try
        {
            // No row: the defaults, with personal auto-accept on (FR-MM-06 as amended).
            var settings = await Get(member, ct);
            Assert.Empty(settings.GetProperty("repos").EnumerateArray());
            Assert.False(settings.GetProperty("paused").GetBoolean());
            Assert.True(settings.GetProperty("auto_accept").GetProperty("personal").GetBoolean());
            Assert.False(settings.GetProperty("auto_accept").GetProperty("shared").GetBoolean());

            // PUT replaces; repos are normalized like start_session's repo_remote and deduplicated. Audited.
            using (var put = await member.PutAsJsonAsync("/api/v1/me/capture-settings", new
            {
                repos = new[] { "git@github.com:Abushanab-Net/Athar.git", "https://github.com/abushanab-net/athar", "github.com/org/other" },
                paused = false, auto_accept = new { personal = false, shared = true },
            }, ct))
                Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(ct));
            settings = await Get(member, ct);
            Assert.Equal(["github.com/abushanab-net/athar", "github.com/org/other"], settings.GetProperty("repos").EnumerateArray().Select(r => r.GetString()));
            Assert.False(settings.GetProperty("auto_accept").GetProperty("personal").GetBoolean()); // an explicit false sticks
            Assert.True(settings.GetProperty("auto_accept").GetProperty("shared").GetBoolean());

            // Not a remote → 400; a read-only token can read but not change them; another person's settings are separate.
            using (var bad = await member.PutAsJsonAsync("/api/v1/me/capture-settings", new { repos = new[] { "not a repo" }, paused = false, auto_accept = new { personal = true, shared = false } }, ct))
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            using var read = Bearer(QaSeed.MemberReadPat);
            Assert.Equal(2, (await Get(read, ct)).GetProperty("repos").GetArrayLength());
            using (var denied = await read.PutAsJsonAsync("/api/v1/me/capture-settings", new { repos = Array.Empty<string>(), paused = true, auto_accept = new { personal = true, shared = false } }, ct))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var admin = Bearer(QaSeed.AdminPat))
                Assert.Empty((await Get(admin, ct)).GetProperty("repos").EnumerateArray());

            // Pause: session start says capture is off, and a commit stores nothing (but acks, so the outbox drains).
            var ext = Guid.NewGuid().ToString();
            var started = await Start(member, ext, ct);
            Assert.True(started.GetProperty("capture").GetBoolean());
            var id = started.GetProperty("session_id").GetGuid();
            using (var pause = await member.PutAsJsonAsync("/api/v1/me/capture-settings", new { repos = new[] { "github.com/abushanab-net/athar" }, paused = true, auto_accept = new { personal = true, shared = false } }, ct))
                Assert.True(pause.IsSuccessStatusCode);
            started = await Start(member, ext, ct);
            Assert.False(started.GetProperty("capture").GetBoolean());
            Assert.Equal(id, started.GetProperty("session_id").GetGuid()); // an older hook still has a valid id
            await using (var mcp = await Mcp.Connect(athar.Web(), new Uri(athar.App.GetEndpoint("web", "https"), "/mcp"), QaSeed.MemberPat, ct))
                Assert.False((await Mcp.Call(mcp, "start_session", new() { ["client"] = "claude-code", ["external_session_id"] = ext }, ct)).GetProperty("capture").GetBoolean());
            Assert.Equal(1, await Commit(member, id, "p1", 0, 1, 0, [new { index = 0, data = new { text = "while paused" } }, new { index = 1, data = new { text = "still paused" } }], ct));

            await using var db = athar.Db(QaSeed.Org);
            Assert.False(await db.SessionRecords.AnyAsync(r => r.SessionId == id, ct));
            Assert.False(await db.SessionCommits.AnyAsync(c => c.SessionId == id, ct));
            Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "capture_settings.update" && e.ResourceId == QaSeed.Member.ToString(), ct));
        }
        finally
        {
            await using var cleanup = athar.Db(QaSeed.Org);
            await cleanup.CaptureSettings.Where(s => s.UserId == QaSeed.Member).ExecuteDeleteAsync(ct);
        }
    }

    private static async Task<JsonElement> Get(HttpClient http, CancellationToken ct)
    {
        using var r = await http.GetAsync("/api/v1/me/capture-settings", ct);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync(ct));
        return await r.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<JsonElement> Start(HttpClient http, string ext, CancellationToken ct)
    {
        using var r = await http.PostAsJsonAsync("/api/v1/sessions", new { client = "claude-code", external_session_id = ext, repo_remote = "git@github.com:abushanab-net/athar.git" }, ct);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync(ct));
        return await r.Content.ReadFromJsonAsync<JsonElement>(ct);
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
}
