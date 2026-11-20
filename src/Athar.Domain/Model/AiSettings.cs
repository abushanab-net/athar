namespace Athar.Domain.Model;

/// <summary>One durable row per organization, including when deployment defaults are active.</summary>
public sealed class AiSettings : OrgEntity
{
    public long Revision { get; set; }
    public bool UsesEnvironment { get; set; } = true;
    public long EmbeddingGeneration { get; set; }
    public string? BaseUrl { get; set; }
    public string? EmbeddingModel { get; set; }
    public string? QueryModel { get; set; }
    public string? VisionModel { get; set; }
    public string? ProtectedApiKey { get; set; }
}

/// <summary>An operation admission failure must escape provider and degraded-result handlers.</summary>
public interface IAiAdmissionFailure
{
    /// <summary>False when retrying the same work would be refused again (for example an over-limit response).</summary>
    bool Retryable => true;
}
