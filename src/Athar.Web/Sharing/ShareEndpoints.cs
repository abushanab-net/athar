using Microsoft.Extensions.Localization;
using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Athar.Data;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace Athar.Web.Sharing;

/// <summary>
/// Public share links at <c>/s/{token}</c> (FR-SH-02..04): server-rendered, read-only, no app chrome and no circuit.
/// Every request re-checks the link; a password unlocks it for 8 hours through a link-scoped HttpOnly cookie; wrong
/// passwords are rate-limited; responses are <c>no-store</c> and <c>no-referrer</c>; views are audited; the token never
/// reaches logs or traces. ponytail: served by the internal app locally; Phase P puts it on the share host (OD-6).
/// </summary>
public static class ShareEndpoints
{
    private static readonly TimeSpan Unlock = TimeSpan.FromHours(8);
    private const string DiagramsKey = "share.diagrams";

    public static IServiceCollection AddSharing(this IServiceCollection services)
    {
        services.AddScoped<ShareService>().AddSingleton<ShareGuard>();
        // Traces carry the route template, never the token (FR-SH-03).
        services.Configure<AspNetCoreTraceInstrumentationOptions>(o =>
        {
            var previous = o.EnrichWithHttpRequest;
            o.EnrichWithHttpRequest = (activity, request) =>
            {
                previous?.Invoke(activity, request);
                if (!request.Path.StartsWithSegments("/s")) return;
                activity.SetTag("url.path", "/s/{token}");
                activity.SetTag("url.full", null);
                activity.DisplayName = $"{request.Method} /s/{{token}}";
            };
        });
        return services;
    }

    public static void MapSharing(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/s"))
            {
                ctx.Response.OnStarting(() =>
                {
                    var h = ctx.Response.Headers;
                    h.CacheControl = "no-store";
                    h["Referrer-Policy"] = "no-referrer";
                    h["X-Robots-Tag"] = "noindex, nofollow";
                    h.XContentTypeOptions = "nosniff";
                    // A shared file is served sandboxed, like /attachments/{id}; pages may show it and outside https images.
                    h.ContentSecurityPolicy = ctx.Request.Path.Value!.Contains("/attachments/")
                        ? "default-src 'none'; sandbox"
                        : "default-src 'none'; img-src 'self' https: http:; style-src 'self' 'unsafe-inline'; font-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'"
                          + (ctx.Items.ContainsKey(DiagramsKey) ? "; script-src 'self'" : "");
                    return Task.CompletedTask;
                });
                // Error bodies are written here; the app's HTML error pages would add chrome and scripts.
                if (ctx.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
            }
            await next();
        });

