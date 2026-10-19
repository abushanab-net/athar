using Athar.Web.Auth;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Athar.Web.Api;

/// <summary>
/// FR-PG-06: <c>POST|GET /api/v1/pages/{id}/attachments</c> (bearer) and <c>GET /attachments/{id}</c> (a signed-in person's
/// cookie or a bearer token). Downloads always carry <c>nosniff</c>; only raster images are inline.
/// </summary>
public static class AttachmentEndpoints
{
    public const string ReadPolicy = "attachment-read";

    public static IServiceCollection AddAttachments(this IServiceCollection services)
    {
        services.AddSingleton<Athar.Data.AttachmentStore>().AddScoped<AttachmentService>();
        services.AddAuthorizationBuilder().AddPolicy(ReadPolicy, p => p
            .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, Bearer.Scheme).RequireAuthenticatedUser());
        return services;
    }

    public static void MapAttachmentEndpoints(this RouteGroupBuilder v1)
    {
        // Bearer only (no cookies), so antiforgery doesn't apply.
        v1.MapPost("/pages/{id:guid}/attachments", (Guid id, IFormFile file, AttachmentService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(async () =>
                {
                    await using var content = file.OpenReadStream();
                    return await s.UploadAsync(Actor.From(http), id, file.FileName, file.ContentType, content, ct);
                }))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(Domain.Wiki.AttachmentRules.MaxBytes + 64 * 1024))
            .WithName("upload_attachment").Produces<AttachmentView>();
        v1.MapGet("/pages/{id:guid}/attachments", (Guid id, AttachmentService s, HttpContext http, CancellationToken ct) =>
                Rest.Run(() => s.ListAsync(Actor.From(http), id, ct)))
            .WithName("list_attachments").Produces<List<AttachmentView>>();
    }

    public static void MapAttachmentDownloads(this WebApplication app) =>
        app.MapGet("/attachments/{id:guid}", async (Guid id, AttachmentService s, HttpContext http, CancellationToken ct) =>
        {
            var actor = http.User.HasClaim(c => c.Type == PatAuth.UserClaim) ? Actor.From(http) : Actor.ForUser(http.User);
            AttachmentFile file;
            try { file = await s.OpenAsync(actor, id, ct); }
            // With a body, so status-code pages don't re-execute into the Blazor not-found page (it expects a cookie user).
            catch (AtharError e) when (e.Code is "not_found" or "forbidden") { return Results.Problem(title: "not_found", statusCode: StatusCodes.Status404NotFound); }
            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            headers.CacheControl = "private, no-store";
            return Results.File(file.Content, file.Mime, file.Inline ? null : file.FileName);
        }).RequireAuthorization(ReadPolicy).ExcludeFromDescription();
}
