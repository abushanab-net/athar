using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athar.Worker.Jobs.Embed;

namespace Athar.Worker.Jobs.Extract;

/// <summary>A memory candidate as the extractor proposes it. Scope and type are suggestions, checked by the job.</summary>
public sealed record ExtractedCandidate(string Title, string BodyMd, string Type, string Scope, string? Evidence = null);

/// <summary>Turns a session transcript into candidates (FR-MM-06). Production uses <see cref="OllamaExtractor"/>; tests substitute a fixed list.</summary>
public interface ICandidateExtractor
{
    Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, CancellationToken ct);
}

/// <summary>
/// Extraction with the local <c>qwen2.5:7b-instruct</c> (docs/03 D8), JSON output, temperature 0. The language rule is in
/// the system message: in the D8 benchmark, putting it only in the user prompt made the model answer Arabic sessions in Chinese.
/// </summary>
public sealed class OllamaExtractor(IConfiguration config, ILogger<OllamaExtractor> log) : ICandidateExtractor
{
    public const string Model = "qwen2.5:7b-instruct";
    public const string PinnedDigest = "845dbda0ea48ed749caafd9e6037047aa19acfcfd82e704d7ca97d631a0b697e";

    // ponytail: a plain HttpClient: CPU extraction takes 40–60 s per session (D8), past ServiceDefaults' resilience timeouts.
    private readonly HttpClient _http = new() { BaseAddress = OllamaEmbedder.OllamaEndpoint(config), Timeout = TimeSpan.FromMinutes(10) };

    private Task? _ready;

    public async Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, CancellationToken ct)
    {
        // Pulled on first use (4.7 GB), not at startup, so only hosts that capture sessions pay for it (T085 fix: before,
        // extraction waited forever on a fresh Ollama volume). Jobs defer while it downloads; a failed pull is retried.
        var ready = _ready ??= PrepareAsync(CancellationToken.None);
        if (!ready.IsCompleted) throw new JobDeferred($"{Model} is being pulled into Ollama", TimeSpan.FromSeconds(30));
        if (ready.IsFaulted)
        {
            _ready = null;
            throw new JobDeferred($"{Model} isn't available: {ready.Exception!.InnerException!.Message}", TimeSpan.FromMinutes(1));
        }

        using var response = await _http.PostAsJsonAsync("/api/chat", new
        {
            model = Model,
            stream = false,
            format = ExtractionPrompt.Schema,
            options = new { temperature = 0 },
            messages = new[] { new { role = "system", content = ExtractionPrompt.System + ExtractionPrompt.LanguageHint(transcript) }, new { role = "user", content = transcript } },
        }, ct);
        response.EnsureSuccessStatusCode();
        var chat = await response.Content.ReadFromJsonAsync<Chat>(ct);
        var parsed = JsonSerializer.Deserialize<Output>(chat?.Message?.Content ?? "{}", Json)?.Candidates ?? [];
        var kept = parsed.Where(c => ExtractionPrompt.InSessionLanguage(c, transcript)).ToList();
        if (kept.Count < parsed.Length) log.LogWarning("Dropped {Count} candidate(s) written in another language", parsed.Length - kept.Count);
        return kept;
    }

    /// <summary>Pulls the model if it's missing, then checks the pinned digest (docs/03 D8). Ollama can't pull by digest.</summary>
    private async Task PrepareAsync(CancellationToken ct)
    {
        if (await DigestAsync(ct) is null && config.GetValue("Athar:Extraction:AutoPull", true))
        {
            log.LogInformation("Pulling {Model} into Ollama", Model);
            using var pull = await _http.PostAsJsonAsync("/api/pull", new { model = Model, stream = false }, ct);
            pull.EnsureSuccessStatusCode();
        }
        var digest = await DigestAsync(ct);
        if (digest != PinnedDigest) throw new InvalidOperationException($"{Model} digest {digest ?? "missing"} isn't the pinned {PinnedDigest} (docs/03 D8).");
        log.LogInformation("{Model} ready ({Digest})", Model, digest[..12]);
    }

    private async Task<string?> DigestAsync(CancellationToken ct) =>
        (await _http.GetFromJsonAsync<Tags>("/api/tags", ct))?.Models.FirstOrDefault(m => m.Name == Model)?.Digest?.Replace("sha256:", "");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record Output(ExtractedCandidate[]? Candidates);
    private sealed record Chat([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
    private sealed record Tags([property: JsonPropertyName("models")] Tag[] Models);
    private sealed record Tag([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("digest")] string? Digest);
}
