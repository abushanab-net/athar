using Athar.Data;
using Athar.Domain.Model;
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
    ValueTask DisposeAsync() => ValueTask.CompletedTask;
    Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, CancellationToken ct);
}

/// <summary>
/// Extraction with the local <c>qwen2.5:7b-instruct</c> (docs/03 D8), JSON output, temperature 0. The language rule is in
/// the system message: in the D8 benchmark, putting it only in the user prompt made the model answer Arabic sessions in Chinese.
/// </summary>
public sealed class OllamaExtractor(IConfiguration config, ILogger<OllamaExtractor> log, HttpMessageHandler? testTransport = null) : ICandidateExtractor
{
    public const string Model = "qwen2.5:7b-instruct";
    public const string PinnedDigest = "845dbda0ea48ed749caafd9e6037047aa19acfcfd82e704d7ca97d631a0b697e";

    // ponytail: a plain HttpClient: CPU extraction takes 40–60 s per session (D8), past ServiceDefaults' resilience timeouts.
    private readonly HttpClient _http = testTransport is null ? new() { BaseAddress = OllamaEmbedder.OllamaEndpoint(config), Timeout = TimeSpan.FromMinutes(10) } : new(testTransport, disposeHandler: false) { BaseAddress = OllamaEmbedder.OllamaEndpoint(config), Timeout = TimeSpan.FromMinutes(10) };

    public Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, CancellationToken ct) =>
        AiOperationPolicy.Strict ? throw AiOperationPolicy.Missing() : ExtractAdmittedAsync(transcript, _ => Task.CompletedTask, AiCallLease.UnrestrictedAsync, ct);

    public Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, AiModelOperation operation, CancellationToken ct)
    {
        AiOperationPolicy.Require(operation);
        return ExtractAdmittedAsync(transcript, operation.CheckAsync, operation.BeginCallAsync, ct);
    }

    private async Task<IReadOnlyList<ExtractedCandidate>> ExtractAdmittedAsync(string transcript, Func<CancellationToken, Task> admission, Func<AiCallRequest, CancellationToken, Task<AiCallLease>> begin, CancellationToken ct)
    {
        await PrepareAsync(admission, begin, ct);
        await CheckAsync(admission, ct);
        using var doc = await AiBoundedRequest.SendJsonAsync(_http, "/api/chat", new
        {
            model = Model,
            stream = false,
            format = ExtractionPrompt.Schema,
            options = new { temperature = 0 },
            messages = new[] { new { role = "system", content = ExtractionPrompt.System + ExtractionPrompt.LanguageHint(transcript) }, new { role = "user", content = transcript } },
        }, ModelRole.Extraction, Model, AiCallKind.Inference, begin, admission, ct, 4096);
        var chat = doc.RootElement.Deserialize<Chat>(Json);
        await CheckAsync(admission, ct);
        var parsed = JsonSerializer.Deserialize<Output>(chat?.Message?.Content ?? "{}", Json)?.Candidates ?? [];
        var kept = parsed.Where(c => ExtractionPrompt.InSessionLanguage(c, transcript)).ToList();
        if (kept.Count < parsed.Length) log.LogWarning("Dropped {Count} candidate(s) written in another language", parsed.Length - kept.Count);
        return kept;
    }

    /// <summary>Pulls the model if it's missing, then checks the pinned digest (docs/03 D8). Ollama can't pull by digest.</summary>
    private static Task CheckAsync(Func<CancellationToken, Task> admission, CancellationToken ct) => admission is not null ? admission(ct) :
        throw (AiOperationPolicy.Strict ? AiOperationPolicy.Missing() : new ArgumentNullException(nameof(admission)));

    private async Task PrepareAsync(Func<CancellationToken, Task> admission, Func<AiCallRequest, CancellationToken, Task<AiCallLease>> begin, CancellationToken ct)
    {
        if (await DigestAsync(admission, begin, ct) is null && config.GetValue("Athar:Extraction:AutoPull", true))
        {
            log.LogInformation("Pulling {Model} into Ollama", Model);
            await CheckAsync(admission, ct);
            using var pull = await AiBoundedRequest.SendJsonAsync(_http, "/api/pull", new { model = Model, stream = false },
                ModelRole.Extraction, Model, AiCallKind.Preparation, begin, admission, ct);
            await CheckAsync(admission, ct);
        }
        var digest = await DigestAsync(admission, begin, ct);
        if (digest != PinnedDigest) throw new InvalidOperationException($"{Model} digest {digest ?? "missing"} isn't the pinned {PinnedDigest} (docs/03 D8).");
        log.LogInformation("{Model} ready ({Digest})", Model, digest[..12]);
    }

    private async Task<string?> DigestAsync(Func<CancellationToken, Task> admission, Func<AiCallRequest, CancellationToken, Task<AiCallLease>> begin, CancellationToken ct)
    {
        await CheckAsync(admission, ct);
        using var doc = await AiBoundedRequest.SendJsonAsync(_http, "/api/tags", null, ModelRole.Extraction, Model, AiCallKind.Metadata, begin, admission, ct);
        var tags = doc.RootElement.Deserialize<Tags>(Json);
        await CheckAsync(admission, ct);
        return tags?.Models.FirstOrDefault(m => m.Name == Model)?.Digest?.Replace("sha256:", "");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record Output(ExtractedCandidate[]? Candidates);
    private sealed record Chat([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
    private sealed record Tags([property: JsonPropertyName("models")] Tag[] Models);
    private sealed record Tag([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("digest")] string? Digest);
}
