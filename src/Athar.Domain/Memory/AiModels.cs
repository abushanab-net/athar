namespace Athar.Domain.Memory;

public enum ApiKeyChange { Keep, Replace, Clear }
public sealed record AiSettingsWrite(long ExpectedRevision, string BaseUrl, string EmbeddingModel,
    string QueryModel, string? VisionModel, ApiKeyChange KeyChange, string? NewApiKey)
{
    public override string ToString() => nameof(AiSettingsWrite);
}

/// <summary>
/// <c>Athar:Ai</c>: the OpenAI-compatible model gateway (docs/03 D8, Phase P: the on-prem model gateway). Without a <see cref="BaseUrl"/>,
/// the hosts fall back to the local Ollama models. The key is a secret (AppHost parameter
/// <c>ai-key</c>, user-secrets in dev, <c>.env</c> on the server); never commit it.
/// </summary>
public sealed class AiOptions
{
    /// <summary>e.g. <c>http://192.168.1.10:8080/v1</c>.</summary>
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string ChatModel { get; set; } = "Qwen3.6-35B-A3B-NVFP4";
    public string? QueryModel { get; set; }
    public string? VisionModel { get; set; }
    public string EmbeddingModel { get; set; } = "Qwen3-Embedding-4B";
    /// <summary>Stored dimensions (<c>vector(1024)</c>): only Qwen3-Embedding supports truncation of longer vectors.</summary>
    public int Dimensions { get; set; } = AiModels.Dimensions;
    /// <summary>Qwen3-Embedding is instruction-aware: queries (not documents) get this task line.</summary>
    public string QueryInstruction { get; set; } = "Given a question from a software team, retrieve the notes and wiki pages that answer it";

    /// <summary>A base URL selects the gateway; then Ollama isn't used (or needed) at all.</summary>
    public bool Gateway => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Fails fast on a half-configured gateway instead of falling back silently.</summary>
    public AiOptions Validated() => Gateway && string.IsNullOrWhiteSpace(ApiKey)
        ? throw new InvalidOperationException("Athar:Ai:BaseUrl is set but Athar:Ai:ApiKey isn't (the AppHost parameter ai-key, or the server's .env).")
        : this;
}

public static class AiModels
{
    public const int Dimensions = 1024;
    /// <summary>The local fallback's embedding model and its <c>chunk_embeddings.model_id</c> (D8 benchmark).</summary>
    public const string OllamaEmbeddingId = "bge-m3";

    /// <summary>
    /// <c>chunk_embeddings.model_id</c> for a gateway model at a stored size. Search reads only the current id, and the
    /// worker re-embeds every chunk that lacks it, so changing the model or the size re-embeds everything.
    /// </summary>
    public static string EmbeddingId(AiOptions o) => o.Gateway ? $"{o.EmbeddingModel}@{o.Dimensions}" : OllamaEmbeddingId;

    /// <summary>
    /// Matryoshka truncation: the first <paramref name="dims"/> values, L2-normalized again so cosine distances stay comparable.
    /// Qwen3-Embedding-4B returns 2560 values and rejects the <c>dimensions</c> parameter.
    /// </summary>
    public static float[] Fit(float[] v, int dims)
    {
        if (v.Length < dims || v.Any(x => !float.IsFinite(x))) throw new InvalidOperationException("Invalid embedding dimensions or values.");
        var fitted = v[..dims];
        var norm = Math.Sqrt(fitted.Sum(x => (double)x * x));
        if (norm <= 0) throw new InvalidOperationException("An embedding must have a nonzero norm.");
        for (var i = 0; i < fitted.Length; i++) fitted[i] = (float)(fitted[i] / norm);
        return fitted;
    }
}
