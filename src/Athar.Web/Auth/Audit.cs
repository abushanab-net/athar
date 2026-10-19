using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Auth;

/// <summary>
/// The origin of the current request or circuit (FR-AU-02). HTTP requests read it from the connection, headers and the
/// PAT claims; a Blazor circuit has no HttpContext after its first request, so Routes hands it the values captured then.
/// </summary>
public sealed class RequestOrigin(IHttpContextAccessor http)
{
    public AuditOrigin? Circuit { get; set; }

    public AuditOrigin? Current => http.HttpContext is { } ctx ? From(ctx) : Circuit;

    public static AuditOrigin From(HttpContext ctx) => new(
        ctx.Connection.RemoteIpAddress?.ToString(),
        ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 512)] : null,
        Guid.TryParse(ctx.User.FindFirstValue(PatAuth.CredentialClaim), out var credential) ? credential : null,
        Guid.TryParse(ctx.User.FindFirstValue(OAuthSetup.ClientClaim), out var client) ? client : null,
        // Untrusted attribution label (FR-AG-03), kept apart from the authenticated identity.
        ClaimedClient: ctx.Request.Headers["X-Athar-Client"].FirstOrDefault() is { Length: > 0 } c ? c[..Math.Min(c.Length, 200)] : null);
}

/// <summary>
/// The audit trail for admins (FR-AU-03): org owners and admins filter and export it. Metadata is always shown; before/after
/// payloads only when the admin can currently read the resource (pages, spaces and memories through the same rules as
/// everywhere else), otherwise <c>[restricted]</c>. Unknown resource types are redacted: fail closed.
/// </summary>
public sealed class AuditService(IDbContextFactory<AtharDbContext> dbs)
{
    public const string Restricted = "[restricted]";

    public sealed record Filter(string? Action = null, string? ResourceType = null, Guid? Actor = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 200);

    public sealed record Row(Guid Id, DateTimeOffset At, string Action, string ResourceType, string ResourceId, Guid? ActorUserId,
        Guid? CredentialId, Guid? OauthClientId, string? ClaimedClient, string? Ip, string? UserAgent, string? Before, string? After);

    // Org-level records every admin may read.
    private static readonly HashSet<string> AdminTypes = ["organization", "user", "team", "workspace", "invitation", "token"];

    /// <summary>Null when the caller isn't an active org owner or admin.</summary>
    public async Task<List<Row>?> QueryAsync(Guid orgId, Guid actorId, Filter filter, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = orgId;
        if (!OrgRules.IsAdmin(await OrgAdminService.ActorRoleAsync(db, actorId, ct))) return null;

        var q = db.AuditEvents.AsNoTracking();
        if (filter.Action is { Length: > 0 } a) q = q.Where(e => e.Action.StartsWith(a));
        if (filter.ResourceType is { Length: > 0 } t) q = q.Where(e => e.ResourceType == t);
        if (filter.Actor is { } u) q = q.Where(e => e.ActorUserId == u);
        if (filter.From is { } from) q = q.Where(e => e.CreatedAt >= from);
        if (filter.To is { } to) q = q.Where(e => e.CreatedAt < to);
        var events = await q.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(Math.Clamp(filter.Limit, 1, 10_000)).ToListAsync(ct);

        var readable = await ReadableAsync(db, actorId, events, ct);
        return events.Select(e =>
        {
            var show = AdminTypes.Contains(e.ResourceType) || readable.Contains((e.ResourceType, e.ResourceId));
            return new Row(e.Id, e.CreatedAt, e.Action, e.ResourceType, e.ResourceId, e.ActorUserId, e.CredentialId, e.OauthClientId,
                e.ClaimedClient, e.Ip, e.UserAgent, Redact(e.Before, show), Redact(e.After, show));
        }).ToList();
    }

    private static string? Redact(string? payload, bool show) => payload is null || show ? payload : Restricted;

    // Which of the referenced pages, spaces and memories the admin can read right now, in one query per type.
    private static async Task<HashSet<(string, string)>> ReadableAsync(AtharDbContext db, Guid actorId, List<AuditEvent> events, CancellationToken ct)
    {
        Guid[] Ids(string type) => [.. events.Where(e => e.ResourceType == type).Select(e => Guid.TryParse(e.ResourceId, out var id) ? id : Guid.Empty).Distinct()];
        var caller = new Caller(actorId);
        var result = new HashSet<(string, string)>();

        var pages = Ids("page");
        foreach (var id in await db.PagesFor(caller).Where(p => pages.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct)) result.Add(("page", id.ToString()));
        var spaces = Ids("space");
        foreach (var id in await db.SpacesFor(caller).Where(s => spaces.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)) result.Add(("space", id.ToString()));

        var memories = Ids("memory");
        if (memories.Length > 0)
        {
            var member = await db.OrgMembers.SingleAsync(m => m.UserId == actorId, ct);
            var who = new MemoryActor(actorId, member.IsKnowledgeOwner, ExternalAgent: false,
                await db.TeamMembers.Where(t => t.UserId == actorId).Select(t => t.TeamId).ToListAsync(ct),
                await db.ProjectMembers.Where(p => p.UserId == actorId).Select(p => p.ProjectId).ToListAsync(ct),
                await db.ProjectMembers.Where(p => p.UserId == actorId && p.Role == ProjectRole.Admin).Select(p => p.ProjectId).ToListAsync(ct));
            foreach (var id in await db.Memories.Where(MemoryRules.Visible(who)).Where(m => memories.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct))
                result.Add(("memory", id.ToString()));
        }
        return result;
    }

    public static string ToCsv(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder("id,at,action,resource_type,resource_id,actor_user_id,credential_id,oauth_client_id,claimed_client,ip,user_agent,before,after\n");
        foreach (var r in rows)
            sb.AppendJoin(',', new object?[] { r.Id, r.At.ToString("O", CultureInfo.InvariantCulture), r.Action, r.ResourceType, r.ResourceId, r.ActorUserId,
                r.CredentialId, r.OauthClientId, r.ClaimedClient, r.Ip, r.UserAgent, r.Before, r.After }.Select(Csv)).Append('\n');
        return sb.ToString();
    }

    // RFC 4180 quoting, and a leading quote on values a spreadsheet would run as a formula.
    private static string Csv(object? value)
    {
        var s = value?.ToString() ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
