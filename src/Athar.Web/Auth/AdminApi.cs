using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Mcp;

namespace Athar.Web.Auth;

/// <summary>
/// Lane A's REST endpoints (docs/06), bearer only like all of <c>/api/v1</c>: <c>/api/v1/tokens</c>,
/// <c>/api/v1/admin/tokens/{id}/classify</c> and <c>/api/v1/audit</c>. Admin operations refuse external-agent credentials
/// (FR-AU-03, FR-ID-06). People use the same services from the admin pages; their audit download is <c>/admin/audit/export</c>.
/// </summary>
public static class AdminApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public sealed record CreateBody(string Name, string[] Scopes, [property: JsonPropertyName("expires_in_days")] int? ExpiresInDays);
    public sealed record ClassifyBody([property: JsonPropertyName("disclosure_class")] string DisclosureClass, string? Reason);

    public static void MapAdminApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(Bearer.Policy).ExcludeFromDescription();

        // FR-ID-06: always external-agent; a credential never mints a token with scopes it doesn't hold itself.
        v1.MapPost("/tokens", async (HttpContext ctx, TokenService tokens, CreateBody body, CancellationToken ct) =>
        {
            var actor = Actor.From(ctx);
            var scopes = body.Scopes ?? [];
            if (scopes.All(TokenService.AllScopes.Contains) && !scopes.All(actor.Scopes.Contains))
                return Problem("forbidden", "A token can't create one with scopes it doesn't have.", 403);
            var (decision, created) = await tokens.CreateAsync(actor.OrgId, actor.UserId, body.Name, scopes, body.ExpiresInDays, ct);
            return decision switch
            {
                OrgDecision.Allow => Results.Json(new { created!.Id, created.Token, created.ExpiresAt, disclosure_class = "external-agent" }, Json, statusCode: 201),
                OrgDecision.Invalid => Problem("invalid_input", $"Give a name, scopes from {string.Join(", ", TokenService.AllScopes)}, and expires_in_days from 0 (never) to 365.", 400),
                _ => Problem("forbidden", null, 403),
            };
        });

        v1.MapGet("/tokens", async (HttpContext ctx, TokenService tokens, CancellationToken ct) =>
        {
            var actor = Actor.From(ctx);
            return Results.Json(await tokens.MineAsync(actor.OrgId, actor.UserId, ct), Json);
        });

        v1.MapDelete("/tokens/{id:guid}", async (Guid id, HttpContext ctx, TokenService tokens, CancellationToken ct) =>
        {
            var actor = Actor.From(ctx);
            return await tokens.RevokeAsync(actor.OrgId, actor.UserId, id, ct) == OrgDecision.Allow ? Results.NoContent() : Problem("forbidden", null, 403);
        });

        v1.MapPost("/admin/tokens/{id:guid}/classify", async (Guid id, HttpContext ctx, TokenService tokens, ClassifyBody body, CancellationToken ct) =>
        {
            var actor = Actor.From(ctx);
            if (actor.Disclosure == DisclosureClass.ExternalAgent) return Problem("forbidden", "External agents can't reclassify credentials.", 403);
            DisclosureClass? cls = body.DisclosureClass switch { "trusted-local" => DisclosureClass.TrustedLocal, "external-agent" => DisclosureClass.ExternalAgent, _ => null };
            if (cls is null) return Problem("invalid_input", "disclosure_class is trusted-local or external-agent.", 400);
            return await tokens.ClassifyAsync(actor.OrgId, actor.UserId, id, cls.Value, body.Reason, ct) switch
            {
                OrgDecision.Allow => Results.NoContent(),
                OrgDecision.Invalid => Problem("invalid_input", "State the reason.", 400),
                _ => Problem("forbidden", "Only an org owner reclassifies tokens.", 403),
            };
        });

        // FR-AU-03: admins only, and never through an external-agent credential (AT-21).
        v1.MapGet("/audit", (HttpContext ctx, AuditService audit, CancellationToken ct) =>
        {
            var actor = Actor.From(ctx);
            return actor.Disclosure == DisclosureClass.ExternalAgent
                ? Task.FromResult(Problem("forbidden", "External agents can't read the audit.", 403))
                : ExportAsync(ctx, audit, actor.OrgId, actor.UserId, ct);
        });

        // The admin page's download, with the person's cookie (not part of /api/v1).
        app.MapGet("/admin/audit/export", (HttpContext ctx, AuditService audit, CancellationToken ct) =>
            ExportAsync(ctx, audit, ctx.User.OrgId(), ctx.User.UserId(), ct)).RequireAuthorization().ExcludeFromDescription();
    }

    /// <summary><c>?format=json|csv&amp;action=&amp;resource_type=&amp;actor=&amp;from=&amp;to=&amp;limit=</c></summary>
    private static async Task<IResult> ExportAsync(HttpContext ctx, AuditService audit, Guid orgId, Guid userId, CancellationToken ct)
    {
        var q = ctx.Request.Query;
        var filter = new AuditService.Filter(q["action"], q["resource_type"],
            Guid.TryParse(q["actor"], out var who) ? who : null,
            DateTimeOffset.TryParse(q["from"], out var from) ? from : null,
            DateTimeOffset.TryParse(q["to"], out var to) ? to : null,
            int.TryParse(q["limit"], out var limit) ? limit : 1000);
        var rows = await audit.QueryAsync(orgId, userId, filter, ct);
        if (rows is null) return Problem("forbidden", "Only org admins read the audit.", 403);
        ctx.Response.Headers.CacheControl = "no-store";
        return q["format"] == "csv"
            ? Results.File(Encoding.UTF8.GetBytes(AuditService.ToCsv(rows)), "text/csv", "athar-audit.csv")
            : Results.Json(rows, Json);
    }

    private static IResult Problem(string code, string? detail, int status) => Results.Problem(title: code, detail: detail, statusCode: status);
}
