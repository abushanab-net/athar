using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Athar.IntegrationTests;

/// <summary>A2 identity (FR-ID-01..04): bootstrap, invitations, sign-in lockout, password reset, the last-owner rule.</summary>
[Collection(AtharCollection.Name)]
public partial class IdentityTests(AtharApp athar)
{
    [Fact]
    public async Task AT_01_bootstrap_creates_the_first_owner_exactly_once()
    {
        var ct = AtharApp.Timeout();
        // A database of its own on the same server, seeded `minimal` like a production first start.
        var cs = new NpgsqlConnectionStringBuilder(await athar.App.GetConnectionStringAsync(AtharData.ConnectionName, ct)) { Database = "athar_at01" };
        var options = new DbContextOptionsBuilder<AtharDbContext>();
        options.UseAthar(cs.ConnectionString, new SeedOptions("minimal"));
        await using var db = new AtharDbContext(options.Options);
        await db.Database.EnsureDeletedAsync(ct);
        try
        {
            await db.Database.MigrateAsync(ct);

            var owner = await AccountService.BootstrapAsync(db, "Founder@Example.com", ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => AccountService.BootstrapAsync(db, "second@example.com", ct));

            db.ChangeTracker.Clear();
            db.CurrentOrgId = QaSeed.Org;
            var member = await db.OrgMembers.SingleAsync(ct);
            Assert.Equal((owner, OrgRole.Owner, true), (member.UserId, member.Role, member.IsKnowledgeOwner));
            Assert.Equal("founder@example.com", (await db.Users.SingleAsync(ct)).Email);
            Assert.NotNull((await db.Organizations.SingleAsync(ct)).BootstrapCompletedAt);
            Assert.Equal("org.bootstrap", (await db.AuditEvents.SingleAsync(ct)).Action);
        }
        finally { await db.Database.EnsureDeletedAsync(CancellationToken.None); }

        // No public sign-up.
        using var browser = Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.Http.GetAsync("/account/register", ct)).StatusCode);
    }

    [Fact]
    public async Task AT_02_invitations_are_single_use_and_expire()
    {
        var ct = AtharApp.Timeout();
        const string invitee = "new.person@qa.athar.local";
        Assert.Equal(OrgDecision.Allow, await (await Admin(ct)).InviteAsync(QaSeed.Org, QaSeed.Admin, invitee, OrgRole.Member, ct));
        var link = await LinkInMailAsync(invitee, ct);

        using var first = Browser();
        var accepted = await first.SubmitAsync(link, new() { ["Input.DisplayName"] = "New Person", ["Input.Password"] = "a-long-new-password", ["Input.Confirm"] = "a-long-new-password" }, ct);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.Http.GetAsync("/admin/users", ct)).StatusCode); // signed in

        // Single use: the same link again does nothing.
        using var second = Browser();
        var again = await second.Http.GetStringAsync(link, ct);
        Assert.Contains("already been used", again);

        // Expired (qa fixture): refused, and no user is created.
        using var late = Browser();
        // The page says so up front and offers no form.
        var expired = await late.Http.GetStringAsync($"/account/accept-invite?token={QaSeed.ExpiredInviteToken}", ct);
        Assert.Contains("has expired", expired);
        Assert.DoesNotContain("Input.Password", expired);

        await using var db = athar.Db(QaSeed.Org);
        var invitation = await db.Invitations.SingleAsync(i => i.Email == invitee, ct);
        Assert.NotNull(invitation.RedeemedAt);
        Assert.InRange(invitation.ExpiresAt - invitation.CreatedAt, TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(7));
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == invitee, ct));
        Assert.False(await db.Users.AnyAsync(u => u.Email == "expired.invitee@qa.athar.local", ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "invitation.redeem" && e.ResourceId == invitation.Id.ToString(), ct));
    }

    [Fact]
    public async Task AT_02_lockout_after_five_failed_sign_ins()
    {
        var ct = AtharApp.Timeout();
        var email = QaSeed.Email("Qa Lockout User");
        using var browser = Browser();
        for (var i = 0; i < 5; i++)
            await SignInAsync(browser, email, "wrong-password-" + i, ct);

        // The right password is refused while locked out.
        var locked = await SignInAsync(browser, email, QaSeed.Password, ct);
        Assert.Contains("Too many failed attempts", await locked.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Http.GetAsync("/admin/users", ct)).StatusCode); // not signed in

        await using var db = athar.Db(QaSeed.Org);
        Assert.True((await db.IdentityUsers.SingleAsync(u => u.Id == QaSeed.LockoutUser, ct)).LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(14));
    }

    [Fact]
    public async Task AT_02_password_reset_revokes_sessions_and_pats()
    {
        var ct = AtharApp.Timeout();
        var email = QaSeed.Email("Qa Reset User");
        using var session = Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await SignInAsync(session, email, QaSeed.Password, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.Http.GetAsync("/admin/users", ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, await McpStatusAsync(QaSeed.ResetUserPat, ct));

        using var other = Browser();
        await other.SubmitAsync("/account/forgot-password", new() { ["Input.Email"] = email }, ct);
        var reset = await other.SubmitAsync(await LinkInMailAsync(email, ct), new() { ["Input.Password"] = "a-brand-new-password", ["Input.Confirm"] = "a-brand-new-password" }, ct);
        Assert.Contains("Your password was changed", await reset.Content.ReadAsStringAsync(ct));

        // The old session ends on its next request, and the PAT is revoked.
        Assert.Equal(HttpStatusCode.Redirect, (await session.Http.GetAsync("/admin/users", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(QaSeed.ResetUserPat, ct));
        // The new password works.
        using var fresh = Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await SignInAsync(fresh, email, "a-brand-new-password", ct)).StatusCode);

        await using var db = athar.Db(QaSeed.Org);
        Assert.NotNull((await db.AccessTokens.SingleAsync(t => t.Id == QaSeed.ResetUserPatId, ct)).RevokedAt);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "user.password_reset" && e.ActorUserId == QaSeed.ResetUser, ct));
    }

    [Fact]
    public async Task AT_03_last_owner_cannot_be_demoted_or_deactivated_even_concurrently()
    {
        var ct = AtharApp.Timeout();
        var admin = await Admin(ct);
        var org = QaSeed.LastOwnerOrg;

        // Two owners demote themselves at the same time: the org row lock serializes them, so exactly one succeeds.
        var both = await Task.WhenAll(
            admin.ChangeRoleAsync(org, QaSeed.OwnerA, QaSeed.OwnerA, OrgRole.Member, ct),
            admin.ChangeRoleAsync(org, QaSeed.OwnerB, QaSeed.OwnerB, OrgRole.Member, ct));
        Assert.Equal([OrgDecision.Allow, OrgDecision.LastOwner], both.Order());

        var last = both[0] == OrgDecision.Allow ? QaSeed.OwnerB : QaSeed.OwnerA;
        Assert.Equal(OrgDecision.LastOwner, await admin.SetDeactivatedAsync(org, last, last, true, ct));
        Assert.Equal(OrgDecision.LastOwner, await admin.ChangeRoleAsync(org, last, last, OrgRole.Admin, ct));

        // Admins can't touch owners (qa Org: one owner, one admin), but they set the knowledge-owner flag; members can't.
        Assert.Equal(OrgDecision.Forbidden, await admin.ChangeRoleAsync(QaSeed.Org, QaSeed.Admin, QaSeed.Owner, OrgRole.Member, ct));
        Assert.Equal(OrgDecision.Forbidden, await admin.SetDeactivatedAsync(QaSeed.Org, QaSeed.Admin, QaSeed.Owner, true, ct));
        Assert.Equal(OrgDecision.Forbidden, await admin.SetKnowledgeOwnerAsync(QaSeed.Org, QaSeed.Member, QaSeed.Member, true, ct));
        Assert.Equal(OrgDecision.Allow, await admin.SetKnowledgeOwnerAsync(QaSeed.Org, QaSeed.Admin, QaSeed.Member, true, ct));
        Assert.Equal(OrgDecision.Allow, await admin.SetKnowledgeOwnerAsync(QaSeed.Org, QaSeed.Admin, QaSeed.Member, false, ct));

        await using var db = athar.Db(org);
        Assert.Equal(1, await db.OrgMembers.CountAsync(m => m.Role == OrgRole.Owner, ct));
        Assert.Null((await db.Users.SingleAsync(u => u.Id == last, ct)).DeactivatedAt);
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "org_member.role", ct));
    }

    // --- helpers ---

    private async Task<OrgAdminService> Admin(CancellationToken ct)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:mailpit"] = await athar.App.GetConnectionStringAsync("mailpit", ct),
        }).Build();
        return new OrgAdminService(new Factory(athar), new Mailer(config));
    }

    private Browser Browser() => new(athar.App.GetEndpoint("web", "https"));

    private static Task<HttpResponseMessage> SignInAsync(Browser browser, string email, string password, CancellationToken ct) =>
        browser.SubmitAsync("/account/login", new() { ["Input.Email"] = email, ["Input.Password"] = password }, ct);

    private async Task<HttpStatusCode> McpStatusAsync(string pat, CancellationToken ct)
    {
        using var http = athar.Web();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return (await http.SendAsync(request, ct)).StatusCode;
    }

    /// <summary>The path and query of the first link in the newest mail to <paramref name="to"/> (Mailpit API).</summary>
    private async Task<string> LinkInMailAsync(string to, CancellationToken ct)
    {
        using var mailpit = athar.App.CreateHttpClient("mailpit", "http");
        while (true)
        {
            var search = await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString("to:" + to)}", ct);
            if (search.GetProperty("messages") is { } messages && messages.GetArrayLength() > 0)
            {
                var message = await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/message/{messages[0].GetProperty("ID").GetString()}", ct);
                return new Uri(LinkRegex().Match(message.GetProperty("Text").GetString()!).Value).PathAndQuery;
            }
            await Task.Delay(200, ct);
        }
    }

    [Fact]
    public async Task Sidebar_shows_admin_links_only_to_admins()
    {
        var ct = AtharApp.Timeout();
        async Task<string> Home(string who)
        {
            using var browser = new Browser(athar.App.GetEndpoint("web", "https"));
            var signIn = await browser.SubmitAsync("/account/login", new() { ["Input.Email"] = QaSeed.Email(who), ["Input.Password"] = QaSeed.Password }, ct);
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            return await browser.Http.GetStringAsync("/wiki", ct);
        }
        var admin = await Home("Qa Admin");
        var member = await Home("Qa Member");
        // Admin pages are in the sidebar for admins only (T085); the sidebar is a drawer below md (UI redesign, DESIGN.md).
        Assert.Contains("href=\"/admin/users\"", admin);
        Assert.DoesNotContain("href=\"/admin/users\"", member);
        Assert.Contains("class=\"side-nav", member);
        // T150: Workspaces moved under Admin; everyone gets Review and the agent setup in the sidebar.
        Assert.Contains("href=\"/workspaces\"", admin);
        Assert.DoesNotContain("href=\"/workspaces\"", member);
        Assert.Contains("href=\"/review\"", member);
        Assert.Contains("href=\"/agents\"", member);
    }

    [Fact]
    public async Task Api_404s_stay_api_404s_for_bearer_callers()
    {
        var ct = AtharApp.Timeout();
        using var http = new HttpClient { BaseAddress = athar.App.GetEndpoint("web", "https") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", QaSeed.MemberPat);
        // Before: re-executed into the Blazor not-found page (HTML, app shell), which threw on the bearer principal (a 500).
        foreach (var path in new[] { "/api/v1/no-such-route", "/mcp/no-such-route" })
        {
            var response = await http.GetAsync(path, ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex LinkRegex();
}

/// <summary>Fresh contexts on the fixture's database, for services that open one per operation.</summary>
public sealed class Factory(AtharApp app) : IDbContextFactory<AtharDbContext>
{
    public AtharDbContext CreateDbContext() => app.Db(Guid.Empty);
}

/// <summary>A cookie-keeping client that submits the static SSR account forms like a browser.</summary>
public sealed partial class Browser(Uri baseAddress) : IDisposable
{
    public HttpClient Http { get; } = new(new HttpClientHandler { CookieContainer = new(), AllowAutoRedirect = false }) { BaseAddress = baseAddress };

    /// <summary>GETs the page, copies its hidden fields (antiforgery token, form handler), adds <paramref name="fields"/>, POSTs it back.</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string url, Dictionary<string, string> fields, CancellationToken ct)
    {
        var html = await Http.GetStringAsync(url, ct);
        var form = new Dictionary<string, string>();
        foreach (Match input in HiddenInput().Matches(html))
            form[Attr(input.Value, "name")] = System.Net.WebUtility.HtmlDecode(Attr(input.Value, "value"));
        foreach (var (k, v) in fields) form[k] = v;
        return await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
    }

    private static string Attr(string tag, string name) => Regex.Match(tag, $"{name}=\"([^\"]*)\"").Groups[1].Value;

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*>")]
    private static partial Regex HiddenInput();

    public void Dispose() => Http.Dispose();
}
