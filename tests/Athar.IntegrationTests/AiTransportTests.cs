using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;

namespace Athar.IntegrationTests;

public sealed class AiTransportTests
{
    private static AiSnapshot Snapshot(FakeAiGateway fake, string? model = null) => new(fake.BaseUrl, model ?? fake.EmbeddingModel,
        fake.QueryModel, fake.VisionModel, null, false, false, "test-id", "retrieve team notes");

    [Theory]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("http://8.8.8.8/v1")]
    [InlineData("http://[fe80::1]/v1")]
    [InlineData("http://[::ffff:169.254.169.254]/v1")]
    [InlineData("http://0.0.0.0/v1")]
    [InlineData("http://user:secret@127.0.0.1/v1")]
    [InlineData("http://127.0.0.1/v1?key=secret")]
    [InlineData("http://127.0.0.1/v1#fragment")]
    [InlineData("ftp://127.0.0.1/v1")]
    [InlineData("http://127.0.0.1/\n")]
    public void Saved_endpoints_reject_public_metadata_and_ambiguous_urls(string url) =>
        Assert.Equal("invalid_endpoint", Assert.Throws<AiProviderException>(() => AiGatewayClient.NormalizeEndpoint(url)).Code);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    [InlineData("fd12::1234")]
    public void Private_address_ranges_are_allowed(string ip) => Assert.True(AiGatewayClient.PrivateAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("count")]
    [InlineData("index")]
    [InlineData("duplicate")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("zero")]
    public async Task Invalid_embedding_shapes_are_rejected(string fault)
    {
        await using var fake = await FakeAiGateway.StartAsync();
        fake.EmbeddingFault = fault;
        var client = new AiGatewayClient(Snapshot(fake));
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<AiProviderException>(() => client.EmbedAsync(["one", "two"], false, default))).Code);
    }

    [Fact]
    public async Task Qwen_alone_truncates_and_instructs_queries_and_cancellation_and_redirects_fail_closed()
    {
        await using var fake = await FakeAiGateway.StartAsync();
        fake.EmbeddingFault = "long";
        var client = new AiGatewayClient(Snapshot(fake, "Qwen3-Embedding-4B"));
        var vectors = await client.EmbedAsync(["question"], true, default);
        Assert.Equal(1024, vectors[0].Length);
        Assert.Equal(0f, vectors[0][0]);
        Assert.Equal(1f, vectors[0][1]);
        Assert.StartsWith("Instruct: retrieve team notes\nQuery:", fake.Requests.Last().Body.GetProperty("input")[0].GetString());
        await client.EmbedAsync(["document"], false, default);
        Assert.Equal("document", fake.Requests.Last().Body.GetProperty("input")[0].GetString());
        var pair = await client.EmbedAsync(["left", "right"], false, default);
        Assert.Equal(1f, pair[0][1]);
        Assert.Equal(1f, pair[1][2]);
        Assert.Equal(0f, pair[1][1]); // the fake reverses response order; the client restores input order
        Assert.Throws<InvalidOperationException>(() => AiModels.Fit([float.NaN, .. Enumerable.Repeat(1f, 1023)], 1024));
        Assert.Throws<InvalidOperationException>(() => AiModels.Fit([float.PositiveInfinity, .. Enumerable.Repeat(1f, 1023)], 1024));
        fake.RedirectTo = "http://169.254.169.254/latest/meta-data";
        Assert.Equal("provider_rejected", (await Assert.ThrowsAsync<AiProviderException>(() => client.ModelsAsync(default))).Code);
        fake.RedirectTo = null;
        fake.BeforeResponse = _ => Task.Delay(300);
        Assert.Equal("provider_unavailable", (await Assert.ThrowsAsync<AiProviderException>(() => client.EmbedAsync(["timeout"], false, default, TimeSpan.FromMilliseconds(30)))).Code);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync(["cancel"], false, cts.Token));
    }
    [Theory]
    [InlineData(408, true, "provider_unavailable")]
    [InlineData(429, true, "provider_unavailable")]
    [InlineData(500, true, "provider_unavailable")]
    [InlineData(503, true, "provider_unavailable")]
    [InlineData(401, true, "authentication_unavailable")]
    [InlineData(403, true, "authentication_unavailable")]
    [InlineData(400, false, "provider_rejected")]
    [InlineData(404, false, "provider_rejected")]
    public async Task Http_failure_categories_are_sanitized_and_preserve_retryability(int status, bool retryable, string code)
    {
        await using var fake = await FakeAiGateway.StartAsync();
        fake.StatusCode = status;
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new AiGatewayClient(Snapshot(fake)).ModelsAsync(default));
        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public async Task Deadline_covers_headers_and_body_together_and_connection_errors_are_retryable()
    {
        await using var fake = await FakeAiGateway.StartAsync();
        fake.BeforeResponse = r => Task.Delay(700, r.Aborted);
        fake.AfterHeaders = async http =>
        {
            await Task.Delay(700, http.RequestAborted);
            await Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(http.Response,
                System.Text.Json.JsonSerializer.Serialize(new { data = new[] { new { index = 0, embedding = new[] { 1f }.Concat(Enumerable.Repeat(0f, 1023)) } } }), http.RequestAborted);
        };
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new AiGatewayClient(Snapshot(fake)).EmbedAsync(["deadline"], false, default, TimeSpan.FromSeconds(1)));
        Assert.Equal("provider_unavailable", error.Code);
        Assert.True(error.Retryable);
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var offline = Snapshot(fake) with { BaseUrl = $"http://127.0.0.1:{port}/v1" };
        Assert.True((await Assert.ThrowsAsync<AiProviderException>(() => new AiGatewayClient(offline).ModelsAsync(default))).Retryable);
    }

    [Fact]
    public async Task Empty_chat_and_malformed_envelopes_remain_permanent_errors()
    {
        await using var fake = await FakeAiGateway.StartAsync();
        var client = new AiGatewayClient(Snapshot(fake));
        fake.ChatOverride = "";
        Assert.False((await Assert.ThrowsAsync<AiProviderException>(() => client.ChatAsync(fake.QueryModel, [], null, 10, default))).Retryable);
        fake.MalformedChat = true;
        Assert.False((await Assert.ThrowsAsync<AiProviderException>(() => client.ChatAsync(fake.QueryModel, [], null, 10, default))).Retryable);
    }

    [Fact]
    public async Task Every_model_call_records_usage_and_a_failing_recorder_never_fails_the_call()
    {
        await using var fake = await FakeAiGateway.StartAsync();
        var recorded = new List<ModelUsage>();
        var client = new AiGatewayClient(Snapshot(fake) with { RecordUsage = u => { recorded.Add(u); return Task.CompletedTask; } });
        await client.ChatAsync(fake.QueryModel, [], null, 10, default);
        await client.EmbedAsync(["usage"], false, default);
        await client.ModelsAsync(default);
        fake.StatusCode = 500;
        await Assert.ThrowsAsync<AiProviderException>(() => client.ChatAsync(fake.QueryModel, [], null, 10, default));
        Assert.Collection(recorded,
            u => Assert.Equal((ModelRole.Query, fake.QueryModel, 12, 3), (u.Role, u.ModelId, u.InputTokens, u.OutputTokens)),
            u => Assert.Equal((ModelRole.Embedding, fake.EmbeddingModel, 0, 0), (u.Role, u.ModelId, u.InputTokens, u.OutputTokens)),
            u => Assert.Equal((ModelRole.Query, 0, 0), (u.Role, u.InputTokens, u.OutputTokens)));

        fake.StatusCode = 200;
        var failing = new AiGatewayClient(Snapshot(fake) with { RecordUsage = _ => throw new InvalidOperationException("db down") });
        Assert.NotEmpty(await failing.ChatAsync(fake.QueryModel, [], null, 10, default));
    }
}
