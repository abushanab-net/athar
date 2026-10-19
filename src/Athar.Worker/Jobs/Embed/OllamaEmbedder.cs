using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athar.Worker.Jobs.Embed;

/// <summary>
/// bge-m3 embeddings from the local Ollama (docs/03 D8). Ollama can't pull by digest, so the worker pulls the tag once and
/// refuses to embed unless the local manifest digest is the pinned one.
/// </summary>
public sealed class OllamaEmbedder(IConfiguration config, ILogger<OllamaEmbedder> log) : IEmbedder
{
    public const string Model = "bge-m3";
    public string ModelId => Athar.Domain.Memory.AiModels.OllamaEmbeddingId; // chunk_embeddings.model_id
    public const string PinnedDigest = "7907646426070047a77226ac3e684fbbe8410524f7b4a74d02837e43f2146bab";
    public const int Dimensions = 1024;

    // ponytail: a plain HttpClient, not IHttpClientFactory: ServiceDefaults' resilience timeouts (10 s per attempt) would
    // cut off a model pull and CPU batch embeds. One client for the process.
    private readonly HttpClient _http = new() { BaseAddress = OllamaEndpoint(config), Timeout = TimeSpan.FromMinutes(15) };
    private Task? _ready;

    /// <summary>Pulls the model if needed and checks its digest, once. Throws until it's ready.</summary>
    public Task EnsureReadyAsync(CancellationToken ct) => _ready ??= PrepareAsync(ct);

    /// <summary>Embeddings for <paramref name="texts"/>, in order. <see cref="JobDeferred"/> while the model is still being pulled.</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var ready = EnsureReadyAsync(CancellationToken.None);
        if (!ready.IsCompleted) throw new JobDeferred("bge-m3 is still being pulled", TimeSpan.FromSeconds(15));
        if (ready.IsFaulted)
        {
            _ready = null; // retry the pull on a later job
            throw new JobDeferred($"bge-m3 isn't available: {ready.Exception!.InnerException!.Message}", TimeSpan.FromSeconds(30));
        }

        using var response = await _http.PostAsJsonAsync("/api/embed", new { model = Model, input = texts }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        if (body?.Embeddings is not { } vectors || vectors.Length != texts.Count || vectors.Any(v => v.Length != Dimensions))
            throw new InvalidOperationException("Ollama returned an unexpected embedding shape.");
        return [.. vectors.Select(v => Athar.Domain.Memory.AiModels.Fit(v, Dimensions))];
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        var digest = await DigestAsync(ct);
        if (digest is null)
        {
            log.LogInformation("Pulling {Model} into Ollama", Model);
            using var pull = await _http.PostAsJsonAsync("/api/pull", new { model = Model, stream = false }, ct);
            pull.EnsureSuccessStatusCode();
            digest = await DigestAsync(ct);
        }
        if (digest != PinnedDigest)
            throw new InvalidOperationException($"{Model} digest {digest ?? "missing"} isn't the pinned {PinnedDigest} (docs/03 D8).");
        log.LogInformation("{Model} ready ({Digest})", Model, digest[..12]);
    }

    private async Task<string?> DigestAsync(CancellationToken ct)
    {
        var tags = await _http.GetFromJsonAsync<TagsResponse>("/api/tags", ct);
        return tags?.Models.FirstOrDefault(m => m.Name is $"{Model}:latest" or Model)?.Digest?.Replace("sha256:", "");
    }

    // ConnectionStrings:ollama is "Endpoint=http://host:port" (Aspire's Ollama resource).
    internal static Uri OllamaEndpoint(IConfiguration config)
    {
        var cs = config.GetConnectionString("ollama") ?? throw new InvalidOperationException("ConnectionStrings:ollama is not set.");
        return new Uri(cs.Split(';').Select(p => p.Trim()).First(p => p.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))["Endpoint=".Length..]);
    }

    private sealed record EmbedResponse([property: JsonPropertyName("embeddings")] float[][]? Embeddings);
    private sealed record TagsResponse([property: JsonPropertyName("models")] TagModel[] Models);
    private sealed record TagModel([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("digest")] string? Digest);
}
