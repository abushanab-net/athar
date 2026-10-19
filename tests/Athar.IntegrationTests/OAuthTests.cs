using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Web.Auth;
using Microsoft.AspNetCore.WebUtilities;

namespace Athar.IntegrationTests;

/// <summary>T031: OAuth for MCP (FR-ID-07). AT-31b, plus the parts of AT-31a that run locally (cookie isolation, metadata).</summary>
[Collection(AtharCollection.Name)]
public class OAuthTests(AtharApp athar)
{
    private static string Esc(string s) => Uri.EscapeDataString(s);

    private sealed record Pkce(string Verifier, string Challenge)
    {
        public static Pkce New()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            return new(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }
        private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private Browser NewBrowser() => new(athar.App.GetEndpoint("web", "https"));

    private async Task<string> ResourceAsync(CancellationToken ct)
    {
        using var http = athar.Web();
        return (await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource", ct)).GetProperty("resource").GetString()!;
    }

    private static string AuthorizeUrl(string client, Pkce? pkce, string resource, string redirect = OAuthClients.QaRedirect, string scope = "read write offline_access") =>
        $"/oauth/authorize?client_id={client}&response_type=code&redirect_uri={Esc(redirect)}&scope={Esc(scope)}&state=xyz&resource={Esc(resource)}"
        + (pkce is null ? "" : $"&code_challenge={pkce.Challenge}&code_challenge_method=S256");

    private static string Location(Browser b, HttpResponseMessage r) =>
        new Uri(b.Http.BaseAddress!, r.Headers.Location!).ToString();

    // The browser part, as a person does it: authorize → sign in (athar.oauth) → consent once → back to the client with a code.
    private async Task<string> CodeAsync(Browser b, string client, Pkce pkce, string resource, string user, CancellationToken ct,
        string redirect = OAuthClients.QaRedirect, Action<string>? consentPage = null, string scope = "read write offline_access")
    {
        var r = await b.Http.GetAsync(AuthorizeUrl(client, pkce, resource, redirect, scope), ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/oauth/login", Location(b, r));
        r = await b.SubmitAsync(Location(b, r), new() { ["Input.Email"] = QaSeed.Email(user), ["Input.Password"] = QaSeed.Password }, ct);
        r = await b.Http.GetAsync(Location(b, r), ct);
        if (Location(b, r).Contains("/oauth/consent"))
        {
            consentPage?.Invoke(await b.Http.GetStringAsync(Location(b, r), ct));
            r = await b.SubmitAsync(Location(b, r), new() { ["Decision"] = "approve" }, ct);
            r = await b.Http.GetAsync(Location(b, r), ct);
        }
        var callback = new Uri(Location(b, r));
        Assert.StartsWith(redirect, callback.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal("xyz", query["state"].ToString());
        return query["code"].ToString();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var http = athar.Web();
        var r = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(form), ct);
        return (r.StatusCode, await r.Content.ReadFromJsonAsync<JsonElement>(ct));
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> ExchangeAsync(string client, string code, Pkce pkce, CancellationToken ct, string redirect = OAuthClients.QaRedirect) =>
        TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["client_id"] = client, ["code_verifier"] = pkce.Verifier }, ct);

    private async Task<HttpStatusCode> McpStatusAsync(string? bearer, CancellationToken ct, HttpClient? via = null)
    {
        using var own = via is null ? athar.Web() : null;
        var http = via ?? own!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }) };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return (await http.SendAsync(request, ct)).StatusCode;
    }

    [Fact]
    public async Task AT_31b_code_flow_with_pkce_gives_audience_bound_rotating_tokens_that_mcp_accepts()
    {
        var ct = AtharApp.Timeout();
        using var anonymous = athar.Web();

        // Metadata is anonymous (RFC 8414, RFC 9728), and an unauthenticated MCP call points at it.
        var server = await anonymous.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server", ct);
        Assert.EndsWith("/oauth/authorize", server.GetProperty("authorization_endpoint").GetString());
        Assert.Contains(server.GetProperty("code_challenge_methods_supported").EnumerateArray(), m => m.GetString() == "S256");
        var resource = await ResourceAsync(ct);
        Assert.EndsWith("/mcp", resource);
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) })
        {
            var challenge = await anonymous.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
            Assert.Contains("resource_metadata=", challenge.Headers.WwwAuthenticate.ToString());
        }

        using var browser = NewBrowser();
        var pkce = Pkce.New();
        var code = await CodeAsync(browser, OAuthClients.Qa, pkce, resource, "Qa Member", ct);
        var (status, tokens) = await ExchangeAsync(OAuthClients.Qa, code, pkce, ct);
        Assert.True(status == HttpStatusCode.OK, tokens.ToString());
        var (access, refresh) = (tokens.GetProperty("access_token").GetString()!, tokens.GetProperty("refresh_token").GetString()!);
        Assert.InRange(tokens.GetProperty("expires_in").GetInt32(), 890, 900); // 15 minutes

        // MCP takes the token; its scopes narrow what the agent may do (no memory:write here).
        await using (var mcp = await Mcp.Connect(anonymous, new Uri(anonymous.BaseAddress!, "/mcp"), access, ct))
        {
            Assert.Contains(await mcp.ListToolsAsync(cancellationToken: ct), t => t.Name == "recall");
            Assert.Contains("memory:write", await Mcp.CallError(mcp, "remember", new()
            {
                ["title"] = "x", ["body_md"] = "x", ["type"] = "fact", ["scope"] = "personal", ["op_id"] = Guid.NewGuid().ToString(),
            }, ct));
        }

        // A refresh token isn't an access token.
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(refresh, ct));

        // Refresh rotation: a new pair; the old refresh token stops working.
        var (rotated, next) = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = OAuthClients.Qa }, ct);
        Assert.True(rotated == HttpStatusCode.OK, next.ToString());
        Assert.NotEqual(refresh, next.GetProperty("refresh_token").GetString());
        var nextAccess = next.GetProperty("access_token").GetString();
        Assert.NotEqual(HttpStatusCode.Unauthorized, await McpStatusAsync(nextAccess, ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = OAuthClients.Qa }, ct)).Status);

        // A code is single use, and replaying it revokes what it produced.
        Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync(OAuthClients.Qa, code, pkce, ct)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(nextAccess, ct));

        // AT-31a, locally: the OAuth browser session grants nothing outside /oauth.
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(null, ct, browser.Http));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Http.GetAsync("/admin/users", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/v1/audit", ct)).StatusCode);
    }

    [Fact]
    public async Task AT_31a_claude_connector_client_signs_in_through_claudes_callback_and_recalls()
    {
        // T099: what claude.ai, Desktop and mobile do with "Use your own OAuth client" (claude-ai, no secret). The hosted part
        // (Claude's servers on the public host) is the manual AT-31a run; this is the same exchange, driven locally.
        var ct = AtharApp.Timeout();
        var resource = await ResourceAsync(ct);
        using var browser = NewBrowser();
        var pkce = Pkce.New();
        string? consent = null;
        var redirect = OAuthClients.ClaudeAiRedirects[0];
        var code = await CodeAsync(browser, OAuthClients.ClaudeAi, pkce, resource, "Qa Member", ct, redirect, html => consent = html);
        Assert.Contains("claude.ai", consent); // the consent page names where the answer goes
        var (status, tokens) = await ExchangeAsync(OAuthClients.ClaudeAi, code, pkce, ct, redirect);
        Assert.True(status == HttpStatusCode.OK, tokens.ToString());

        using var http = athar.Web();
        await using var mcp = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), tokens.GetProperty("access_token").GetString()!, ct);
        var recall = await Mcp.Call(mcp, "recall", new() { ["query"] = "deploy approvals" }, ct);
        Assert.True(recall.TryGetProperty("groups", out _));

        // Only Claude's callbacks: anything else is refused before sign-in.
        var other = await browser.Http.GetAsync(AuthorizeUrl(OAuthClients.ClaudeAi, Pkce.New(), resource, "https://evil.example/api/mcp/auth_callback"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    // T133: the requests Codex 0.160 and VS Code 1.141 send (captured from `codex mcp login` and read from VS Code's
    // extension host), one sign-in per redirect URI. Codex asks for every scope the server advertises, openid included.
    public static TheoryData<string, string, string> AgentClients() => new()
    {
        { OAuthClients.Codex, OAuthClients.CodexRedirects[0], "openid offline_access read write memory:write pages:delete" },
        { OAuthClients.VsCode, OAuthClients.VsCodeRedirects[0], "read write memory:write pages:delete offline_access" },
        { OAuthClients.VsCode, OAuthClients.VsCodeRedirects[1], "read write memory:write pages:delete offline_access" },
        { OAuthClients.VsCode, OAuthClients.VsCodeRedirects[2], "read write memory:write pages:delete offline_access" },
    };

    [Theory]
    [MemberData(nameof(AgentClients))]
    public async Task AT_59_codex_and_vscode_sign_in_through_their_callbacks_and_recall(string client, string redirect, string scope)
    {
        var ct = AtharApp.Timeout();
        var resource = await ResourceAsync(ct);
        using var browser = NewBrowser();
        var pkce = Pkce.New();
        var code = await CodeAsync(browser, client, pkce, resource, "Qa Member", ct, redirect, scope: scope);
        var (status, tokens) = await ExchangeAsync(client, code, pkce, ct, redirect);
        Assert.True(status == HttpStatusCode.OK, tokens.ToString());
        Assert.True(tokens.TryGetProperty("refresh_token", out _));

        using var http = athar.Web();
        await using var mcp = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), tokens.GetProperty("access_token").GetString()!, ct);
        Assert.True((await Mcp.Call(mcp, "recall", new() { ["query"] = "deploy approvals" }, ct)).TryGetProperty("groups", out _));

        // Exact match: another loopback port or path is refused before sign-in (Codex without its fixed callback_port).
        var other = await browser.Http.GetAsync(AuthorizeUrl(client, Pkce.New(), resource, "http://127.0.0.1:50123/callback", scope), ct);
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    [Fact]
    public async Task Bearer_only_mcp_and_api_v1_ignore_the_app_cookie()
    {
        var ct = AtharApp.Timeout();
        using var admin = NewBrowser();
        await admin.SubmitAsync("/account/login", new() { ["Input.Email"] = QaSeed.Email("Qa Admin"), ["Input.Password"] = QaSeed.Password }, ct);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync("/admin/users", ct)).StatusCode); // signed in to the app

        // docs/03 §2: the same session gets nothing on /mcp or any /api/v1 route (Lane C's REST included).
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(null, ct, admin.Http));
        foreach (var path in new[] { "/api/v1/memories", "/api/v1/tokens", "/api/v1/audit" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await admin.Http.GetAsync(path, ct)).StatusCode);
    }

    [Fact]
    public async Task AT_31b_missing_pkce_redirect_mismatch_wrong_resource_expired_and_revoked_are_refused()
    {
        var ct = AtharApp.Timeout();
        var resource = await ResourceAsync(ct);
        using var browser = NewBrowser();

        // Missing PKCE: refused with invalid_request, before any sign-in.
        var r = await browser.Http.GetAsync(AuthorizeUrl(OAuthClients.Qa, pkce: null, resource), ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("invalid_request", await r.Content.ReadAsStringAsync(ct));
        // Redirect URI not registered (exact match): refused outright, never redirected.
        r = await browser.Http.GetAsync(AuthorizeUrl(OAuthClients.Qa, Pkce.New(), resource, redirect: "http://127.0.0.1:59999/other"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        // Another resource (RFC 8707): invalid_target, refused outright.
        r = await browser.Http.GetAsync(AuthorizeUrl(OAuthClients.Qa, Pkce.New(), "https://evil.example/mcp"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("invalid_target", await r.Content.ReadAsStringAsync(ct));

        // Expired: the short-lived qa client issues 2-second access tokens.
        var pkce = Pkce.New();
        var (_, shortLived) = await ExchangeAsync(OAuthClients.QaShortLived, await CodeAsync(browser, OAuthClients.QaShortLived, pkce, resource, "Lab Viewer", ct), pkce, ct);
        var expiring = shortLived.GetProperty("access_token").GetString()!;
        Assert.NotEqual(HttpStatusCode.Unauthorized, await McpStatusAsync(expiring, ct));
        await Task.Delay(TimeSpan.FromSeconds(4), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(expiring, ct));

        // Revoked grant: disconnecting the app ends its access and refresh tokens on their next use.
        using var admin = NewBrowser();
        pkce = Pkce.New();
        var (_, tokens) = await ExchangeAsync(OAuthClients.Qa, await CodeAsync(admin, OAuthClients.Qa, pkce, resource, "Qa Admin", ct), pkce, ct);
        var access = tokens.GetProperty("access_token").GetString()!;
        Assert.NotEqual(HttpStatusCode.Unauthorized, await McpStatusAsync(access, ct));
        var service = new TokenService(new Factory(athar));
        var grant = Assert.Single(await service.GrantsAsync(QaSeed.Org, QaSeed.Admin, ct), g => g.Client == "Athar QA client");
        Assert.Equal(OrgDecision.Allow, await service.RevokeGrantAsync(QaSeed.Org, QaSeed.Admin, grant.Id, ct));
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(access, ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync(new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!, ["client_id"] = OAuthClients.Qa,
        }, ct)).Status);
    }

    [Fact]
    public void OpenIddict_keys_live_in_the_keys_folder_and_survive_a_restart()
    {
        // T098, NFR-08: on a host the signing and encryption certificates are files in the backed-up keys volume, not the
        // container's certificate store, so a rebuild or a restore keeps every issued token valid.
        var folder = Path.Combine(Path.GetTempPath(), "athar-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var signing = OAuthSetup.KeyFile(folder, "signing", System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature);
            using var again = OAuthSetup.KeyFile(folder, "signing", System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature);
            using var encryption = OAuthSetup.KeyFile(folder, "encryption", System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment);
            Assert.Equal(signing.Thumbprint, again.Thumbprint);
            Assert.NotEqual(signing.Thumbprint, encryption.Thumbprint);
            Assert.True(signing.HasPrivateKey && encryption.HasPrivateKey);
            Assert.Equal(["openiddict-encryption.pfx", "openiddict-signing.pfx"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
