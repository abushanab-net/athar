using Athar.Data;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;

namespace Athar.Worker.Jobs;

/// <summary>Per-operation settings and lifetime; shared local transports never own admission.</summary>
public sealed class AiOperationClients(AiSettingsResolver resolver, IServiceProvider services, ILogger<GatewayExtractor> log)
{
    public async Task<(IEmbedder Embedder, ICandidateExtractor Extractor)> ResolveAsync(AtharDbContext work, CancellationToken ct)
    {
        var operation = await resolver.CaptureAsync(work, ct);
        AiOperationPolicy.Require(operation);
        var embedder = await ResolveEmbedderAsync(operation, ct);
        try
        {
            var s = await resolver.ResolveAsync(operation, AiPurpose.Extraction, ct);
            return (embedder, s.Gateway ? new GatewayExtractor(s, log) : new LocalExtractor(services.GetRequiredService<OllamaExtractor>(), s, operation));
        }
        catch { await embedder.DisposeAsync(); throw; }
    }
    public async Task<IEmbedder> ResolveEmbedderAsync(AtharDbContext work, CancellationToken ct) =>
        await ResolveEmbedderAsync(await resolver.CaptureAsync(work, ct), ct);

    private async Task<IEmbedder> ResolveEmbedderAsync(AiModelOperation operation, CancellationToken ct)
    {
        AiOperationPolicy.Require(operation);
        var s = await resolver.ResolveAsync(operation, AiPurpose.Embedding, ct);
        return s.Gateway ? new GatewayEmbedder(s) : new LocalEmbedder(services.GetRequiredService<OllamaEmbedder>(), s, operation);
    }
    private sealed class LocalEmbedder(OllamaEmbedder inner, AiSnapshot snapshot, AiModelOperation operation) : IEmbedder
    {
        public string ModelId => snapshot.EmbeddingIdentity;
        public ValueTask DisposeAsync() => snapshot.DisposeAsync();
        public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            await snapshot.CheckAsync(ct);
            var result = await inner.EmbedAsync(texts, operation, ct);
            await snapshot.CheckAsync(ct); return result;
        }
    }
    private sealed class LocalExtractor(OllamaExtractor inner, AiSnapshot snapshot, AiModelOperation operation) : ICandidateExtractor
    {
        public ValueTask DisposeAsync() => snapshot.DisposeAsync();
        public async Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, CancellationToken ct)
        {
            await snapshot.CheckAsync(ct);
            var result = await inner.ExtractAsync(transcript, operation, ct);
            await snapshot.CheckAsync(ct); return result;
        }
    }
}
