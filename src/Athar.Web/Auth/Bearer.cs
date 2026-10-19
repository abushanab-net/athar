using System.Security.Claims;
using System.Text.Encodings.Web;
using Athar.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;

namespace Athar.Web.Auth;

/// <summary>
/// <c>/mcp</c> and <c>/api/v1</c> take bearer credentials only (docs/03 §2): a PAT or an OAuth access token, never a cookie.
/// One policy (<see cref="Policy"/>) over one policy scheme that forwards PATs to <see cref="PatAuth"/> and anything else to
/// OpenIddict validation, with a single challenge pointing at the protected-resource metadata.
/// </summary>
public static class Bearer
{
    public const string Policy = "bearer";
    public const string Scheme = "bearer";

    public static IServiceCollection AddAtharBearer(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, PatAuth>(PatAuth.SchemeName, null)
            .AddPolicyScheme(Scheme, "PAT or OAuth access token", o =>
            {
                o.ForwardDefaultSelector = ctx => ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer " + PatAuth.Prefix, StringComparison.Ordinal)
                    ? PatAuth.SchemeName : OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
                o.ForwardChallenge = PatAuth.SchemeName;
            });
        services.AddAuthorizationBuilder().AddPolicy(Policy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        return services;
    }

    public static bool IsBearerOnly(PathString path) => path.StartsWithSegments("/mcp") || path.StartsWithSegments("/api/v1");

    /// <summary>
    /// Between authentication and authorization: on bearer-only paths, drop the cookie principal the default scheme set, so
    /// only the bearer policy can authenticate the request, whatever an endpoint asks for.
    /// </summary>
    public static void UseBearerOnlyPaths(this WebApplication app) =>
        app.Use((ctx, next) =>
        {
            if (IsBearerOnly(ctx.Request.Path))
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
                // An API 404 stays an API 404: no re-execution into the Blazor not-found page (it rendered the app shell
                // with a bearer principal and turned the 404 into a 500).
                if (ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
            }
            return next(ctx);
        });
}

/// <summary>
/// Personal access tokens on every request (FR-ID-06, FR-ID-08): the hash must match a token that's neither revoked nor
/// expired, of an active org member. Claims: user, org, credential, scopes and disclosure class.
/// </summary>
public sealed class PatAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AtharDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Pat";
    public const string Prefix = "athar_pat_";
    public const string UserClaim = "sub", OrgClaim = "athar:org", CredentialClaim = "athar:cred", ScopeClaim = "scope", DisclosureClaim = "athar:disclosure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + Prefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var hash = Tokens.Hash(header["Bearer ".Length..].Trim());
        var now = DateTimeOffset.UtcNow;
        var token = await (
            from t in db.AccessTokens.IgnoreQueryFilters()
            join u in db.Users.IgnoreQueryFilters() on new { t.OrgId, Id = t.UserId } equals new { u.OrgId, u.Id }
            where t.TokenHash == hash && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now) && u.DeactivatedAt == null
                && db.OrgMembers.IgnoreQueryFilters().Any(m => m.OrgId == t.OrgId && m.UserId == t.UserId)
            select t).FirstOrDefaultAsync(Context.RequestAborted);
        if (token is null) return AuthenticateResult.Fail("invalid token");
        // For the tokens page; at most one write per token every 5 minutes.
        if (token.LastUsedAt is null || token.LastUsedAt < now.AddMinutes(-5))
            await db.AccessTokens.IgnoreQueryFilters().Where(t => t.OrgId == token.OrgId && t.Id == token.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);

        Claim[] claims =
        [
            new(UserClaim, token.UserId.ToString()), new(OrgClaim, token.OrgId.ToString()),
            new(CredentialClaim, token.Id.ToString()), new(DisclosureClaim, token.DisclosureClass.ToString()),
            .. token.Scopes.Select(s => new Claim(ScopeClaim, s)),
        ];
        return AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    // A body, so the status-code page middleware leaves the 401 alone. resource_metadata points OAuth clients at the
    // protected-resource metadata (RFC 9728, MCP authorization). The bearer scheme forwards every challenge here.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{Request.Scheme}://{Request.Host}{Request.PathBase}/.well-known/oauth-protected-resource\"";
        return Results.Problem(title: "unauthorized", statusCode: 401).ExecuteAsync(Context);
    }
}
