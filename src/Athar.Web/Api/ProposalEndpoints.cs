using System.Text.Json.Serialization;
using Athar.Web.Mcp;
using Athar.Web.Services;

namespace Athar.Web.Api;

// Request bodies are snake_case like the rest of the contract.
public sealed record ApproveBody([property: JsonPropertyName("base_revision")] int? BaseRevision);
public sealed record RejectBody([property: JsonPropertyName("note")] string Note);
public sealed record RebaseBody(string? Title, [property: JsonPropertyName("body_md")] string? BodyMd);

/// <summary>Proposal review over REST (docs/06 §6.3). Knowledge owners only; the reviewer is re-checked inside the transaction.</summary>
public static class ProposalEndpoints
{
    public static void MapProposalEndpoints(this RouteGroupBuilder v1)
    {
        var api = v1.MapGroup("/proposals");
        // The queue: knowledge owners get every open proposal, others their own. Optional status filter.
        api.MapGet("/", (string? status, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.ProposalsAsync(Actor.From(http), status, ct)));
        api.MapPost("/{id:guid}/approve", (Guid id, ApproveBody? body, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.ApproveAsync(Actor.From(http), id, body?.BaseRevision, ct)));
        api.MapPost("/{id:guid}/reject", (Guid id, RejectBody body, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.RejectAsync(Actor.From(http), id, body.Note, ct)));
        api.MapPost("/{id:guid}/rebase", (Guid id, RebaseBody? body, MemoryService s, HttpContext http, CancellationToken ct) =>
            Rest.Run(() => s.RebaseAsync(Actor.From(http), id, body?.Title, body?.BodyMd, ct)));
    }
}
