using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Athar.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Athar.Web.Auth;

public static class AtharClaims
{
    public const string Org = "athar:org";
    public const string Locale = "athar:locale";

    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static Guid OrgId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(Org)!);
}

/// <summary>Random single-use tokens (invitations). Only the SHA-256 hash is stored.</summary>
public static class Tokens
{
    public static string New() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>People sign in with ASP.NET Identity (docs/03 D10). Agents use PATs and OAuth, which stay bearer-only.</summary>
public static class AuthSetup
{
    public static IServiceCollection AddAtharAuth(this IServiceCollection services)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, RevalidatingAuthState>();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddAtharBearer();
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "athar.auth";
            o.Cookie.HttpOnly = true;
            // The pilot's LAN host is plain HTTP (T095), where browsers drop Secure cookies. Over HTTPS it's still Secure.
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.LoginPath = "/account/login";
            o.AccessDeniedPath = "/account/login";
        });
        services.AddIdentityCore<AtharIdentityUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                // Length over composition rules.
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = o.Password.RequireLowercase = o.Password.RequireUppercase = o.Password.RequireNonAlphanumeric = false;
                // FR-ID-02: lockout after 5 failures, for 15 minutes.
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<AtharDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<AtharClaimsFactory>();
        // FR-ID-08: a password reset or deactivation changes the security stamp, and cookies are checked on every request.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);

        // FR-ID-02 rate limits: account form posts, per client address.
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/account")
                    ? RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) })
                    : RateLimitPartition.GetNoLimiter(""));
        });

        services.AddLocalization(o => o.ResourcesPath = "Resources");
        services.AddScoped<Mailer>();
        services.AddScoped<AccountService>();
        services.AddScoped<OrgAdminService>();
        services.AddScoped<WorkspaceAdminService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<AuditService>();
        services.AddScoped<TokenService>();
        services.AddScoped<RequestOrigin>();

        // FR-AU-02: every context, scoped or from the factory, stamps the request's or circuit's origin on its audit events.
        // EF's factory stays registered under its own type; the scoped wrapper takes its place (and feeds the scoped context).
        var ef = services.Single(d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<AtharDbContext>));
        services.Remove(ef);
        services.Add(new ServiceDescriptor(ef.ImplementationType!, ef.ImplementationType!, ef.Lifetime));
        services.AddScoped<Microsoft.EntityFrameworkCore.IDbContextFactory<AtharDbContext>>(sp => new OriginStampingFactory(
            (Microsoft.EntityFrameworkCore.IDbContextFactory<AtharDbContext>)sp.GetRequiredService(ef.ImplementationType!), sp.GetRequiredService<RequestOrigin>()));
        return services;
    }

    public static void UseAtharAuth(this WebApplication app)
    {
        // Explicit order: the default (cookie) principal is dropped on bearer-only paths before any policy runs.
        app.UseAuthentication();
        app.UseBearerOnlyPaths();
        app.UseAuthorization();
        app.UseRateLimiter();
        // The UI language follows the user's locale, else the browser's (en, ar).
        app.UseRequestLocalization(o =>
        {
            string[] cultures = ["en", "ar"];
            o.SetDefaultCulture("en").AddSupportedCultures(cultures).AddSupportedUICultures(cultures);
            o.RequestCultureProviders.Insert(0, new CustomRequestCultureProvider(ctx =>
                Task.FromResult(ctx.User.FindFirstValue(AtharClaims.Locale) is { } l ? new ProviderCultureResult(l) : null)));
        });
        app.MapPost("/account/logout", async (SignInManager<AtharIdentityUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/account/login");
        }).WithMetadata(new Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryTokenAttribute());
        // The language switch (account menu, sign-in page): the culture cookie for everyone, and the user's own locale
        // when signed in, since the locale claim outranks the cookie.
        app.MapPost("/account/language", async (HttpContext http, SignInManager<AtharIdentityUser> signIn, AtharDbContext db) =>
        {
            var form = await http.Request.ReadFormAsync();
            var culture = form["culture"].ToString();
            if (culture is not ("en" or "ar")) return Results.BadRequest();
            http.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName, CookieRequestCultureProvider.MakeCookieValue(new(culture)),
                new() { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax, HttpOnly = true, Secure = http.Request.IsHttps });
            if (http.User.FindFirst(AtharClaims.Org) is not null && http.User.FindFirst(ClaimTypes.NameIdentifier) is not null)
            {
                var (org, id) = (http.User.OrgId(), http.User.UserId());
                await db.Users.IgnoreQueryFilters().Where(u => u.OrgId == org && u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Locale, culture));
                if (await signIn.UserManager.GetUserAsync(http.User) is { } user) await signIn.RefreshSignInAsync(user);
            }
            var back = form["returnUrl"].ToString();
            return Results.LocalRedirect(back.StartsWith('/') && !back.StartsWith("//") ? back : "/");
        }).WithMetadata(new Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryTokenAttribute());
    }

    public static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
}

