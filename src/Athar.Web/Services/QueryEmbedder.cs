using System.Net.Http.Json;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;

namespace Athar.Web.Services;

/// <summary>One immutable configuration per search or recall, including its ranking top-ups.</summary>
public sealed class QueryEmbedder(IConfiguration config, ILogger<QueryEmbedder> log, AiSettingsResolver? resolver = null)
{
    private readonly AiOptions environment = config.GetSection("Athar:Ai").Get<AiOptions>() ?? new();
    public string ModelId => AiModels.EmbeddingId(environment);
    public Task<AiSnapshot> SnapshotAsync(Guid org, CancellationToken ct) => resolver is not null ? resolver.ResolveAsync(org, ct)
        : Task.FromResult(new AiSnapshot(environment.BaseUrl, environment.Gateway ? environment.EmbeddingModel : AiModels.OllamaEmbeddingId,
            environment.QueryModel ?? environment.ChatModel, environment.VisionModel, environment.ApiKey, false, true, ModelId, environment.QueryInstruction));

    public async Task<float[]?> EmbedAsync(string query, CancellationToken ct) =>
        await EmbedAsync(await SnapshotAsync(Guid.Empty, ct), query, ct);

    public async Task<float[]?> EmbedAsync(AiSnapshot snapshot, string query, CancellationToken ct)
    {
        try
        {
            if (snapshot.Gateway) return (await new AiGatewayClient(snapshot).EmbedAsync([query], true, ct, TimeSpan.FromSeconds(5)))[0];
            if (config.GetConnectionString("ollama") is not { } cs) return null;
            var endpoint = cs.Split(';').Select(p => p.Trim()).First(p => p.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))["Endpoint=".Length..];
            using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.PostAsJsonAsync("/api/embed", new { model = AiModels.OllamaEmbeddingId, input = new[] { query } }, ct);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var data = body.RootElement.GetProperty("embeddings");
            if (data.GetArrayLength() != 1) return null;
            var vector = data[0].EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return vector.Length == AiModels.Dimensions ? AiModels.Fit(vector, AiModels.Dimensions) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is AiProviderException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            log.LogWarning("Query embedding unavailable; search is degraded.");
            return null;
        }
    }
}
