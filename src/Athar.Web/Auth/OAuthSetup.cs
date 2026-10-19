using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Athar.Web.Auth;

/// <summary>
/// OAuth for MCP (FR-ID-07, MCP authorization profile 2025-11-25, docs/03 §2). OpenIddict issues audience-bound access
/// tokens (15 min) for the MCP resource and rotating refresh tokens (30 days) to pre-registered public clients with PKCE.
/// The browser part (<c>/oauth/login</c>, <c>/oauth/consent</c>, <c>/oauth/authorize</c>) uses its own <c>athar.oauth</c>
/// cookie (<c>Path=/oauth</c>), authenticated only by those three; <c>/mcp</c> and <c>/api/v1</c> take bearer tokens only.
/// </summary>
public static class OAuthSetup
{
    public const string CookieScheme = "athar.oauth";
    /// <summary>The OpenIddict application id of the client behind an OAuth token (audit: oauth_client_id).</summary>
    public const string ClientClaim = "athar:client";

    public static string PublicUrl(IConfiguration c) => (c["Athar:PublicUrl"] ?? "https://localhost:7100").TrimEnd('/');
    /// <summary>The canonical MCP resource (RFC 8707): the audience of every access token. Phase P sets the public host's.</summary>
    public static string Resource(IConfiguration c) => c["Athar:OAuth:Resource"] ?? $"{PublicUrl(c)}/mcp";
    private static string Issuer(IConfiguration c) => PublicUrl(c) + "/";

    /// <summary>
    /// A self-signed OpenIddict certificate in <paramref name="folder"/> (<c>openiddict-{name}.pfx</c>), created on first use and
    /// loaded from the file after that. OpenIddict needs no CA; the file is the key, so it is backed up with the keys volume.
    /// </summary>
    public static X509Certificate2 KeyFile(string folder, string name, X509KeyUsageFlags usage)
    {
        var file = Path.Combine(folder, $"openiddict-{name}.pfx");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(folder);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN=Athar OpenIddict {name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
            var temp = $"{file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, cert.Export(X509ContentType.Pfx));
            try { File.Move(temp, file); } // another instance won the race: keep its file
            catch (IOException) { File.Delete(temp); }
        }
        return X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    public static IServiceCollection AddAtharOAuth(this IServiceCollection services, IConfiguration config)
    {
        var resource = Resource(config);
        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AtharDbContext>()
                .ReplaceDefaultEntities<OAuthApplication, OAuthAuthorization, OAuthScope, OAuthToken, Guid>())
            .AddServer(o =>
            {
                o.SetIssuer(new Uri(Issuer(config)));
                o.SetAuthorizationEndpointUris("oauth/authorize").SetTokenEndpointUris("oauth/token").SetRevocationEndpointUris("oauth/revoke")
                    .SetConfigurationEndpointUris(".well-known/oauth-authorization-server", ".well-known/openid-configuration");
                o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                // Rotation: each use returns a new refresh token and the old one stops working at once (no reuse leeway);
                // replaying a used one revokes the chain.
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(15)).SetRefreshTokenLifetime(TimeSpan.FromDays(30)).SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                o.RegisterScopes([.. OAuthClients.Scopes, Scopes.OfflineAccess]);
                // RFC 8707: the MCP resource is the only one; any other gets invalid_target. Every client may ask for it.
                o.RegisterResources(resource).IgnoreResourcePermissions();
                // NFR-08: on a host the certificates live with the Data Protection keys (the `keys` volume, backed up by T098), so
                // tokens survive a container rebuild and a restore. Locally: per-machine development certificates.
                if (config["Athar:Keys:Path"] is { Length: > 0 } keys)
                    o.AddSigningCertificate(KeyFile(keys, "signing", X509KeyUsageFlags.DigitalSignature))
                        .AddEncryptionCertificate(KeyFile(keys, "encryption", X509KeyUsageFlags.KeyEncipherment));
                else
                    o.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
                var aspNet = o.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
                // Only the local Docker stack (deploy/local.override.yml) sets this: it serves http://localhost with no TLS proxy.
                if (config.GetValue<bool>("Athar:OAuth:AllowHttp")) aspNet.DisableTransportSecurityRequirement();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
                o.AddAudiences(resource);
                // Revocation applies on the next request (FR-ID-08).
                o.EnableTokenEntryValidation();
                o.EnableAuthorizationEntryValidation();
            });

