using System.Text.Json;
using System.Text.Json.Serialization;
using Athar.Web.Mcp;
using Athar.Web.Services;

namespace Athar.Web.Api;

public sealed record StartSessionBody(
    [property: JsonPropertyName("client")] string Client,
    [property: JsonPropertyName("external_session_id")] string ExternalSessionId,
    [property: JsonPropertyName("repo_remote")] string? RepoRemote);
public sealed record SaveChatBody(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary_md")] string SummaryMd,
    [property: JsonPropertyName("key_points")] string[]? KeyPoints,
    [property: JsonPropertyName("project_id")] Guid? ProjectId,
    [property: JsonPropertyName("op_id")] string OpId);
public sealed record CommitRecordBody([property: JsonPropertyName("index")] int Index, [property: JsonPropertyName("data")] JsonElement Data);
public sealed record CommitBody(
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("from_index")] int FromIndex,
    [property: JsonPropertyName("to_index")] int ToIndex,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("records")] CommitRecordBody[] Records);

/// <summary><c>/api/v1/sessions</c> and <c>/api/v1/me/capture-settings</c> (docs/06 §6.3), what the capture hook calls. Reads are owner-only.</summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this RouteGroupBuilder v1)
    {
        var api = v1.MapGroup("/sessions");
        api.MapPost("/", (StartSessionBody body, SessionService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.StartAsync(Actor.From(http), body.Client, body.ExternalSessionId, body.RepoRemote, ct)));
        api.MapPost("/{id:guid}/commit", (Guid id, CommitBody body, SessionService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.CommitAsync(Actor.From(http), id,
                new(body.CommitId, body.FromIndex, body.ToIndex, body.Skipped, [.. (body.Records ?? []).Select(r => new CommitRecord(r.Index, r.Data))]), ct)));
        // FR-AG-09 (T139): the REST twin of MCP save_chat.
        api.MapPost("/chat", (SaveChatBody b, SessionService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.SaveChatAsync(Actor.From(http), b.Title, b.SummaryMd, b.KeyPoints, b.ProjectId, b.OpId, ct)));
        api.MapGet("/", (SessionService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.ListAsync(Actor.From(http), ct)));
        // T138: Delete now (the transcript and unreviewed candidates), owner only.
        api.MapDelete("/{id:guid}", (Guid id, SessionService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.DeleteTranscriptAsync(Actor.From(http), id, ct)));
        api.MapGet("/{id:guid}", (Guid id, SessionService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.GetAsync(Actor.From(http), id, ct)));

        // I-1 (T135): the caller's own capture settings; the hook reads them at session start.
        v1.MapGet("/me/capture-settings", (CaptureSettingsService s, HttpContext http, CancellationToken ct) => Rest.Run(() => s.GetAsync(Actor.From(http), ct)));
        v1.MapPut("/me/capture-settings", (CaptureSettingsView body, CaptureSettingsService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.PutAsync(Actor.From(http), body, ct)));
    }
}
