using System.Text.Json;
using ModelContextProtocol.Client;

namespace Athar.IntegrationTests;

/// <summary>MCP and REST helpers shared by the brain tests.</summary>
internal static class Mcp
{
    /// <summary>An MCP client authenticated with <paramref name="pat"/>. Also records each tool's output schema for <see cref="Call"/>.</summary>
    internal static async Task<McpClient> Connect(HttpClient http, Uri endpoint, string pat, CancellationToken ct)
    {
        var client = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {pat}" },
        }, http), cancellationToken: ct);
        foreach (var t in await client.ListToolsAsync(cancellationToken: ct))
            if (t.ProtocolTool.OutputSchema is { } schema) Schemas[t.Name] = schema;
        return client;
    }

    internal static async Task Refused(HttpStatusCode expected, HttpClient http, Uri endpoint, string? pat, string? origin, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        if (pat is not null) request.Headers.Add("Authorization", $"Bearer {pat}");
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await http.SendAsync(request, ct);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    internal static async Task<JsonElement> Call(McpClient mcp, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await mcp.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.True(result.IsError != true, string.Join("\n", result.Content.Select(c => c.ToString())));
        // Clients such as Claude Code reject structured content that misses a property the output schema requires.
        AssertRequired(Schemas[tool], result.StructuredContent!.Value, tool);
        return result.StructuredContent!.Value;
    }

    private static readonly Dictionary<string, JsonElement> Schemas = [];

    internal static void AssertRequired(JsonElement schema, JsonElement data, string path)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    Assert.True(data.TryGetProperty(name.GetString()!, out _), $"{path}.{name} is required by the output schema");
            if (schema.TryGetProperty("properties", out var props))
                foreach (var p in data.EnumerateObject())
                    if (props.TryGetProperty(p.Name, out var sub)) AssertRequired(sub, p.Value, $"{path}.{p.Name}");
        }
        else if (data.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var e in data.EnumerateArray()) AssertRequired(items, e, $"{path}[]");
    }

    internal static async Task<string> CallError(McpClient mcp, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await mcp.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.True(result.IsError);
        return JsonSerializer.Serialize(result.Content);
    }

    /// <summary>POST a JSON body to REST with a PAT; returns the status and parsed body.</summary>
    internal static async Task<(HttpStatusCode Status, JsonElement Body)> Post(HttpClient http, string path, string pat, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = System.Net.Http.Json.JsonContent.Create(body ?? new { }) };
        request.Headers.Add("Authorization", $"Bearer {pat}");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, Json(response.StatusCode, text));
    }

    /// <summary>GET from REST with a PAT; returns the status and parsed body.</summary>
    internal static async Task<(HttpStatusCode Status, JsonElement Body)> Get(HttpClient http, string path, string pat, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Authorization", $"Bearer {pat}");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, Json(response.StatusCode, text));
    }

    // A non-JSON body (a 500's stack trace, a redirect page) fails with what the server actually said.
    private static JsonElement Json(HttpStatusCode status, string text)
    {
        if (text.Length == 0) return default;
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException($"{(int)status} with a non-JSON body: {text[..Math.Min(text.Length, 2000)]}"); }
    }

    internal static string Str(this JsonElement e, string name) => e.GetProperty(name).ToString();
}