        services.AddAuthentication()
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = "athar.oauth";
                o.Cookie.Path = "/oauth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.LoginPath = "/oauth/login";
                o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            });
        return services;
    }

    public static void MapAtharOAuth(this WebApplication app)
    {
        var resource = Resource(app.Configuration);
        var issuer = Issuer(app.Configuration);
        // RFC 9728, at the root and path-suffixed for the /mcp resource. Anonymous.
        object Metadata() => new
        {
            resource, authorization_servers = new[] { issuer }, bearer_methods_supported = new[] { "header" },
            scopes_supported = OAuthClients.Scopes.Append(Scopes.OfflineAccess), resource_name = "Athar",
        };
        app.MapGet("/.well-known/oauth-protected-resource", Metadata).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/.well-known/oauth-protected-resource/mcp", Metadata).AllowAnonymous().ExcludeFromDescription();

        app.MapMethods("/oauth/authorize", ["GET", "POST"], AuthorizeAsync).ExcludeFromDescription();
    }

    // The authorization endpoint (passthrough): sign-in through athar.oauth, persistent consent per user and client,
    // then a code whose principal carries the user, org, grant, client, scopes and the MCP resource as audience.
    private static async Task<IResult> AuthorizeAsync(HttpContext ctx, IOpenIddictApplicationManager apps, IOpenIddictAuthorizationManager grants,
        AtharDbContext db, IConfiguration config)
    {
        var request = ctx.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OAuth request.");
        var resource = Resource(config);
        if (ctx.Request.Query["athar_consent"] == "denied") return Refuse(Errors.AccessDenied, "Consent was denied.");

        var here = ctx.Request.PathBase + ctx.Request.Path + QueryString.Create(ctx.Request.Query.Where(q => q.Key != "athar_consent"));
        var cookie = await ctx.AuthenticateAsync(CookieScheme);
        if (!cookie.Succeeded || !Guid.TryParse(cookie.Principal.FindFirstValue(Claims.Subject), out var userId))
            return Results.Redirect($"/oauth/login?returnUrl={Uri.EscapeDataString(here)}");

        var client = (OAuthApplication?)await apps.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException("Unknown client.");
        db.CurrentOrgId = client.OrgId;
        // The user must still be active, in the client's org (FR-AC-03).
        if (cookie.Principal.FindFirstValue(AtharClaims.Org) != client.OrgId.ToString() || await db.ActorAsync(userId, null) is null)
        {
            await ctx.SignOutAsync(CookieScheme);
            return Refuse(Errors.AccessDenied, "This account can't use this client.");
        }

        var scopes = request.GetScopes() is { Length: > 0 } s ? s : [OAuthClients.Scopes[0], Scopes.OfflineAccess];
        var grant = await grants.FindAsync(subject: userId.ToString(), client: client.Id.ToString(), status: Statuses.Valid,
            type: AuthorizationTypes.Permanent, scopes: scopes).FirstOrDefaultAsync();
        if (grant is null) return Results.Redirect($"/oauth/consent?returnUrl={Uri.EscapeDataString(here)}");
        var grantId = await grants.GetIdAsync(grant);

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, userId.ToString())
            .SetClaim(AtharClaims.Org, client.OrgId.ToString())
            .SetClaim(PatAuth.CredentialClaim, grantId)
            .SetClaim(ClientClaim, client.Id.ToString())
            // OAuth clients are always external agents (FR-ID-06).
            .SetClaim(PatAuth.DisclosureClaim, DisclosureClass.ExternalAgent.ToString());
        identity.SetScopes(scopes);
        identity.SetResources(resource);
        identity.SetAuthorizationId(grantId);
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Refuse(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
