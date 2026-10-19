using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athar.Web.CollabBridge;

/// <summary>
/// A server-side page write (docs/03 §4.2, docs/06 §6.4): <c>create</c>, <c>append</c>, <c>replace</c> (with
/// <c>ExpectedLiveToken</c>), <c>restore</c> (<c>VersionId</c>, <c>Json</c>) or <c>anchorComment</c> (<c>ThreadId</c>,
/// <c>From</c>/<c>To</c> as base64 Yjs relative positions, or none and an optional <c>AnchorHint</c> to find
/// <c>Quote</c> by its text). Every field is part of the request hash.
/// </summary>
public sealed record PageOp(
    string Kind, string? Markdown = null, string? ExpectedLiveToken = null,
    Guid? VersionId = null, JsonElement? Json = null,
    Guid? ThreadId = null, string? From = null, string? To = null, string? Quote = null, string? AnchorHint = null,
    Guid? SpaceId = null, Guid? ParentId = null, string? Title = null);

/// <summary>What a page op returns once the revision holding it is persisted.</summary>
public sealed record PageOpResult(Guid PageId, long Revision, string LiveToken, Guid[] OrphanedThreads);

/// <summary>`get_page` (docs/06 §6.2): content and live token from one snapshot (FR-RT-06).</summary>
public sealed record PageView(Guid PageId, string Title, string Content, string LiveToken, long Revision, string Space, string[] Path, DateTimeOffset UpdatedAt);

/// <summary>`GET /api/v1/operations/{op_id}`: the stored outcome of any durable write (FR-RT-07).</summary>
public sealed record OperationStatusView(string OpId, string Status, string TargetKind, string? TargetId, JsonElement? Result, string? Error);

/// <summary>Calls collab's internal API (docs/06 §6.4) with the service secret.</summary>
public sealed class CollabClient(HttpClient http)
{
    public Task<HttpResponseMessage> OpAsync(Guid pageId, object body, CancellationToken ct) =>
        http.PostAsJsonAsync($"/documents/{pageId}/ops", body, CollabEndpoints.Json, ct);

    /// <summary>The live snapshot when collab has the document loaded; null otherwise (or when collab is unreachable).</summary>
    public async Task<JsonElement?> SnapshotAsync(Guid pageId, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync($"/documents/{pageId}", ct);
            return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
        }
        catch (HttpRequestException) { return null; }
    }
}

/// <summary>
/// Page writes through collab, durable (FR-RT-07, AT-45). The op is recorded as pending before collab sees it:
/// <list type="bullet">
/// <item>collab persists the revision holding the op before it answers, and that persist marks the op done
/// (<see cref="CollabStore"/>), so a lost response or a collab restart can't leave it pending once it's applied;</item>
/// <item>a retry by the same actor and credential returns the stored result, or re-sends an op that never persisted
/// (collab never applies an op twice);</item>
/// <item>another payload under the same op_id is <c>op_id_reused</c>; another actor's op_id is <c>not_found</c>.</item>
/// </list>
/// Agent credentials also need the <c>write</c> scope; the access rules apply the space AI policy to them (FR-WS-03, T030).
/// </summary>
public sealed class PageOpsService(AtharDbContext db, CollabClient collab, PageService pages)
{
    /// <summary>append, replace, restore or anchorComment on an existing page.</summary>
    public Task<PageOpResult> RunAsync(Actor actor, Guid pageId, string opId, PageOp op, CancellationToken ct = default)
    {
        var action = op.Kind == "anchorComment" ? PageAction.Comment : PageAction.Edit;
        return DurableAsync(actor, pageId, opId, op, () => RequireAsync(actor, pageId, action, ct), prepare: null, ct);
    }

