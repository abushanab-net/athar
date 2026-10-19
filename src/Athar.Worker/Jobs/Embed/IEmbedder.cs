namespace Athar.Worker.Jobs.Embed;

/// <summary>Document embeddings for the index: the model gateway in the pilot, the local Ollama as the fallback (docs/03 D8).</summary>
public interface IEmbedder
{
    /// <summary>What <c>chunk_embeddings.model_id</c> records for these vectors.</summary>
    string ModelId { get; }

    /// <summary>One 1024-value, L2-normalized vector per text, in order. <see cref="JobDeferred"/> while the model isn't reachable yet.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
