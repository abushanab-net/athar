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
