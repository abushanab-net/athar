using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;

namespace Athar.Worker.Jobs.Extract;

public sealed class GatewayExtractor(AiSnapshot snapshot, ILogger<GatewayExtractor> log) : ICandidateExtractor
{
    public GatewayExtractor(AiOptions options, ILogger<GatewayExtractor> log) : this(new AiSnapshot(options.BaseUrl,
        options.EmbeddingModel, options.QueryModel ?? options.ChatModel, options.VisionModel, options.ApiKey, false, true,
        AiModels.EmbeddingId(options), options.QueryInstruction), log) { }

    public async Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, CancellationToken ct)
    {
        try
        {
            var text = await new AiGatewayClient(snapshot).ChatAsync(snapshot.QueryModel,
                [new { role = "system", content = ExtractionPrompt.System + ExtractionPrompt.LanguageHint(transcript) },
                 new { role = "user", content = transcript }], ExtractionPrompt.Schema, 4096, ct);
            var parsed = JsonSerializer.Deserialize<Output>(text, IndexJobs.Json)?.Candidates
                ?? throw new AiProviderException("invalid_response");
            if (parsed.Any(c => c is null || c.Title is null || c.BodyMd is null || c.Type is null || c.Scope is null))
                throw new AiProviderException("invalid_response");
            var kept = parsed.Where(c => ExtractionPrompt.InSessionLanguage(c, transcript)).ToList();
            if (kept.Count < parsed.Length) log.LogWarning("Dropped {Count} candidate(s) written in another language", parsed.Length - kept.Count);
            return kept;
        }
        catch (JsonException) { throw new AiProviderException("invalid_response"); }
        catch (AiProviderException e) when (e.Retryable) { throw new JobDeferred(e.Code, TimeSpan.FromMinutes(1)); }
    }
    private sealed record Output(ExtractedCandidate[]? Candidates);
}
