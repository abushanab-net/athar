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

namespace Athar.IntegrationTests;

/// <summary>T014: audit writer, immutability, export with redaction, retention (FR-AU-01..04).</summary>
[Collection(AtharCollection.Name)]
public class AuditTests(AtharApp athar)
{
    private static readonly Guid Org = QaSeed.Org;
    private static readonly Guid OAuthClient = Guid.Parse("00000000-0000-7000-8000-00000000c1e7");

    [Fact]
    public async Task AT_21_export_shows_identity_fields_redacts_unreadable_payloads_and_refuses_agents()
    {
        var ct = AtharApp.Timeout();
        await using (var db = athar.Db(Org))
        {
            void Add(string type, Guid id) => db.AuditEvents.Add(new AuditEvent
            {
                OrgId = Org, ActorUserId = QaSeed.Member, CredentialId = QaSeed.MemberPatId, OauthClientId = OAuthClient,
                ClaimedClient = "claude-code (claimed)", Ip = "203.0.113.7", UserAgent = "qa-agent/1.0",
                Action = "test.export", ResourceType = type, ResourceId = id.ToString(), Before = """{"title":"before"}""", After = """{"title":"after"}""",
            });
            Add("page", QaSeed.LabRestricted);  // qa Admin has no access to the access lab
            Add("memory", QaSeed.MemoryPersonal); // Member's personal memory
            Add("page", QaSeed.HomePage);        // readable: Admin is EngWiki's space admin
            Add("team", QaSeed.PlatformTeam);    // org-level: always readable by admins
            await db.SaveChangesAsync(ct);
        }

        using var admin = new Browser(athar.App.GetEndpoint("web", "https"));
        admin.Http.DefaultRequestHeaders.UserAgent.ParseAdd("qa-browser/1.0");
        await admin.SubmitAsync("/account/login", new() { ["Input.Email"] = QaSeed.Email("Qa Admin"), ["Input.Password"] = QaSeed.Password }, ct);

        // The admin page's download (cookie, outside /api/v1).
        var rows = (await admin.Http.GetFromJsonAsync<JsonElement>("/admin/audit/export?format=json&action=test.export", ct)).EnumerateArray()
            .ToDictionary(r => r.GetProperty("resource_id").GetString()!);
        Assert.Equal(4, rows.Count);
        string? After(Guid id) => rows[id.ToString()].GetProperty("after").GetString();
        Assert.Equal("[restricted]", After(QaSeed.LabRestricted));
        Assert.Equal("[restricted]", rows[QaSeed.LabRestricted.ToString()].GetProperty("before").GetString());
        Assert.Equal("[restricted]", After(QaSeed.MemoryPersonal));
        Assert.Contains("after", After(QaSeed.HomePage));
        Assert.Contains("after", After(QaSeed.PlatformTeam));

        // FR-AU-02: the identity fields, each in its own column, with the claimed label apart from the authenticated ones.
        var row = rows[QaSeed.HomePage.ToString()];
        Assert.Equal(QaSeed.Member.ToString(), row.GetProperty("actor_user_id").GetString());
        Assert.Equal(QaSeed.MemberPatId.ToString(), row.GetProperty("credential_id").GetString());
        Assert.Equal(OAuthClient.ToString(), row.GetProperty("oauth_client_id").GetString());
        Assert.Equal("claude-code (claimed)", row.GetProperty("claimed_client").GetString());
        Assert.Equal(("203.0.113.7", "qa-agent/1.0"), (row.GetProperty("ip").GetString(), row.GetProperty("user_agent").GetString()));

        var csv = await admin.Http.GetAsync("/admin/audit/export?format=csv&action=test.export", ct);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync(ct);
        Assert.StartsWith("id,at,action,resource_type,resource_id,actor_user_id,credential_id,oauth_client_id,claimed_client,ip,user_agent,before,after", text);
        Assert.Contains("[restricted]", text);

        // The sign-in itself was audited, with the request's origin stamped on it.
        await using (var db = athar.Db(Org))
        {
            var signIn = await db.AuditEvents.Where(e => e.Action == "auth.sign_in" && e.ActorUserId == QaSeed.Admin)
                .OrderByDescending(e => e.CreatedAt).FirstAsync(ct);
            Assert.Equal("qa-browser/1.0", signIn.UserAgent);
            Assert.NotNull(signIn.Ip);
        }

        // REST is bearer only: the admin's cookie gets 401 there.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.Http.GetAsync("/api/v1/audit", ct)).StatusCode);
        // External-agent credentials get 403, an admin's included (FR-AU-03).
        using var agent = athar.Web();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", QaSeed.AdminPat);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/audit", ct)).StatusCode);
        // An admin's trusted-local token reads it, redacted the same way.
        var tokens = new TokenService(new Factory(athar));
        var (_, created) = await tokens.CreateAsync(Org, QaSeed.Admin, "audit export", ["read"], 1, ct);
        Assert.Equal(OrgDecision.Allow, await tokens.ClassifyAsync(Org, QaSeed.Owner, created!.Id, DisclosureClass.TrustedLocal, "Runs the nightly audit archive on the ops host", ct));
        using var trusted = athar.Web();
        trusted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        var viaApi = (await trusted.GetFromJsonAsync<JsonElement>("/api/v1/audit?action=test.export", ct)).EnumerateArray().ToList();
        Assert.Equal(4, viaApi.Count);
        Assert.Contains(viaApi, r => r.GetProperty("resource_id").GetString() == QaSeed.LabRestricted.ToString() && r.GetProperty("after").GetString() == "[restricted]");
        // A non-admin gets 403 on the page's download too.
        using var member = new Browser(athar.App.GetEndpoint("web", "https"));
        await member.SubmitAsync("/account/login", new() { ["Input.Email"] = QaSeed.Email("Qa Member"), ["Input.Password"] = QaSeed.Password }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Http.GetAsync("/admin/audit/export", ct)).StatusCode);
    }

    [Fact]
    public async Task AT_56_audit_rows_are_append_only_and_retention_removes_only_expired_rows()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(Org);
        db.Audit(Org, QaSeed.Admin, "test.append_only", "team", QaSeed.PlatformTeam);
        await db.SaveChangesAsync(ct);

        var e = await db.AuditEvents.SingleAsync(a => a.Action == "test.append_only", ct);
        e.Action = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
        db.ChangeTracker.Clear();
        db.AuditEvents.Remove(await db.AuditEvents.SingleAsync(a => a.Action == "test.append_only", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
        db.ChangeTracker.Clear();

        var now = DateTimeOffset.UtcNow;
        db.AuditEvents.AddRange(
            new AuditEvent { OrgId = Org, Action = "test.retention_expired", ResourceType = "team", ResourceId = "x", CreatedAt = now.AddDays(-91) },
            new AuditEvent { OrgId = QaSeed.OtherOrg, Action = "test.retention_expired", ResourceType = "team", ResourceId = "y", CreatedAt = now.AddDays(-200) },
            new AuditEvent { OrgId = Org, Action = "test.retention_recent", ResourceType = "team", ResourceId = "z", CreatedAt = now.AddDays(-89) });
        await db.SaveChangesAsync(ct);

        Assert.Equal(2, await AuditRetention.PurgeAsync(db, now, ct: ct));
        Assert.False(await db.AuditEvents.IgnoreQueryFilters().AnyAsync(a => a.Action == "test.retention_expired", ct));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "test.retention_recent", ct));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "test.append_only", ct));
    }
}
