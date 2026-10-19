using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Athar.Data;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Athar.Web.Mcp;

/// <summary>
/// One call filter for every MCP tool (FR-AG-12a): tool, duration, outcome and result count with the FR-AU-02 identity,
/// never arguments or content; plus the read audits agents owe (FR-AU-01, <see cref="AgentAudit"/>). Best-effort: a failed
/// write is logged and never fails the call.
/// </summary>
public static partial class McpCapture
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (ctx, ct) =>
        {
            var started = Stopwatch.GetTimestamp();
            CallToolResult? result = null;
            string? failure = null;
            try { return result = await next(ctx, ct); }
            catch (Exception e)
            {
                failure = e is OperationCanceledException ? "cancelled" : Code(e.Message);
                throw;
            }
            finally
            {
                await RecordAsync(ctx, result, failure ?? (result?.IsError == true ? Code(Text(result)) : "ok"),
                    (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
        };

    private static async Task RecordAsync(RequestContext<CallToolRequestParams> ctx, CallToolResult? result, string outcome, int ms)
    {
        var services = ctx.Services!;
        try
        {
            var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext!;
            var actor = Actor.From(http);
            var origin = RequestOrigin.From(http);
            var tool = ctx.Params!.Name;
            var structured = result?.StructuredContent is { } s ? JsonSerializer.SerializeToElement(s) : (JsonElement?)null;
            await using var db = await services.GetRequiredService<IDbContextFactory<AtharDbContext>>().CreateDbContextAsync();
            db.CurrentOrgId = actor.OrgId;
            db.McpCalls.Add(new McpCall
            {
                ActorUserId = actor.UserId, CredentialId = actor.CredentialId, OauthClientId = origin.OAuthClientId,
                ClaimedClient = origin.ClaimedClient, Ip = origin.Ip, UserAgent = origin.UserAgent,
                Tool = tool.Length <= 100 ? tool : tool[..100], DurationMs = ms, Outcome = outcome, ResultCount = Count(structured),
            });
            if (outcome == "ok" && structured is { } r && ctx.Params.Arguments is { } args
                && AgentAudit.For(actor, tool, JsonSerializer.SerializeToElement(args), r) is { } audit)
                db.AuditEvents.Add(audit);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception e) { services.GetService<ILogger<McpCall>>()?.LogWarning(e, "MCP call not recorded"); }
    }

    /// <summary>
    /// Items in the result's first list; a list of groups (recall) counts the items in its groups. Null for a single
    /// result, one with its own <c>id</c> or <c>*_id</c> (a page's <c>path</c> isn't a result list).
    /// </summary>
    internal static int? Count(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } o
            || o.EnumerateObject().Any(p => p.Name == "id" || p.Name.EndsWith("_id", StringComparison.Ordinal))) return null;
        foreach (var p in o.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array)
                return p.Value.EnumerateArray().Sum(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 1);
        return null;
    }

    private static string? Text(CallToolResult r) => r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;

    /// <summary>The <c>code</c> of an <c>"code: message"</c> error (<see cref="Services.AtharError"/>), else <c>error</c>.</summary>
    internal static string Code(string? message) => message is not null && ErrorCode().Match(message) is { Success: true } m ? m.Groups[1].Value : "error";

    [GeneratedRegex(@"(?:^|\s)([a-z]+(?:_[a-z]+)*): ")]
    private static partial Regex ErrorCode();
}

/// <summary>
/// FR-AU-01's read audits for agent calls (MCP and REST): <c>memory.search</c> with the query and result IDs, and
/// <c>page.view</c> / <c>page.list</c>. People in the browser aren't audited here: these run only on token-authenticated calls.
/// </summary>
public static class AgentAudit
{
    public static AuditEvent? For(Actor actor, string tool, JsonElement args, JsonElement result) => tool switch
    {
        "search" => Event(actor, "memory.search", "memory", "*", new
        {
            query = Str(args, "query") is { } q && q.Length > 500 ? q[..500] : Str(args, "query"),
            ids = Ids(result, "hits", "id"),
        }),
        "get_page" when Str(args, "page_id") is { } page => Event(actor, "page.view", "page", page, null),
        "list_pages" when Str(args, "space_id") is { } space => Event(actor, "page.list", "space", space, new
        {
            parent_id = Str(args, "parent_id"),
            ids = Ids(result, "pages", "page_id"),
        }),
        _ => null,
    };

    /// <summary>The REST routes' equivalent of the MCP filter's audit: same tool names, same argument names, same JSON.</summary>
    public static async Task<T> AuditAsync<T>(this Task<T> call, HttpContext http, string tool, object args)
    {
        var result = await call;
        var actor = Actor.From(http);
        if (For(actor, tool, JsonSerializer.SerializeToElement(args), JsonSerializer.SerializeToElement(result, McpEndpoint.Json)) is { } audit)
        {
            await using var db = await http.RequestServices.GetRequiredService<IDbContextFactory<AtharDbContext>>().CreateDbContextAsync();
            db.CurrentOrgId = actor.OrgId;
            db.AuditEvents.Add(audit);
            await db.SaveChangesAsync(http.RequestAborted);
        }
        return result;
    }

    private static AuditEvent Event(Actor a, string action, string type, string id, object? after) => new()
    {
        ActorUserId = a.UserId, CredentialId = a.CredentialId, ClaimedClient = a.ClaimedClient,
        Action = action, ResourceType = type, ResourceId = id,
        After = after is null ? null : JsonSerializer.Serialize(after),
    };

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string?[] Ids(JsonElement o, string list, string id) =>
        o.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array ? [.. items.EnumerateArray().Select(i => Str(i, id))] : [];
}
