using System.Net.Http.Json;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;

namespace Athar.Web.Services;

/// <summary>One immutable configuration per search or recall, including its ranking top-ups.</summary>
public sealed class QueryEmbedder(IConfiguration config, ILogger<QueryEmbedder> log, AiSettingsResolver? resolver = null)
{
    private readonly AiOptions environment = config.GetSection("Athar:Ai").Get<AiOptions>() ?? new();
    public string ModelId => AiModels.EmbeddingId(environment);
    public Task<AiSnapshot> SnapshotAsync(Guid org, CancellationToken ct) => resolver is not null ? resolver.ResolveAsync(org, AiPurpose.Embedding, ct)
        : Task.FromResult(new AiSnapshot(environment.BaseUrl, environment.Gateway ? environment.EmbeddingModel : AiModels.OllamaEmbeddingId,
            environment.QueryModel ?? environment.ChatModel, environment.VisionModel, environment.ApiKey, false, true, ModelId, environment.QueryInstruction)
            );

    public Task<AiModelOperation> CaptureAsync(AtharDbContext work, CancellationToken ct) => resolver is not null ? resolver.CaptureAsync(work, ct)
        : Task.FromResult(new AiModelOperation(work));


    public async Task<AiSnapshot> SnapshotAsync(AiModelOperation operation, CancellationToken ct)
    {
        await operation.CheckAsync(ct);
        var snapshot = resolver is not null ? await resolver.ResolveAsync(operation, AiPurpose.Embedding, ct)
            : new AiSnapshot(environment.BaseUrl, environment.Gateway ? environment.EmbeddingModel : AiModels.OllamaEmbeddingId,
                environment.QueryModel ?? environment.ChatModel, environment.VisionModel, environment.ApiKey, false, true, ModelId, environment.QueryInstruction);
        return snapshot with { Admission = operation.CheckAsync, CallAdmission = operation.BeginCallAsync };
    }

    public async Task<float[]?> EmbedAsync(string query, CancellationToken ct)
    {
        await using var snapshot = await SnapshotAsync(Guid.Empty, ct);
        return await EmbedAsync(snapshot, query, ct);
    }

    public async Task<float[]?> EmbedAsync(AiSnapshot snapshot, string query, CancellationToken ct)
    {
        await snapshot.CheckAsync(ct);
        try
        {
            if (snapshot.Gateway) return (await new AiGatewayClient(snapshot).EmbedAsync([query], true, ct, TimeSpan.FromSeconds(5)))[0];
            if (config.GetConnectionString("ollama") is not { } cs) return null;
            var endpoint = cs.Split(';').Select(p => p.Trim()).First(p => p.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))["Endpoint=".Length..];
            using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(5) };
            await snapshot.CheckAsync(ct);
            using var body = await AiBoundedRequest.SendJsonAsync(http, "/api/embed", new { model = AiModels.OllamaEmbeddingId, input = new[] { query } },
                ModelRole.Embedding, AiModels.OllamaEmbeddingId, AiCallKind.Inference, snapshot.BeginCallAsync, snapshot.CheckAsync, ct);
            await snapshot.CheckAsync(ct);
            var data = body.RootElement.GetProperty("embeddings");
            if (data.GetArrayLength() != 1) return null;
            var vector = data[0].EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return vector.Length == AiModels.Dimensions ? AiModels.Fit(vector, AiModels.Dimensions) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is AiProviderException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            await snapshot.CheckAsync(ct);
            log.LogWarning("Query embedding unavailable; search is degraded.");
            return null;
        }
    }
}
