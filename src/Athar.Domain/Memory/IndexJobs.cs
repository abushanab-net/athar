using System.Text.Json;
using Athar.Domain.Model;

namespace Athar.Domain.Memory;

/// <summary>
/// The search-index jobs (docs/03 §7). Insert them in the same transaction as the change that triggers them; the
/// worker discards a job whose owner has moved on.
/// </summary>
public static class IndexJobs
{
    public const string EmbedPage = "embed-page";
    public const string EmbedVectors = "embed-vectors";
    public const string ExtractSession = "extract-session";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary><c>embed-page</c> payload (enqueued by the collab persist, docs/03 §4.1).</summary>
    public sealed record PagePayload(Guid PageId, long Revision);

    /// <summary><c>embed-vectors</c> payload: an owner revision whose chunks need vectors. <c>owner_kind</c> is a <see cref="ChunkOwner"/> name.</summary>
    public sealed record VectorsPayload(string OwnerKind, Guid OwnerId, long Revision, string? EmbeddingIdentity = null);

    /// <summary><c>extract-session</c> payload: a session's records up to <c>acked_index</c> are ready for extraction (FR-MM-06).</summary>
    public sealed record SessionPayload(Guid SessionId, int AckedIndex);

    /// <summary>The <c>extract-session</c> job once a commit moves a session's acknowledged index; idempotent per index.</summary>
    public static Job Extract(Guid sessionId, int ackedIndex, DateTimeOffset now) => new()
    {
        Kind = ExtractSession, Key = $"{sessionId}:{ackedIndex}", RunAfter = now,
        Payload = JsonSerializer.Serialize(new SessionPayload(sessionId, ackedIndex), Json),
    };

    /// <summary>The <c>embed-vectors</c> job for an owner revision; idempotent by (kind, key).</summary>
    public static Job Vectors(ChunkOwner kind, Guid ownerId, long revision, DateTimeOffset now, string? embeddingIdentity = null)
    {
        var name = kind.ToString().ToLowerInvariant();
        return new()
        {
            Kind = EmbedVectors, Key = $"{name}:{ownerId}:{revision}" + (embeddingIdentity is null ? "" : $":{embeddingIdentity}"), RunAfter = now,
            Payload = JsonSerializer.Serialize(new VectorsPayload(name, ownerId, revision, embeddingIdentity), Json),
        };
    }
}