    /// <summary>
    /// `create_page`: a new page with Markdown content. The op is recorded under a page id chosen up front, and the page row
    /// is created after it, so a crash or a retry never makes a second page.
    /// </summary>
    public async Task<PageOpResult> CreatePageAsync(Actor actor, Guid spaceId, Guid? parentId, string title, string markdown, string opId, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var existing = await db.Operations.AsNoTracking().Where(o => o.OpId == opId).Select(o => o.TargetId).SingleOrDefaultAsync(ct);
        var pageId = existing is not null && Guid.TryParse(existing, out var known) ? known : Guid.CreateVersion7();
        var wiki = new WikiActor(actor.UserId, actor.OrgId);
        return await DurableAsync(actor, pageId, opId, new PageOp("create", markdown, SpaceId: spaceId, ParentId: parentId, Title: title), async () =>
        {
            if (await db.Pages.AnyAsync(p => p.Id == pageId, ct)) return;
            var caller = await CallerAsync(actor, PageAction.Edit, ct);
            await pages.RequireCanPlaceAsync(wiki, spaceId, parentId, ct);
            // No page yet for the access rule to judge, so the space's AI policy is checked here (FR-WS-03).
            if (caller.Agent && !await db.Spaces.AsNoTracking().AnyAsync(s => s.Id == spaceId && s.AiPolicy.Read && s.AiPolicy.Write, ct))
                throw new AtharError("policy_denied", "This space's AI policy doesn't allow agent writes.");
        }, prepare: async () =>
        {
            if (!await db.Pages.AnyAsync(p => p.Id == pageId, ct)) await pages.CreateAsync(wiki, spaceId, parentId, title, ct, id: pageId);
        }, ct);
    }

