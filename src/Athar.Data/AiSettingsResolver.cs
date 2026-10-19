using System.Security.Cryptography;
using System.Text;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Athar.Data;

/// <summary>Frozen for one operation. Secrets never participate in diagnostic output.</summary>
public sealed record AiSnapshot(string? BaseUrl, string EmbeddingModel, string QueryModel, string? VisionModel,
    string? ApiKey, bool KeyUnreadable, bool UsesEnvironment, string EmbeddingIdentity, string QueryInstruction)
{
    public int Dimensions { get; init; } = AiModels.Dimensions;
    /// <summary>Stores one model call's usage (FR-AG-12b); null when nothing records it.</summary>
    public Func<ModelUsage, Task>? RecordUsage { get; init; }
    public bool Gateway => !string.IsNullOrWhiteSpace(BaseUrl);
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

public sealed class AiSettingsResolver(IDbContextFactory<AtharDbContext> dbs, IConfiguration config, IDataProtectionProvider protection, ILogger<AiSettingsResolver>? log = null)
{
    private readonly AiOptions environment = config.GetSection("Athar:Ai").Get<AiOptions>() ?? new();

    public string Protect(Guid org, string key) => Protector(org).Protect(key);
    private IDataProtector Protector(Guid org) => protection.CreateProtector(AtharKeys.AiApiKeyPurpose, org.ToString("N"));

    public async Task<AiSnapshot> ResolveAsync(Guid org, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = org;
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
        return new(url, model, query, vision, key, unreadable, env, identity, environment.QueryInstruction) { Dimensions = env ? environment.Dimensions : AiModels.Dimensions, RecordUsage = u => RecordUsageAsync(org, u) };
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
