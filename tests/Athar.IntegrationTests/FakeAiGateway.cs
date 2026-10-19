using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Athar.IntegrationTests;

/// <summary>Real loopback HTTP transport with deterministic responses; no model download or GPU required.</summary>
public sealed class FakeAiGateway : IAsyncDisposable
{
    private readonly WebApplication app;
    private FakeAiGateway(WebApplication app) => this.app = app;
    public string BaseUrl { get; private set; } = "";
    public string EmbeddingModel => "qa-embedding";
    public string QueryModel => "qa-query";
    public string VisionModel => "qa-vision";
    public ConcurrentQueue<Request> Requests { get; } = new();
    public sealed record Request(string Path, string? Authorization, JsonElement Body, CancellationToken Aborted = default);
    public Func<Request, Task>? BeforeResponse { get; set; }
    public string? EmbeddingFault { get; set; }
    public bool MalformedChat { get; set; }
    public Func<HttpContext, Task>? AfterHeaders { get; set; }
    public string? ChatOverride { get; set; }
    public int StatusCode { get; set; } = 200;
    public string? RedirectTo { get; set; }
    public string VisionText { get; set; } = "Synthetic image transcription: zephyr approval procedure.";
    public object[] Candidates { get; set; } = [];

    public static async Task<FakeAiGateway> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = Athar.Data.AiGatewayClient.MaxRequestBytes);
        var app = builder.Build();
        var fake = new FakeAiGateway(app);
        app.MapMethods("/v1/{**path}", ["GET", "POST"], async (HttpContext http, string path) =>
        {
            using var body = http.Request.Method == "POST" ? await JsonDocument.ParseAsync(http.Request.Body) : JsonDocument.Parse("{}");
            var request = new Request(path, http.Request.Headers.Authorization.FirstOrDefault(), body.RootElement.Clone(), http.RequestAborted);
            fake.Requests.Enqueue(request);
            if (fake.BeforeResponse is { } callback) await callback(request);
            if (fake.AfterHeaders is { } afterHeaders)
            {
                http.Response.ContentType = "application/json";
                await http.Response.StartAsync(http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
                await afterHeaders(http);
                return Results.Empty;
            }
            if (fake.RedirectTo is { } redirect) return Results.Redirect(redirect);
            if (fake.StatusCode != 200) return Results.Text("Provider body must never escape: SECRET", statusCode: fake.StatusCode);
            if (path == "models") return Results.Json(new { data = new[] { fake.EmbeddingModel, fake.QueryModel, fake.VisionModel }.Select(id => new { id }) });
            if (path == "embeddings")
            {
                var n = request.Body.GetProperty("input").GetArrayLength();
                var dimensions = fake.EmbeddingFault switch { "short" => 3, "long" => 2560, _ => 1024 };
                // Keep fake vectors distinct from QaSeed.CanaryVector, which tenant-isolation tests count exactly.
                var data = Enumerable.Range(0, fake.EmbeddingFault == "count" ? n + 1 : n).Select(i => new
                {
                    index = fake.EmbeddingFault == "index" ? n : fake.EmbeddingFault == "duplicate" ? 0 : i,
                    embedding = Enumerable.Range(0, dimensions).Select(j => fake.EmbeddingFault == "zero" ? 0f : j == i + 1 ? 1f : 0f).ToArray(),
                }).Reverse(); // prove the consumer respects indices, not response order
                return Results.Json(new { data });
            }
            if (path == "chat/completions")
            {
                if (fake.MalformedChat) return Results.Json(new { choices = Array.Empty<object>() });
                var root = request.Body;
                string text;
                if (fake.ChatOverride is { } overridden) text = overridden;
                else if (root.GetProperty("model").GetString() == fake.VisionModel) text = fake.VisionText;
                else if (root.TryGetProperty("response_format", out var format)
                    && format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties") is var props && props.TryGetProperty("ok", out _)) text = "{\"ok\":true}";
                else if (root.TryGetProperty("response_format", out format)
                    && format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").TryGetProperty("candidates", out _))
                    text = JsonSerializer.Serialize(new { candidates = fake.Candidates });
                else text = "{\"answer\":\"The supplied evidence describes the approval procedure.\",\"citations\":[1]}";
                return Results.Json(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = text } } },
                    usage = new { prompt_tokens = 12, completion_tokens = 3 } });
            }
            return Results.NotFound();
        });
        await app.StartAsync();
        fake.BaseUrl = app.Urls.Single() + "/v1";
        return fake;
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}
