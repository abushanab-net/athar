namespace Athar.Domain.Model;

// docs/04 §4.2 Second brain.

public class Project : OrgEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
}

public class ProjectMember : OrgEntity
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public ProjectRole Role { get; set; }
}

/// <summary>Exactly one of RepoRemote (normalized), SpaceId or TeamId.</summary>
public class ProjectLink : OrgEntity
{
    public Guid ProjectId { get; set; }
    public string? RepoRemote { get; set; }
    public Guid? SpaceId { get; set; }
    public Guid? TeamId { get; set; }
}

public enum MemoryType { Fact, Decision, Convention, Procedure, Policy, Preference, Incident, Glossary }
public enum MemoryStatus { Active, Archived, Superseded }

/// <summary>Owner columns match the scope; all null for company (check constraint).</summary>
public class Memory : OrgEntity
{
    public Scope Scope { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? OwnerTeamId { get; set; }
    public Guid? OwnerProjectId { get; set; }
    public MemoryType Type { get; set; }
    public required string Title { get; set; }
    public required string BodyMd { get; set; }
    public MemoryStatus Status { get; set; }
    /// <summary>Default false for policies (domain).</summary>
    public bool Overridable { get; set; }
    /// <summary>Free label: manual, remember, capture, promotion, ...</summary>
    public required string Origin { get; set; }
    public Guid? CreatedByUser { get; set; }
    public Guid? CreatedByCredential { get; set; }
    public int Revision { get; set; } = 1;
    public bool IsConfidential { get; set; }
}

public class MemoryVersion : OrgEntity
{
    public Guid MemoryId { get; set; }
    public int Revision { get; set; }
    public required string Title { get; set; }
    public required string BodyMd { get; set; }
    public Guid? ChangedBy { get; set; }
    public string? Reason { get; set; }
}

/// <summary>A project's override of a company memory. One active shadow per (project, company memory).</summary>
public class MemoryShadow : OrgEntity
{
    public Guid ProjectId { get; set; }
    public Guid CompanyMemoryId { get; set; }
    public Guid ShadowMemoryId { get; set; }
    public int BaseRevision { get; set; }
    public bool Stale { get; set; }
    /// <summary>Removed shadows are kept for history; the unique index covers active (not deleted) rows.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Skill: a company skill package (payload: name, description, body_md, files) awaiting a knowledge owner (FR-SK-02).</summary>
public enum ProposalKind { New, Replace, Evidence, Skill }
public enum ProposalStatus { Pending, Approved, Rejected, NeedsRereview }

public class MemoryProposal : OrgEntity
{
    /// <summary>Null for a new memory.</summary>
    public Guid? TargetMemoryId { get; set; }
    public int? BaseRevision { get; set; }
    /// <summary>jsonb: title, body, type.</summary>
    public required string Payload { get; set; }
    /// <summary>jsonb.</summary>
    public string? Evidence { get; set; }
    public ProposalKind Kind { get; set; }
    public Guid ProposedBy { get; set; }
    public ProposalStatus Status { get; set; }
    public Guid? RebasedFrom { get; set; }
    public Guid? ReviewerId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
    public bool IsConfidential { get; set; }
}

public enum ConflictStatus { Open, Resolved }
public enum ConflictResolution { KeepA, KeepB, Merged, NotConflict }

public class MemoryConflict : OrgEntity
{
    public Guid MemoryA { get; set; }
    public Guid MemoryB { get; set; }
    public Guid OpenedBy { get; set; }
    public ConflictStatus Status { get; set; }
    public ConflictResolution? Resolution { get; set; }
    public string? Note { get; set; }
}

public class MemoryLink : OrgEntity
{
    public Guid MemoryId { get; set; }
    public required string TargetKind { get; set; }
    public required string TargetId { get; set; }
    public string? Anchor { get; set; }
}

public enum CandidateStatus { New, Accepted, Edited, Discarded, AutoAccepted }

/// <summary>Visible only to its owner.</summary>
public class CaptureCandidate : OrgEntity
{
    public Guid SessionId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Scope SuggestedScope { get; set; }
    public Guid? SuggestedOwner { get; set; }
    public MemoryType Type { get; set; }
    public required string Title { get; set; }
    public required string BodyMd { get; set; }
    /// <summary>At most 500 characters.</summary>
    public string? EvidenceExcerpt { get; set; }
    public Guid[] SimilarIds { get; set; } = [];
    public CandidateStatus Status { get; set; }
    public bool IsConfidential { get; set; }
    /// <summary>The memory an accepted candidate became (Undo archives it), or the proposal for a company one.</summary>
    public Guid? MemoryId { get; set; }
    public Guid? ProposalId { get; set; }
}

/// <summary>Hook: a coding agent's transcript. Chat: a summary saved from a chat app with save_chat (FR-AG-09).</summary>
public enum SessionSource { Hook, Chat }

public class AgentSession : OrgEntity
{
    public Guid UserId { get; set; }
    public Guid? CredentialId { get; set; }
    public required string Client { get; set; }
    public required string ExternalSessionId { get; set; }
    public string? RepoRemote { get; set; }
    public Guid? ProjectId { get; set; }
    public string? Title { get; set; }
    public SessionSource Source { get; set; }
    public int AckedIndex { get; set; } = -1;
    public int ExtractedUpto { get; set; } = -1;
}

/// <summary>A repeated CommitId is a no-op.</summary>
public class SessionCommit : OrgEntity
{
    public Guid SessionId { get; set; }
    public required string CommitId { get; set; }
    public int FromIndex { get; set; }
    public int ToIndex { get; set; }
    public bool Skipped { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Owner only; purged 30 days after receipt.</summary>
public class SessionRecord : OrgEntity
{
    public Guid SessionId { get; set; }
    public int RecordIndex { get; set; }
    /// <summary>jsonb.</summary>
    public required string RedactedRecord { get; set; }
    public DateTimeOffset PurgeAfter { get; set; }
}

/// <summary>Url, Document and File sources hold snapshots, not git commits (FR-KS-06).</summary>
public enum SourceKind { Github, Url, Document, File }
public enum SourceStatus { Ok, PausedAuth, Error }

public class KnowledgeSource : OrgEntity
{
    public Guid ProjectId { get; set; }
    public SourceKind Kind { get; set; }
    /// <summary>What the source points at: owner/repo (Github), the URL (Url), the file name (Document) or the folder label (File).</summary>
    public required string Repo { get; set; }
    public string? Title { get; set; }
    public Guid? CreatedBy { get; set; }
    public byte[]? EncryptedPat { get; set; }
    public string? LastCommitSha { get; set; }
    public SourceStatus Status { get; set; }
    public bool IsConfidential { get; set; }
}

public class SourceDocument : OrgEntity
{
    public Guid SourceId { get; set; }
    public required string Path { get; set; }
    public string? Title { get; set; }
    public required string ContentMd { get; set; }
    public required string ContentHash { get; set; }
    /// <summary>Github sources only.</summary>
    public string? CommitSha { get; set; }
    public string? MediaType { get; set; }
    public long? SizeBytes { get; set; }
    /// <summary>When a snapshot was taken (Url, Document, File).</summary>
    public DateTimeOffset? FetchedAt { get; set; }
    /// <summary>The original file in the attachment store, kept for re-extraction.</summary>
    public string? StoredFile { get; set; }
    public DateTimeOffset? TombstonedAt { get; set; }
}

public enum SkillStatus { Active, Archived }

/// <summary>Scoped and owned like memories.</summary>
public class Skill : OrgEntity
{
    public Scope Scope { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? OwnerTeamId { get; set; }
    public Guid? OwnerProjectId { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string BodyMd { get; set; }
    /// <summary>jsonb.</summary>
    public string Files { get; set; } = "[]";
    public SkillStatus Status { get; set; }
    public bool IsConfidential { get; set; }
    public int Revision { get; set; } = 1;
}

/// <summary>Every published revision of a skill, for history (FR-SK-02).</summary>
public class SkillVersion : OrgEntity
{
    public Guid SkillId { get; set; }
    public int Revision { get; set; }
    public required string Description { get; set; }
    public required string BodyMd { get; set; }
    /// <summary>jsonb.</summary>
    public string Files { get; set; } = "[]";
    public Guid PublishedBy { get; set; }
}

/// <summary>A person's capture choices (FR-AG-04, FR-MM-06). No row means the defaults.</summary>
public class CaptureSettings : OrgEntity
{
    public Guid UserId { get; set; }
    /// <summary>Normalized repo remotes opted in; the hook adds its local capture_repos and removes capture_exclude.</summary>
    public string[] Repos { get; set; } = [];
    /// <summary>Stops all capture, whatever the local config says.</summary>
    public bool Paused { get; set; }
    public bool AutoAcceptPersonal { get; set; } = true;
    /// <summary>Project and team candidates the owner can publish.</summary>
    public bool AutoAcceptShared { get; set; }
}

public enum ChunkOwner { Page, Memory, SourceDocument, Attachment, Skill }

/// <summary>
/// A searchable slice of an owner. Scope keys and IsConfidential are denormalized for filtering.
/// The tsv_ar / tsv_en / tsv_simple tsvector columns are generated from SearchText (EF shadow properties).
/// </summary>
public class Chunk : OrgEntity
{
    public ChunkOwner OwnerKind { get; set; }
    public Guid OwnerId { get; set; }
    public long OwnerRevision { get; set; }
    public int Idx { get; set; }
    public string? HeadingPath { get; set; }
    public required string Body { get; set; }
    /// <summary>Normalized search copy (FR-SR-05).</summary>
    public required string SearchText { get; set; }
    public required string ContentHash { get; set; }
    public Guid? SpaceId { get; set; }
    public Scope? Scope { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? OwnerTeamId { get; set; }
    public Guid? OwnerProjectId { get; set; }
    public bool IsConfidential { get; set; }
}

public class ChunkEmbedding : OrgEntity
{
    public Guid ChunkId { get; set; }
    public required string ModelId { get; set; }
    /// <summary>vector(1024), bge-m3.</summary>
    public required float[] Embedding { get; set; }
}
