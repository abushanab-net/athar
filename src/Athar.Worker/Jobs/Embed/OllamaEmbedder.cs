using Athar.Data;
using Athar.Domain.Model;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athar.Worker.Jobs.Embed;

/// <summary>
/// bge-m3 embeddings from the local Ollama (docs/03 D8). Ollama can't pull by digest, so the worker pulls the tag once and
/// refuses to embed unless the local manifest digest is the pinned one.
/// </summary>
public sealed class OllamaEmbedder(IConfiguration config, ILogger<OllamaEmbedder> log, HttpMessageHandler? testTransport = null) : IEmbedder
{
    public const string Model = "bge-m3";
    public string ModelId => Athar.Domain.Memory.AiModels.OllamaEmbeddingId; // chunk_embeddings.model_id
    public const string PinnedDigest = "7907646426070047a77226ac3e684fbbe8410524f7b4a74d02837e43f2146bab";
    public const int Dimensions = 1024;

    // ponytail: a plain HttpClient, not IHttpClientFactory: ServiceDefaults' resilience timeouts (10 s per attempt) would
    // cut off a model pull and CPU batch embeds. One client for the process.
    private readonly HttpClient _http = testTransport is null ? new() { BaseAddress = OllamaEndpoint(config), Timeout = TimeSpan.FromMinutes(15) } : new(testTransport, disposeHandler: false) { BaseAddress = OllamaEndpoint(config), Timeout = TimeSpan.FromMinutes(15) };
    public Task EnsureReadyAsync(CancellationToken ct) =>
        PrepareAsync(_ => Task.CompletedTask, AiCallLease.UnrestrictedAsync, ct);
    public Task EnsureReadyAsync(AiModelOperation operation, CancellationToken ct)
    { Require(operation); return PrepareAsync(operation.CheckAsync, operation.BeginCallAsync, ct); }

    public IEmbedder ForOperation(AiModelOperation operation) { Require(operation); return new OperationClient(this, operation); }
    private sealed class OperationClient(OllamaEmbedder owner, AiModelOperation operation) : IEmbedder
    {
        public string ModelId => owner.ModelId;
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) => owner.EmbedAsync(texts, operation, ct);
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        EmbedAdmittedAsync(texts, _ => Task.CompletedTask, AiCallLease.UnrestrictedAsync, ct);

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, AiModelOperation operation, CancellationToken ct)
    { Require(operation); return EmbedAdmittedAsync(texts, operation.CheckAsync, operation.BeginCallAsync, ct); }

    private static void Require(AiModelOperation operation)
    {
    }

    private async Task<float[][]> EmbedAdmittedAsync(IReadOnlyList<string> texts, Func<CancellationToken, Task> admission, Func<AiCallRequest, CancellationToken, Task<AiCallLease>> begin, CancellationToken ct)
    {
        await PrepareAsync(admission, begin, ct);
        await CheckAsync(admission, ct);
        using var doc = await AiBoundedRequest.SendJsonAsync(_http, "/api/embed", new { model = Model, input = texts },
            ModelRole.Embedding, Model, AiCallKind.Inference, begin, admission, ct);
        var body = doc.RootElement.Deserialize<EmbedResponse>();
        await CheckAsync(admission, ct);
        if (body?.Embeddings is not { } vectors || vectors.Length != texts.Count || vectors.Any(v => v.Length != Dimensions))
            throw new InvalidOperationException("Ollama returned an unexpected embedding shape.");
        return [.. vectors.Select(v => Athar.Domain.Memory.AiModels.Fit(v, Dimensions))];
    }

    private static Task CheckAsync(Func<CancellationToken, Task> admission, CancellationToken ct) => admission is not null ? admission(ct) :
        throw new ArgumentNullException(nameof(admission));

    private async Task PrepareAsync(Func<CancellationToken, Task> admission, Func<AiCallRequest, CancellationToken, Task<AiCallLease>> begin, CancellationToken ct)
    {
        var digest = await DigestAsync(admission, begin, ct);
        if (digest is null)
        {
            log.LogInformation("Pulling {Model} into Ollama", Model);
            await CheckAsync(admission, ct);
            using var pull = await AiBoundedRequest.SendJsonAsync(_http, "/api/pull", new { model = Model, stream = false },
                ModelRole.Embedding, Model, AiCallKind.Preparation, begin, admission, ct);
            await CheckAsync(admission, ct);
            digest = await DigestAsync(admission, begin, ct);
        }
        if (digest != PinnedDigest)
            throw new InvalidOperationException($"{Model} digest {digest ?? "missing"} isn't the pinned {PinnedDigest} (docs/03 D8).");
        log.LogInformation("{Model} ready ({Digest})", Model, digest[..12]);
    }

    private async Task<string?> DigestAsync(Func<CancellationToken, Task> admission, Func<AiCallRequest, CancellationToken, Task<AiCallLease>> begin, CancellationToken ct)
    {
        await CheckAsync(admission, ct);
        using var doc = await AiBoundedRequest.SendJsonAsync(_http, "/api/tags", null, ModelRole.Embedding, Model, AiCallKind.Metadata, begin, admission, ct);
        var tags = doc.RootElement.Deserialize<TagsResponse>();
        await CheckAsync(admission, ct);
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
