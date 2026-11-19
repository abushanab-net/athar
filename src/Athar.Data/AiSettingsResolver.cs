using System.Security.Cryptography;
using System.Text;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Athar.Data;

public enum AiPurpose { Embedding, Answer, Extraction, Vision, WikiSummary, AnswerCheck }

public interface IAiFunctionRouter
{
    Task<AiModelOperation> CaptureAsync(AtharDbContext work, CancellationToken ct);
    Task RequireOperationAsync(AiModelOperation? operation, CancellationToken ct);
    Task<AiSnapshot> ResolveAsync(AiModelOperation operation, AiPurpose purpose, CancellationToken ct);
}

public enum AiCallKind { Inference, Metadata, Preparation }
public sealed record AiCallRequest(ModelRole Role, string Model, int RequestBytes, int OutputTokens = 0, AiCallKind Kind = AiCallKind.Inference);

/// <summary>Owns one request's capacity until response consumption finishes. CE retains its existing transport bounds.</summary>
public class AiCallLease(CancellationToken caller) : IAsyncDisposable
{
    public virtual bool Enforced => false;
    public virtual CancellationToken Token => caller;
    public virtual int MaxRequestBytes => AiGatewayClient.MaxRequestBytes;
    public virtual int MaxResponseBytes => AiGatewayClient.MaxResponseBytes;
    public virtual int MaxOutputTokens => 4096;
    public virtual void RequireRequestBytes(long bytes) { if (bytes > MaxRequestBytes) throw new AiProviderException("input_too_large"); }
    public virtual void RequireResponseBytes(long bytes) { if (bytes > MaxResponseBytes) throw new AiProviderException("response_too_large"); }
    public virtual Task CheckAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public virtual Task CompleteAsync(int? inputTokens, int? outputTokens, CancellationToken ct) => CheckAsync(ct);
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
    // Explicit protocol-only tests and ordinary CE contracts; EE entrypoints attach a genuine operation factory.
    public static Task<AiCallLease> UnrestrictedAsync(AiCallRequest request, CancellationToken ct) => Task.FromResult(new AiCallLease(ct));
    public static (int? Input, int? Output) ReportedTokens(System.Text.Json.JsonElement root)
    {
        static int? Read(System.Text.Json.JsonElement value, params string[] names)
        {
            foreach (var name in names) if (value.ValueKind == System.Text.Json.JsonValueKind.Object && value.TryGetProperty(name, out var n) && n.ValueKind == System.Text.Json.JsonValueKind.Number && n.TryGetInt32(out var count) && count >= 0) return count;
            return null;
        }
        var usage = root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("usage", out var nested) ? nested : root;
        return (Read(usage, "prompt_tokens", "input_tokens", "prompt_eval_count"), Read(usage, "completion_tokens", "output_tokens", "eval_count"));
    }
}

/// <summary>Neutral transport helper; EE supplies the owned policy lease, while ordinary CE preserves its transport settings.</summary>
public static class AiBoundedRequest
{
    public static async Task<System.Text.Json.JsonDocument> SendJsonAsync(HttpClient http, string path, object? body, ModelRole role, string model,
        AiCallKind kind, Func<AiCallRequest, CancellationToken, Task<AiCallLease>> begin, Func<CancellationToken, Task> admission, CancellationToken ct, int outputTokens = 0)
    {
        await admission(ct);
        var bytes = body is null ? [] : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body);
        var reservedBytes = bytes.Length;
        if (outputTokens > 0)
        {
            var estimate = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
            estimate["options"] ??= new System.Text.Json.Nodes.JsonObject(); estimate["options"]!["num_predict"] = outputTokens;
            reservedBytes = Math.Max(reservedBytes, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(estimate).Length);
        }
        await using var lease = await begin(new(role, model, reservedBytes, outputTokens, kind), ct);
        if (lease.Enforced && outputTokens > 0)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
            node["options"] ??= new System.Text.Json.Nodes.JsonObject();
            node["options"]!["num_predict"] = Math.Min(outputTokens, lease.MaxOutputTokens);
            bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node);
            // The bound includes the injected output cap, not just the original request.
            lease.RequireRequestBytes(bytes.Length);
        }
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        }
        try
        {
            await lease.CheckAsync(ct); await admission(ct);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lease.Token);
            response.EnsureSuccessStatusCode();
            var limit = lease.Enforced ? lease.MaxResponseBytes : int.MaxValue;
            if (lease.Enforced && response.Content.Headers.ContentLength is { } length) lease.RequireResponseBytes(length);
            if (response.Content.Headers.ContentLength > limit) throw new AiProviderException("response_too_large");
            await using var stream = await response.Content.ReadAsStreamAsync(lease.Token);
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(chunk, lease.Token)) > 0)
            { if (lease.Enforced) lease.RequireResponseBytes(buffer.Length + count); if (buffer.Length + count > limit) throw new AiProviderException("response_too_large"); buffer.Write(chunk, 0, count); }
            await lease.CheckAsync(ct); await admission(ct);
            var doc = System.Text.Json.JsonDocument.Parse(buffer.ToArray());
            try
            {
                var usage = AiCallLease.ReportedTokens(doc.RootElement);
                await lease.CompleteAsync(usage.Input, role == ModelRole.Embedding ? 0 : usage.Output, ct); await admission(ct); return doc;
            }
            catch { doc.Dispose(); throw; }
        }
        catch (OperationCanceledException) { await lease.CheckAsync(ct); throw; }
    }
}

