using Athar.Data;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;

namespace Athar.Worker.Jobs;

/// <summary>Both clients use exactly the same snapshot, including extraction similarity matching.</summary>
public sealed class AiOperationClients(AiSettingsResolver resolver, IServiceProvider services, ILogger<GatewayExtractor> log)
{
    public async Task<(IEmbedder Embedder, ICandidateExtractor Extractor)> ResolveAsync(Guid org, CancellationToken ct)
    {
        var s = await resolver.ResolveAsync(org, ct);
        return s.Gateway ? (new GatewayEmbedder(s), new GatewayExtractor(s, log))
            : (new LocalEmbedder(services.GetRequiredService<OllamaEmbedder>(), s.EmbeddingIdentity), services.GetRequiredService<OllamaExtractor>());
    }
    private sealed class LocalEmbedder(IEmbedder inner, string id) : IEmbedder
    {
        public string ModelId => id;
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) => inner.EmbedAsync(texts, ct);
    }
}