/// <summary>Adds the org and locale to the cookie principal. Roles are not claims: they're checked fresh on every action (FR-AC-02).</summary>
public sealed class AtharClaimsFactory(UserManager<AtharIdentityUser> users, IOptions<IdentityOptions> options, AtharDbContext db)
    : UserClaimsPrincipalFactory<AtharIdentityUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AtharIdentityUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new(AtharClaims.Org, user.OrgId.ToString()));
        var locale = await db.Users.IgnoreQueryFilters().Where(u => u.OrgId == user.OrgId && u.Id == user.Id).Select(u => u.Locale).SingleAsync();
        identity.AddClaim(new(AtharClaims.Locale, locale));
        return identity;
    }
}

public sealed class OriginStampingFactory(Microsoft.EntityFrameworkCore.IDbContextFactory<AtharDbContext> inner, RequestOrigin origin)
    : Microsoft.EntityFrameworkCore.IDbContextFactory<AtharDbContext>
{
    public AtharDbContext CreateDbContext()
    {
        var db = inner.CreateDbContext();
        db.OriginSource = () => origin.Current;
        return db;
    }
}

/// <summary>Sends mail through Mailpit in development (the AppHost's <c>mailpit</c> resource). Never logs bodies: they carry tokens.</summary>
public sealed class Mailer(IConfiguration config)
{
    /// <summary>Where people open the app (the internal host, docs/03 §2), for links in emails. Athar:PublicUrl is the agents' public host (the OAuth issuer).</summary>
    public string PublicUrl => (config["Athar:AppUrl"] ?? config["Athar:PublicUrl"] ?? "https://localhost:7100").TrimEnd('/');

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        // Production (T094): the company SMTP relay from Athar:Smtp (STARTTLS, app password in a secret).
        // Development: Mailpit, whose connection string is "Endpoint=smtp://host:port".
        using var smtp = config["Athar:Smtp:Host"] is { Length: > 0 } host
            ? new System.Net.Mail.SmtpClient(host, config.GetValue("Athar:Smtp:Port", 587))
            {
                EnableSsl = config.GetValue("Athar:Smtp:StartTls", true),
                Credentials = config["Athar:Smtp:User"] is { Length: > 0 } user ? new System.Net.NetworkCredential(user, config["Athar:Smtp:Password"]) : null,
            }
            : Mailpit();
        var from = config["Athar:Smtp:From"] is { Length: > 0 } f ? f : "athar@localhost";
        await smtp.SendMailAsync(new System.Net.Mail.MailMessage(from, to, subject, body), ct);
    }

    private System.Net.Mail.SmtpClient Mailpit()
    {
        var cs = config.GetConnectionString("mailpit") ?? throw new InvalidOperationException("Set Athar:Smtp:Host (or ConnectionStrings:mailpit in development).");
        var uri = new Uri(cs.Replace("Endpoint=", "", StringComparison.OrdinalIgnoreCase));
        return new System.Net.Mail.SmtpClient(uri.Host, uri.Port);
    }
}
