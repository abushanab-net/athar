namespace Athar.Domain.Model;

// docs/04 §4.2: credentials, jobs, durable operations.

public enum DisclosureClass { ExternalAgent, TrustedLocal }

public class AccessToken : OrgEntity
{
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public string[] Scopes { get; set; } = [];
    public DisclosureClass DisclosureClass { get; set; } = DisclosureClass.ExternalAgent;
    public Guid? ClassifiedBy { get; set; }
    public string? ClassificationReason { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public enum JobStatus { Ready, Running, Done, Dead }

/// <summary>Postgres job queue with leases (architecture §7).</summary>
public class Job : OrgEntity
{
    public required string Kind { get; set; }
    public required string Key { get; set; }
    /// <summary>jsonb.</summary>
    public string Payload { get; set; } = "{}";
    public JobStatus Status { get; set; }
    public DateTimeOffset RunAfter { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>Postgres xmin, the EF concurrency token.</summary>
    public uint Version { get; set; }
}

public enum OperationTarget { Page, Memory, Proposal, Shadow, Conflict, Comment }
public enum OperationStatus { Pending, Done, Failed }

/// <summary>Durable, idempotent writes (FR-RT-07). Kept 7 days.</summary>
public class Operation : OrgEntity
{
    public required string OpId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? CredentialId { get; set; }
    public required string RequestHash { get; set; }
    public OperationTarget TargetKind { get; set; }
    public string? TargetId { get; set; }
    public OperationStatus Status { get; set; }
    /// <summary>jsonb.</summary>
    public string? Result { get; set; }
}

/// <summary>One MCP tool call (FR-AG-12a): identity as audit events (FR-AU-02), never arguments or content. Raw rows kept 90 days.</summary>
public class McpCall : OrgEntity
{
    public Guid? ActorUserId { get; set; }
    public Guid? CredentialId { get; set; }
    public Guid? OauthClientId { get; set; }
    /// <summary>Untrusted label the client claimed.</summary>
    public string? ClaimedClient { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public required string Tool { get; set; }
    public int DurationMs { get; set; }
    /// <summary>"ok" or an error code.</summary>
    public required string Outcome { get; set; }
    public int? ResultCount { get; set; }
}

public enum ModelRole { Embedding, Query, Vision }

/// <summary>One model gateway call (FR-AG-12b): tokens from the response's usage, zero when absent. Raw rows kept 90 days.</summary>
public class ModelUsage : OrgEntity
{
    public ModelRole Role { get; set; }
    public required string ModelId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int DurationMs { get; set; }
}

/// <summary>Nightly rollup (FR-AG-12c): one count per org, day, user, client and kind. Kept 400 days.</summary>
public class DailyAgentStat : OrgEntity
{
    public DateOnly Date { get; set; }
    /// <summary>Null for activity without a user (model usage, system jobs).</summary>
    public Guid? UserId { get; set; }
    public required string Client { get; set; }
    public required string Kind { get; set; }
    public long Count { get; set; }
}