    /// <summary>FR-PG-05 restore: the version's content becomes the live content through the ops path, as current_revision + 1.</summary>
    public async Task<PageOpResult> RestoreAsync(Actor actor, Guid pageId, Guid versionId, string opId, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var version = await db.PageVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId && v.PageId == pageId, ct)
            ?? throw new AtharError("not_found", "Version not found.");
        var json = JsonSerializer.Deserialize<JsonElement>(version.ContentJson ?? """{"type":"doc"}""");
        return await RunAsync(actor, pageId, opId, new PageOp("restore", VersionId: versionId, Json: json), ct);
    }

    /// <summary>`get_page`: from collab's live document when it's loaded, otherwise from the database with the stored epoch (FR-RT-06).</summary>
    public async Task<PageView> GetPageAsync(Actor actor, Guid pageId, string format = "markdown", CancellationToken ct = default)
    {
        await RequireAsync(actor, pageId, PageAction.Read, ct);
        var page = await db.Pages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pageId && p.DeletedAt == null, ct)
            ?? throw new AtharError("not_found", "Page not found.");
        var space = await db.Spaces.Where(s => s.Id == page.SpaceId).Select(s => s.Name).SingleAsync(ct);
        // Only ancestors the caller may read: a draft (U-1) or restricted parent shows as "…", not its title (T119).
        var path = await db.PagesFor(actor.Caller).Where(p => page.AncestorIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Title, ct);
        string[] titles = [.. page.AncestorIds.Select(id => path.GetValueOrDefault(id, "…"))];

        var json = format == "json";
        if (await collab.SnapshotAsync(pageId, ct) is { } live)
            return new(pageId, page.Title, json ? live.GetProperty("json").GetRawText() : live.GetProperty("markdown").GetString()!,
                live.GetProperty("live_token").GetString()!, live.GetProperty("revision").GetInt64(), space, titles, page.UpdatedAt);
        return new(pageId, page.Title, json ? page.ContentJson ?? """{"type":"doc"}""" : page.ContentMd ?? "",
            $"{page.LiveEpoch}:{page.LiveSeq}", page.Revision, space, titles, page.UpdatedAt);
    }

    /// <summary>The outcome of one of the actor's durable writes, page or not.</summary>
    public async Task<OperationStatusView> StatusAsync(Actor actor, string opId, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var row = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.OpId == opId, ct);
        if (row is null || row.ActorUserId != actor.UserId || row.CredentialId != actor.CredentialId)
            throw new AtharError("not_found", "Unknown op_id.");
        if (row.TargetKind == OperationTarget.Page && Guid.TryParse(row.TargetId, out var page))
            await RequireAsync(actor, page, PageAction.Read, ct);
        var result = row.Result is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(row.Result);
        var failed = row.Status == OperationStatus.Failed;
        return new(row.OpId, row.Status.ToString().ToLowerInvariant(), row.TargetKind.ToString().ToLowerInvariant(), row.TargetId,
            failed ? null : result, failed ? result!.Value.GetProperty("error").GetString() : null);
    }

    /// <summary>Record, then send; or replay what's stored. <paramref name="prepare"/> runs after the op is recorded (create's page row).</summary>
    private async Task<PageOpResult> DurableAsync(Actor actor, Guid pageId, string opId, PageOp op, Func<Task> authorize, Func<Task>? prepare, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opId)) throw new AtharError("invalid_input", "op_id is required.");
        db.CurrentOrgId = actor.OrgId;
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { pageId, op })));

        var row = await db.Operations.SingleOrDefaultAsync(o => o.OpId == opId, ct);
        if (row is null)
        {
            await authorize();
            row = new Operation
            {
                OpId = opId, ActorUserId = actor.UserId, CredentialId = actor.CredentialId, RequestHash = hash,
                TargetKind = OperationTarget.Page, TargetId = pageId.ToString(), Status = OperationStatus.Pending,
            };
            db.Operations.Add(row);
            db.Audit(actor.OrgId, actor.UserId, $"page.{op.Kind}", "page", pageId,
                after: new { op_id = opId, version_id = op.VersionId, thread_id = op.ThreadId }, credentialId: actor.CredentialId);
            try
            {
                await db.SaveChangesAsync(ct); // committed before collab is called
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // A concurrent duplicate recorded it first: treat this call as its retry.
                db.ChangeTracker.Clear();
                row = await db.Operations.SingleAsync(o => o.OpId == opId, ct);
            }
        }
        Owned(actor, row, hash);

        switch (row.Status)
        {
            case OperationStatus.Done:
                await RequireAsync(actor, pageId, PageAction.Read, ct); // access is re-checked before a stored result is returned
                return Result(row);
            case OperationStatus.Failed:
                throw Error(row);
            default:
                // Pending: never sent, sent and lost, or still in flight. Collab applies it at most once.
                await authorize();
                if (prepare is not null) await prepare();
                return await SendAsync(actor, row, pageId, opId, op, ct);
        }
    }

    private async Task<PageOpResult> SendAsync(Actor actor, Operation row, Guid pageId, string opId, PageOp op, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await collab.OpAsync(pageId, new
            {
                op_id = opId, kind = op.Kind, user = actor.UserId, credential = actor.CredentialId, expected_live_token = op.ExpectedLiveToken,
                markdown = op.Markdown, json = op.Json, thread_id = op.ThreadId, from = op.From, to = op.To, quote = op.Quote, anchor_hint = op.AnchorHint,
            }, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return await SettledOrPending(row, ct);
        }
        using (res)
        {
            if (res.StatusCode == HttpStatusCode.OK)
            {
                var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
                var result = new PageOpResult(pageId, body.GetProperty("persisted_revision").GetInt64(), body.GetProperty("live_token").GetString()!,
                    [.. body.GetProperty("orphaned_threads").EnumerateArray().Select(t => t.GetGuid())]);
                return await DoneAsync(row, result, ct);
            }
            var error = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            var code = error.TryGetProperty("error", out var c) ? c.GetString() : null;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            (string Code, string Message)? failure = (res.StatusCode, code) switch
            {
                (HttpStatusCode.Conflict, "live_token_moved") => ("conflict_revision", $"The page changed; the live token is now {error.GetProperty("live_token").GetString()}."),
                (HttpStatusCode.NotFound, _) => ("not_found", "Page not found."),
                (HttpStatusCode.UnprocessableEntity, _) => (code ?? "invalid_input", message ?? "The operation can't be applied."),
                (HttpStatusCode.BadRequest, _) => ("invalid_input", message ?? "Invalid operation."),
                _ => null,
            };
            // Anything else (a 5xx) may have applied without persisting yet: leave it pending.
            if (failure is null) return await SettledOrPending(row, ct);
            await FailAsync(row, failure.Value.Code, failure.Value.Message, ct);
            throw new AtharError(failure.Value.Code, failure.Value.Message);
        }
    }

    /// <summary>The response was lost: the op is done if collab's persist recorded it, otherwise still pending.</summary>
    private async Task<PageOpResult> SettledOrPending(Operation row, CancellationToken ct)
    {
        await db.Entry(row).ReloadAsync(ct);
        if (row.Status == OperationStatus.Done) return Result(row);
        throw new AtharError("pending", $"The operation isn't persisted yet. Retry with the same op_id, or poll /api/v1/operations/{row.OpId}.");
    }

    private async Task<PageOpResult> DoneAsync(Operation row, PageOpResult result, CancellationToken ct)
    {
        // The persist may have marked it done already; collab's own answer carries the op's exact live token and orphans.
        await db.Entry(row).ReloadAsync(ct);
        row.Status = OperationStatus.Done;
        row.Result = JsonSerializer.Serialize(result, CollabEndpoints.Json);
        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task FailAsync(Operation row, string code, string message, CancellationToken ct)
    {
        row.Status = OperationStatus.Failed;
        row.Result = JsonSerializer.Serialize(new { error = code, message });
        await db.SaveChangesAsync(ct);
    }

    private static void Owned(Actor actor, Operation row, string hash)
    {
        // Another actor's op_id looks like nothing at all; the same actor reusing it for another payload is an error.
        if (row.ActorUserId != actor.UserId || row.CredentialId != actor.CredentialId) throw new AtharError("not_found", "Unknown op_id.");
        if (row.RequestHash != hash) throw new AtharError("op_id_reused", "This op_id was already used with a different payload.");
    }

    /// <summary>
    /// The access-rule caller for this actor (T030): null when the user is inactive or the credential revoked or expired
    /// (AT-08). An agent credential needs the <c>write</c> scope for anything but reading (FR-AG-01).
    /// </summary>
    private async Task<Caller> CallerAsync(Actor actor, PageAction action, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var caller = await db.ActorAsync(actor.UserId, actor.CredentialId, ct) ?? throw new AtharError("unauthorized", "The user is inactive or the credential is revoked.");
        if (action != PageAction.Read && caller.Agent && !actor.Scopes.Contains("write")) throw new AtharError("forbidden", "The token lacks the 'write' scope.");
        return caller;
    }

    /// <summary>Page rights through Athar.Domain.Access, which also applies the space AI policy to agents (FR-WS-03).</summary>
    private async Task RequireAsync(Actor actor, Guid pageId, PageAction action, CancellationToken ct)
    {
        var caller = await CallerAsync(actor, action, ct);
        if (await db.CanAsync(caller, pageId, action, ct)) return;
        // The person could, so it's the space's AI policy that bars their agent.
        if (caller.Agent && await db.CanAsync(caller with { Agent = false, External = false }, pageId, action, ct))
            throw new AtharError("policy_denied", "This space's AI policy doesn't allow this for agents.");
        // Someone who can read the page but not do this gets forbidden; anyone else learns nothing.
        throw action != PageAction.Read && await db.CanAsync(caller, pageId, PageAction.Read, ct)
            ? new AtharError("forbidden", "You can't change this page.")
            : new AtharError("not_found", "Page not found.");
    }

    private static PageOpResult Result(Operation row) => JsonSerializer.Deserialize<PageOpResult>(row.Result!, CollabEndpoints.Json)!;

    private static AtharError Error(Operation row)
    {
        var e = JsonSerializer.Deserialize<JsonElement>(row.Result!);
        return new AtharError(e.GetProperty("error").GetString()!, e.GetProperty("message").GetString()!);
    }
}

public static class OperationEndpoints
{
    /// <summary>`GET /api/v1/operations/{op_id}` (docs/06 §6.3): the same actor polls a write that answered <c>pending</c>.</summary>
    public static void MapOperationEndpoints(this RouteGroupBuilder v1) =>
        v1.MapGet("/operations/{opId}", (string opId, HttpContext http, PageOpsService ops, CancellationToken ct) =>
            Api.Rest.Run(() => ops.StatusAsync(Actor.From(http), opId, ct)));
}
