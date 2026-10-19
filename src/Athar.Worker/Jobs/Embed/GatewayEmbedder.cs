using Athar.Data;
using Athar.Domain.Memory;

namespace Athar.Worker.Jobs.Embed;

public sealed class GatewayEmbedder(AiSnapshot snapshot) : IEmbedder
{
    public GatewayEmbedder(AiOptions options) : this(new AiSnapshot(options.BaseUrl, options.EmbeddingModel,
        options.QueryModel ?? options.ChatModel, options.VisionModel, options.ApiKey, false, true,
        AiModels.EmbeddingId(options), options.QueryInstruction)) { }

    public string ModelId => snapshot.EmbeddingIdentity;
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        try { return await new AiGatewayClient(snapshot).EmbedAsync(texts, false, ct, TimeSpan.FromMinutes(5)); }
        catch (AiProviderException e) when (e.Retryable) { throw new JobDeferred(e.Code, TimeSpan.FromMinutes(1)); }
    }
}