/// <summary>One original admission, retained across roles and explicit prepare/commit context handoff.</summary>
public class AiModelOperation(AtharDbContext work, Func<AtharDbContext, CancellationToken, Task>? admission = null)
{
    public AtharDbContext Work { get; } = work;
    public Guid OrgId { get; } = work.CurrentOrgId;
    public Task CheckAsync(CancellationToken ct) => CheckAsync(Work, ct);
    public virtual async Task<AiCallLease> BeginCallAsync(AiCallRequest request, CancellationToken ct)
    { await CheckAsync(ct); return new AiCallLease(ct); }
    public virtual Task CheckAsync(AtharDbContext actualWork, CancellationToken ct)
    {
        if (admission is not null) return admission(actualWork, ct);
        if (actualWork.CurrentOrgId != OrgId) throw new InvalidOperationException("ai_operation_org_changed");
        return Task.CompletedTask;
    }
}

public interface IAiModelClient
{
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct, TimeSpan? timeout);
    Task<string> ChatAsync(string model, object[] messages, object? schema, int maxTokens, CancellationToken ct);
    Task<string> TranscribeAsync(byte[] bytes, string mime, CancellationToken ct);
}

/// <summary>Frozen for one operation. Secrets never participate in diagnostic output.</summary>
public sealed record AiSnapshot(string? BaseUrl, string EmbeddingModel, string QueryModel, string? VisionModel,
    string? ApiKey, bool KeyUnreadable, bool UsesEnvironment, string EmbeddingIdentity, string QueryInstruction) : IAsyncDisposable
{
    public int Dimensions { get; init; } = AiModels.Dimensions;
    /// <summary>Stores one model call's usage (FR-AG-12b); null when nothing records it.</summary>
    public Func<ModelUsage, Task>? RecordUsage { get; init; }
    public IAiModelClient? Client { get; init; }
    public Func<CancellationToken, Task>? Admission { get; init; }
    public Func<AiCallRequest, CancellationToken, Task<AiCallLease>>? CallAdmission { get; init; }
    public Task<AiCallLease> BeginCallAsync(AiCallRequest request, CancellationToken ct) => CallAdmission is not null ? CallAdmission(request, ct) :
        AiOperationPolicy.Strict ? throw AiOperationPolicy.Missing() : AiCallLease.UnrestrictedAsync(request, ct);
    public Task CheckAsync(CancellationToken ct) => Admission?.Invoke(ct) ?? Task.CompletedTask;
    public IAsyncDisposable? Lifetime { get; init; }
    public ValueTask DisposeAsync() => Lifetime?.DisposeAsync() ?? ValueTask.CompletedTask;
    public bool Gateway => Client is not null || !string.IsNullOrWhiteSpace(BaseUrl);
    public bool QwenEmbedding => EmbeddingModel.StartsWith("Qwen3-Embedding", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => nameof(AiSnapshot);

    public void RequireEndpoint()
    {
        if (KeyUnreadable) throw new AiProviderException("key_unreadable");
        if (!Gateway || UsesEnvironment && string.IsNullOrWhiteSpace(ApiKey))
            throw new AiProviderException("configuration_unavailable");
    }

    public void RequireGateway()
    {
        RequireEndpoint();
        if (Dimensions != AiModels.Dimensions || string.IsNullOrWhiteSpace(EmbeddingModel) || string.IsNullOrWhiteSpace(QueryModel))
            throw new AiProviderException("configuration_invalid");
    }
}

public sealed class AiSettingsResolver(IDbContextFactory<AtharDbContext> dbs, IConfiguration config, IDataProtectionProvider protection, ILogger<AiSettingsResolver>? log = null, IAiFunctionRouter? router = null)
{
    private readonly AiOptions environment = config.GetSection("Athar:Ai").Get<AiOptions>() ?? new();

    public string Protect(Guid org, string key) => Protector(org).Protect(key);
    private IDataProtector Protector(Guid org) => protection.CreateProtector(AtharKeys.AiApiKeyPurpose, org.ToString("N"));

    public async Task<AiModelOperation> CaptureAsync(AtharDbContext work, CancellationToken ct = default)
    {
        var operation = work.ModelOperation ?? (router is null ? new AiModelOperation(work) : work.ModelOperation = await router.CaptureAsync(work, ct));
        await operation.CheckAsync(ct);
        return operation;
    }

    public async Task<AiSnapshot> ResolveAsync(AiModelOperation operation, AiPurpose purpose, CancellationToken ct = default)
    {
        await operation.CheckAsync(ct);
        if (router is not null) await router.RequireOperationAsync(operation, ct);
        var snapshot = router is null ? await ResolveLegacyAsync(operation, ct) : await router.ResolveAsync(operation, purpose, ct);
        try
        {
            await operation.CheckAsync(ct);
            return snapshot with { Admission = operation.CheckAsync, CallAdmission = operation.BeginCallAsync };
        }
        catch (Exception failure)
        {
            try { await snapshot.DisposeAsync(); }
            catch (Exception) when (failure is IAiAdmissionFailure || failure is OperationCanceledException && ct.IsCancellationRequested)
            { log?.LogWarning("AI snapshot cleanup failed after admission rejection or cancellation."); }
            throw;
        }
    }

    public async Task<AiSnapshot> ResolveAsync(AtharDbContext work, AiPurpose purpose, CancellationToken ct = default) =>
        await ResolveAsync(await CaptureAsync(work, ct), purpose, ct);

    // Compatibility callers own a real operation context for the returned snapshot's lifetime.
    public async Task<AiSnapshot> ResolveAsync(Guid org, AiPurpose purpose, CancellationToken ct = default)
    {
        var db = await dbs.CreateDbContextAsync(ct); db.CurrentOrgId = org;
        AiSnapshot? snapshot = null;
        try
        {
            snapshot = await ResolveAsync(db, purpose, ct);
            return snapshot with { Lifetime = new OwnedLifetime(snapshot.Lifetime, db) };
        }
        catch (Exception failure)
        {
            try
            {
                try { if (snapshot is not null) await snapshot.DisposeAsync(); }
                catch (Exception) when (failure is IAiAdmissionFailure || failure is OperationCanceledException && ct.IsCancellationRequested)
                { log?.LogWarning("AI snapshot cleanup failed after admission rejection or cancellation."); }
            }
            finally
            {
                try { await db.DisposeAsync(); }
                catch (Exception) when (failure is IAiAdmissionFailure || failure is OperationCanceledException && ct.IsCancellationRequested)
                { log?.LogWarning("AI context cleanup failed after admission rejection or cancellation."); }
            }
            throw;
        }
    }

    private sealed class OwnedLifetime(IAsyncDisposable? pin, AtharDbContext db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { try { if (pin is not null) await pin.DisposeAsync(); } finally { await db.DisposeAsync(); } }
    }

    public async Task<AiSnapshot> ResolveLegacyAsync(AiModelOperation operation, CancellationToken ct = default)
    {
        await operation.CheckAsync(ct);
        if (router is not null) await router.RequireOperationAsync(operation, ct);
        var snapshot = Resolve(operation.OrgId, await operation.Work.AiSettings.AsNoTracking().SingleOrDefaultAsync(ct));
        return ForOperation(snapshot, operation, ct);
    }

    public AiSnapshot ForOperation(AiSnapshot snapshot, AiModelOperation operation, CancellationToken ct) => snapshot with
    {
        Admission = operation.CheckAsync, CallAdmission = operation.BeginCallAsync,
        RecordUsage = usage => RecordUsageAsync(operation, usage, ct)
    };

    private async Task RecordUsageAsync(AiModelOperation operation, ModelUsage usage, CancellationToken ct)
    {
        await operation.CheckAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct); db.CurrentOrgId = operation.OrgId;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await operation.CheckAsync(db, ct);
            db.ModelUsage.Add(usage);
            await operation.CheckAsync(db, ct);
            await db.SaveChangesAsync(ct);
            await operation.CheckAsync(db, ct);
            await tx.CommitAsync(ct);
            await operation.CheckAsync(ct);
        }
        catch (Exception e) when (e is not IAiAdmissionFailure) { log?.LogWarning(e, "Model usage not recorded"); }
    }

    public async Task<AiSnapshot> ResolveAsync(Guid org, CancellationToken ct = default)
    {
        var db = await dbs.CreateDbContextAsync(ct); db.CurrentOrgId = org;
        AiSnapshot? snapshot = null;
        try
        {
            var operation = await CaptureAsync(db, ct);
            snapshot = await ResolveLegacyAsync(operation, ct);
            return snapshot with { Lifetime = new OwnedLifetime(snapshot.Lifetime, db) };
        }
        catch (Exception failure)
        {
            try
            {
                try { if (snapshot is not null) await snapshot.DisposeAsync(); }
                catch (Exception) when (failure is IAiAdmissionFailure || failure is OperationCanceledException && ct.IsCancellationRequested)
                { log?.LogWarning("AI snapshot cleanup failed after admission rejection or cancellation."); }
            }
            finally
            {
                try { await db.DisposeAsync(); }
                catch (Exception) when (failure is IAiAdmissionFailure || failure is OperationCanceledException && ct.IsCancellationRequested)
                { log?.LogWarning("AI context cleanup failed after admission rejection or cancellation."); }
            }
            throw;
        }
    }

    public async Task<AiSnapshot> ResolveMetadataAsync(Guid org, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateDbContextAsync(ct); db.CurrentOrgId = org;
        return Resolve(org, await db.AiSettings.AsNoTracking().SingleOrDefaultAsync(ct));
    }

    public AiSnapshot Resolve(Guid org, AiSettings? row)
    {
        var env = row is null || row.UsesEnvironment;
        string? key = env ? environment.ApiKey : null;
        var unreadable = false;
        if (!env && row!.ProtectedApiKey is { } encrypted)
        {
            try { key = Protector(org).Unprotect(encrypted); }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { unreadable = true; }
        }
        var url = env ? environment.BaseUrl : row!.BaseUrl;
        var model = env ? environment.Gateway ? environment.EmbeddingModel : AiModels.OllamaEmbeddingId : row!.EmbeddingModel!;
        var query = env ? environment.QueryModel ?? environment.ChatModel : row!.QueryModel!;
        var vision = env ? environment.VisionModel : row!.VisionModel;
        var identity = row is null || row.UsesEnvironment && row.EmbeddingGeneration == 0 ? AiModels.EmbeddingId(environment)
            : Identity(url, model, environment.QueryInstruction, row.EmbeddingGeneration);
        return new(url, model, query, vision, key, unreadable, env, identity, environment.QueryInstruction) { Dimensions = env ? environment.Dimensions : AiModels.Dimensions, RecordUsage = u => RecordUsageAsync(org, u), Admission = router is null ? null : token => router.RequireOperationAsync(null, token) };
    }

    /// <summary>Best-effort (FR-AG-12): a failed write is logged and never fails the model call.</summary>
    private async Task RecordUsageAsync(Guid org, ModelUsage usage)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync();
            db.CurrentOrgId = org;
            db.ModelUsage.Add(usage);
            await db.SaveChangesAsync();
        }
        catch (Exception e) { log?.LogWarning(e, "Model usage not recorded"); }
    }

    public static string Identity(string? url, string model, string instruction, long generation)
    {
        var endpoint = string.IsNullOrWhiteSpace(url) ? "ollama" : Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? uri.AbsoluteUri : url;
        var preprocessing = model.StartsWith("Qwen3-Embedding", StringComparison.OrdinalIgnoreCase) ? "qwen-fit-v1:" + instruction : "exact-v1";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}\n{model}\n1024\n{preprocessing}\n{generation}"));
        return "ai:" + Convert.ToHexStringLower(hash);
    }
}

/// <summary>Only stable, secret-free categories cross the provider boundary.</summary>
public sealed class AiProviderException(string code) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable => Code is "provider_unavailable" or "authentication_unavailable" or "key_unreadable" or "configuration_unavailable";
}
