using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athar.Web.Services;
using Microsoft.AspNetCore.Diagnostics;

namespace Athar.Web.CollabBridge;

/// <summary>Internal collab API (docs/06 §6.4). Only collab calls it, with the shared service secret.</summary>
public static class CollabEndpoints
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record AuthorizeRequest(string Page, string? Ticket, string? Lease);
    private sealed record TicketRequest(Guid User, Guid? Credential, Guid Page, string Level);

    public static IServiceCollection AddCollabBridge(this IServiceCollection services)
    {
        services.AddSingleton<CollabTickets>().AddScoped<CollabStore>().AddScoped<PageOpsService>().AddScoped<CommentService>().AddScoped<NotificationService>().AddScoped<VersionService>();
        services.AddHostedService<AnchorReconciler>();
        services.AddHttpClient<CollabClient>((sp, http) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            http.BaseAddress = new Uri(config["services:collab:http:0"] ?? "http://localhost:1234");
            http.DefaultRequestHeaders.Add("x-collab-secret", config["Collab:Secret"] ?? "");
            http.Timeout = TimeSpan.FromSeconds(30); // past this the caller gets `pending` and polls
        });
        return services;
    }

    public static void MapCollabBridge(this WebApplication app)
    {
        var g = app.MapGroup("/internal/collab").AddEndpointFilter(RequireSecret).ExcludeFromDescription();

        // Validate a ticket (handshake) or renew a lease (every 30 s).
        g.MapPost("/authorize", async (HttpRequest req, CollabTickets tickets, CollabStore store, Athar.Data.AtharDbContext db) =>
        {
            var r = await req.ReadFromJsonAsync<AuthorizeRequest>(Json);
            var grant = r?.Ticket is { } t ? tickets.Redeem(t) : r?.Lease is { } l ? tickets.Renew(l) : null;
            if (grant is null || !Guid.TryParse(r!.Page, out var id) || id != grant.Page) return Results.Unauthorized();
            if (await store.FindAsync(id) is not { } page || page.OrgId != grant.Org) return Results.Unauthorized();
            // FR-ID-08 (T013): the actor and their access are re-checked at the handshake and on every renewal, so a revocation
            // or an edit→view downgrade reaches collab within 30 s. A trashed page already ends the lease.
            db.CurrentOrgId = grant.Org;
            if (await Auth.Revocation.CollabLevelAsync(db, grant.User, grant.Credential, id, grant.Level) is not { } level) return Results.Unauthorized();
            grant = grant with { Level = level };
            return Results.Json(new { grant.User, grant.Credential, grant.Level, lease = tickets.Lease(grant), lease_seconds = CollabTickets.LeaseSeconds }, Json);
        });

        g.MapGet("/pages/{id:guid}/state", async (Guid id, CollabStore store) =>
            await store.StateAsync(id) is { } state ? Results.Json(state, Json) : Results.NotFound());

        g.MapPut("/pages/{id:guid}", async (Guid id, HttpRequest req, CollabStore store) =>
        {
            if (await req.ReadFromJsonAsync<CollabPersist>(Json) is not { } body) return Results.BadRequest();
            var (revision, stored) = await store.PersistAsync(id, body);
            if (revision is { } n) return Results.Json(new { revision = n }, Json);
            return stored is null ? Results.NotFound() : Results.Json(stored, Json, statusCode: StatusCodes.Status409Conflict);
        });

        if (app.Environment.IsDevelopment())
        {
            // ponytail: test hooks (docs/06 §6.4). Tickets come from the signed-in page once sign-in lands (T010).
            g.MapPost("/tickets", async (HttpRequest req, CollabTickets tickets, CollabStore store) =>
                await req.ReadFromJsonAsync<TicketRequest>(Json) is { } r && await store.FindAsync(r.Page) is { } page
                    ? Results.Json(new { ticket = tickets.Issue(new(r.User, r.Credential, page.OrgId, r.Page, r.Level)) }, Json)
                    : Results.BadRequest());
            g.MapGet("/pages/{id:guid}/revisions", async (Guid id, CollabStore store) =>
                Results.Json((await store.RevisionsAsync(id)).Select(r => new { r.Revision, r.LiveSeq, r.Contributors, r.AgentCredentials, r.BatchIds, r.OpIds }), Json));
        }
    }

    private static async ValueTask<object?> RequireSecret(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        // Collab reads raw status codes; never re-execute a 401 into the HTML not-found page.
        if (ctx.HttpContext.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        var expected = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Collab:Secret"];
        var given = ctx.HttpContext.Request.Headers["x-collab-secret"].ToString();
        // Fails closed when the secret isn't configured.
        return !string.IsNullOrEmpty(expected) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected))
            ? await next(ctx)
            : Results.Unauthorized();
    }
}
