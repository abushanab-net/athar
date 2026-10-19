using System.Text.Json;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Athar.Data.Seed;

/// <summary>
/// Pre-registered OAuth clients (FR-ID-07): public, PKCE required, exact redirect URIs, consent asked once per user.
/// claude.ai, Claude Desktop and Claude mobile share one client (T099).
/// </summary>
public static class OAuthClients
{
    public const string ClaudeCode = "claude-code";
    /// <summary>Claude Code's callback for a pre-registered client (its OAuth callback port set to 3118).</summary>
    public static readonly string[] ClaudeCodeRedirects = ["http://localhost:3118/callback", "http://127.0.0.1:3118/callback"];

    /// <summary>
    /// claude.ai, Claude Desktop and Claude mobile (T099, AT-31a): a custom connector with "Use your own OAuth client", this id
    /// and no secret (public, PKCE). They all return to Claude's hosted callback, which Anthropic says may move to claude.com.
    /// </summary>
    public const string ClaudeAi = "claude-ai";
    public static readonly string[] ClaudeAiRedirects = ["https://claude.ai/api/mcp/auth_callback", "https://claude.com/api/mcp/auth_callback"];

    /// <summary>
    /// Codex CLI (T133, AT-59): <c>[mcp_servers.athar.oauth] client_id = "codex"</c> with <c>callback_port = 3119</c>. Without the
    /// fixed port Codex picks a free one and the exact match fails. Codex asks for every advertised scope, <c>openid</c> included.
    /// </summary>
    public const string Codex = "codex";
    public static readonly string[] CodexRedirects = ["http://127.0.0.1:3119/callback"];

    /// <summary>
    /// VS Code (T133, AT-59): <c>"oauth": { "clientId": "vscode" }</c> in <c>mcp.json</c>. It listens on 127.0.0.1:33418 and falls
    /// back to the vscode.dev relay (insiders.vscode.dev for Insiders) when that port is busy or the loopback flow fails.
    /// </summary>
    public const string VsCode = "vscode";
    public static readonly string[] VsCodeRedirects = ["http://127.0.0.1:33418/", "https://vscode.dev/redirect", "https://insiders.vscode.dev/redirect"];

    /// <summary>qa: the client integration tests drive the code flow with.</summary>
    public const string Qa = "athar-qa";
    /// <summary>qa: like <see cref="Qa"/>, with 2-second access tokens (AT-31b, expiry).</summary>
    public const string QaShortLived = "athar-qa-short";
    public const string QaRedirect = "http://127.0.0.1:59999/callback";

    public static readonly string[] Scopes = ["read", "write", "memory:write", "pages:delete"];

    internal static void Add(AtharDbContext db, Guid org, bool qa)
    {
        Client(db, org, Id(0xc0de1), ClaudeCode, "Claude Code", ClaudeCodeRedirects);
        Client(db, org, Id(0xc0de4), ClaudeAi, "Claude", ClaudeAiRedirects, type: ApplicationTypes.Web);
        Client(db, org, Id(0xc0de5), Codex, "Codex", CodexRedirects);
        Client(db, org, Id(0xc0de6), VsCode, "VS Code", VsCodeRedirects);
        if (!qa) return;
        Client(db, org, Id(0xc0de2), Qa, "Athar QA client", [QaRedirect]);
        Client(db, org, Id(0xc0de3), QaShortLived, "Athar QA client (short tokens)", [QaRedirect],
            new() { [Settings.TokenLifetimes.AccessToken] = TimeSpan.FromSeconds(2).ToString("c") });
    }

    private static Guid Id(int n) => new($"00000000-0000-7000-8000-{n:x12}");

    private static void Client(AtharDbContext db, Guid org, Guid id, string clientId, string name, string[] redirects, Dictionary<string, string>? settings = null, string type = ApplicationTypes.Native)
    {
        if (db.OauthApplications.Any(a => a.ClientId == clientId)) return;
        string[] permissions =
        [
            Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
            Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
            .. Scopes.Select(s => Permissions.Prefixes.Scope + s), Permissions.Prefixes.Scope + Scopes_.OfflineAccess,
        ];
        db.OauthApplications.Add(new OAuthApplication
        {
            Id = id, OrgId = org, ClientId = clientId, DisplayName = name,
            ClientType = ClientTypes.Public, ApplicationType = type, ConsentType = ConsentTypes.Explicit,
            RedirectUris = JsonSerializer.Serialize(redirects), Permissions = JsonSerializer.Serialize(permissions),
            Requirements = JsonSerializer.Serialize(new[] { Requirements.Features.ProofKeyForCodeExchange }),
            Settings = settings is null ? null : JsonSerializer.Serialize(settings),
            ConcurrencyToken = id.ToString("N"),
        });
    }

    // OpenIddictConstants.Scopes clashes with the field above.
    private static class Scopes_
    {
        public const string OfflineAccess = OpenIddict.Abstractions.OpenIddictConstants.Scopes.OfflineAccess;
    }
}