        var s = app.MapGroup("/s").AllowAnonymous().ExcludeFromDescription();
        s.MapGet("/{token}", (string token, HttpContext ctx, ShareService shares, IAntiforgery af, IDataProtectionProvider dp, CancellationToken ct) =>
            ShowAsync(token, null, ctx, shares, af, dp, error: null, ct));
        s.MapGet("/{token}/{pageId:guid}", (string token, Guid pageId, HttpContext ctx, ShareService shares, IAntiforgery af, IDataProtectionProvider dp, CancellationToken ct) =>
            ShowAsync(token, pageId, ctx, shares, af, dp, error: null, ct));
        s.MapPost("/{token}/unlock", UnlockAsync);
        s.MapGet("/{token}/attachments/{id:guid}", FileAsync);
    }

    /// <summary>
    /// FR-PG-06 / AT-20: a file of a shared page, under the same checks as the page (live link, unlocked when it has a
    /// password). Anything else is a 404, so the route says nothing about files outside the share.
    /// </summary>
    private static async Task<IResult> FileAsync(string token, Guid id, HttpContext ctx, ShareService shares, AttachmentStore store, IDataProtectionProvider dp, CancellationToken ct)
    {
        var link = await shares.FindAsync(token, ct);
        if (link is null) return Notice(ctx, 404, "ShareNotFoundTitle", "ShareNotFound");
        if (!shares.IsLive(link)) return Notice(ctx, 410, "ShareGoneTitle", "ShareGone");
        var file = link.PasswordHash is not null && !Unlocked(ctx, link, dp)
            ? null
            : (await shares.AttachmentsAsync(link, await shares.PagesAsync(link, ct), ct)).FirstOrDefault(a => a.Id == id);
        if (file is null) return Notice(ctx, 404, "ShareNotFoundTitle", "ShareFileMissing");
        return Results.File(store.Open(file.StorageKey), file.MimeSniffed, AttachmentRules.Inline(file.MimeSniffed) ? null : file.FileName);
    }

    private static async Task<IResult> ShowAsync(string token, Guid? pageId, HttpContext ctx, ShareService shares, IAntiforgery af, IDataProtectionProvider dp, string? error, CancellationToken ct)
    {
        var link = await shares.FindAsync(token, ct);
        if (link is null) return Notice(ctx, 404, "ShareNotFoundTitle", "ShareNotFound");
        if (!shares.IsLive(link)) return Notice(ctx, 410, "ShareGoneTitle", "ShareGone");
        if (link.PasswordHash is not null && !Unlocked(ctx, link, dp))
            return PasswordForm(token, ctx, af, error, error is null ? 200 : 401);

        var pages = await shares.PagesAsync(link, ct);
        var page = pageId is { } id ? pages.FirstOrDefault(p => p.Id == id) : pages.FirstOrDefault();
        if (page is null) return Notice(ctx, 404, "ShareNotFoundTitle", "SharePageMissing");
        await shares.AuditViewAsync(link, page.Id, ct);

        var inShare = pages.Select(p => p.Id.ToString())
            .Concat((await shares.AttachmentsAsync(link, pages, ct)).Select(a => $"/attachments/{a.Id}")).ToHashSet();
        string? Href(string target) => inShare.Contains(target) ? $"/s/{token}/{target.TrimStart('/')}" : null;
        var nav = pages.Count > 1
            ? $"<nav aria-label=\"{ShareHtml.Enc(T(ctx)["SharePages"])}\">" + string.Concat(pages.Select(p => $"<a href=\"/s/{token}/{p.Id}\" dir=\"auto\">{ShareHtml.Enc(p.Title)}</a>")) + "</nav>"
            : "";
        var content = ShareHtml.Content(page.ContentJson, Href);
        var body = $"{nav}<main><h1 dir=\"auto\">{ShareHtml.Enc(page.Title)}</h1>{content}</main>";
        return Page(ctx, 200, page.Title, body, diagrams: ShareHtml.HasMermaid(content));
    }

    private static async Task<IResult> UnlockAsync(string token, HttpContext ctx, ShareService shares, ShareGuard guard, IAntiforgery af, IDataProtectionProvider dp, CancellationToken ct)
    {
        if (!await af.IsRequestValidAsync(ctx)) return Notice(ctx, 400, "ShareBadRequestTitle", "ShareBadRequest");
        var link = await shares.FindAsync(token, ct);
        if (link is null) return Notice(ctx, 404, "ShareNotFoundTitle", "ShareNotFound");
        if (!shares.IsLive(link)) return Notice(ctx, 410, "ShareGoneTitle", "ShareGone");
        if (link.PasswordHash is null) return Results.Redirect($"/s/{token}");

        var who = $"{ctx.Connection.RemoteIpAddress}|{link.Id}";
        if (guard.Blocked(who)) return Notice(ctx, 429, "ShareTooManyTitle", "ShareTooMany");
        var form = await ctx.Request.ReadFormAsync(ct);
        if (!ShareService.PasswordMatches(link, form["password"].ToString()))
        {
            guard.Failed(who);
            return await ShowAsync(token, null, ctx, shares, af, dp, error: T(ctx)["ShareWrongPassword"], ct);
        }
        guard.Reset(who);
        ctx.Response.Cookies.Append(CookieName(link), Protector(dp).Protect(link.Id.ToString(), Unlock), new CookieOptions
        {
            HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = $"/s/{token}", MaxAge = Unlock, IsEssential = true,
        });
        return Results.Redirect($"/s/{token}");
    }

    private static bool Unlocked(HttpContext ctx, ShareLink link, IDataProtectionProvider dp)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName(link), out var value)) return false;
        try { return Protector(dp).Unprotect(value) == link.Id.ToString(); }
        catch (CryptographicException) { return false; }
    }

    private static string CookieName(ShareLink link) => $"athar.share.{link.Id:N}";

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider dp) =>
        dp.CreateProtector("Athar.Share.Unlock").ToTimeLimitedDataProtector();

    private static IResult PasswordForm(string token, HttpContext ctx, IAntiforgery af, string? error, int status)
    {
        var tokens = af.GetAndStoreTokens(ctx);
        var message = error is null ? "" : $"<p role=\"alert\">{ShareHtml.Enc(error)}</p>";
        var t = T(ctx);
        return Page(ctx, status, t["SharePasswordTitle"], $"""
            <main><h1>{ShareHtml.Enc(t["ShareProtected"])}</h1><p class="muted">{ShareHtml.Enc(t["ShareProtectedText"])}</p>{message}
            <form method="post" action="/s/{token}/unlock">
            <input type="hidden" name="{tokens.FormFieldName}" value="{ShareHtml.Enc(tokens.RequestToken)}">
            <label for="password">{ShareHtml.Enc(t["Password"])}</label><input id="password" name="password" type="password" autocomplete="current-password" required>
            <button type="submit">{ShareHtml.Enc(t["ShareOpen"])}</button>
            </form></main>
            """);
    }

    private static IStringLocalizer<SharedResource> T(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();

    private static IResult Notice(HttpContext ctx, int status, string titleKey, string textKey)
    {
        var t = T(ctx);
        return Page(ctx, status, t[titleKey], $"<main class=\"notice\"><h1>{ShareHtml.Enc(t[titleKey])}</h1><p>{ShareHtml.Enc(t[textKey])}</p></main>");
    }

    private static IResult Page(HttpContext ctx, int status, string title, string body, bool diagrams = false)
    {
        var t = T(ctx);
        // T120: only a page whose content has a diagram loads a script (our own, same origin); the CSP allows it there alone.
        if (diagrams) ctx.Items[DiagramsKey] = true;
        var html = ShareHtml.Document(title, body, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft,
            t["ShareReadOnly"], t["ShareSharedFrom"], diagrams);
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }
}

/// <summary>Wrong-password throttle per client and link: 5 failures lock it for 15 minutes (FR-SH-03, AT-20).</summary>
public sealed class ShareGuard(TimeProvider clock)
{
    private const int Limit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    // ponytail: in-process; needs a shared store if the share host ever runs more than one instance.
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> failures = new();

    public bool Blocked(string who) =>
        failures.TryGetValue(who, out var f) && f.Count >= Limit && clock.GetUtcNow() - f.Since < Window;

    public void Failed(string who) => failures.AddOrUpdate(who, _ => (1, clock.GetUtcNow()),
        (_, f) => clock.GetUtcNow() - f.Since >= Window ? (1, clock.GetUtcNow()) : (f.Count + 1, f.Since));

    public void Reset(string who) => failures.TryRemove(who, out _);
}
