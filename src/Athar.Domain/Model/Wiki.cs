namespace Athar.Domain.Model;

// docs/04 §4.1 Wiki and access.

public class User : OrgEntity
{
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public string Locale { get; set; } = "en";
    public bool IsGuest { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    /// <summary>The profile photo's file name in the attachment store (T113); null shows initials.</summary>
    public string? AvatarFile { get; set; }
}

public class OrgMember : OrgEntity
{
    public Guid UserId { get; set; }
    public OrgRole Role { get; set; }
    public bool IsKnowledgeOwner { get; set; }
}

public class Invitation : OrgEntity
{
    public required string Email { get; set; }
    public OrgRole Role { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
}

public class Team : OrgEntity
{
    public required string Name { get; set; }
    public Guid? LeadUserId { get; set; }
}

public class TeamMember : OrgEntity
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
}

public class Workspace : OrgEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    /// <summary>A key of <see cref="Wiki.Appearance.Colors"/>; null shows a color derived from the id.</summary>
    public string? Color { get; set; }
    /// <summary>A key of <see cref="Wiki.Appearance.Icons"/>; null shows the name's initials.</summary>
    public string? Icon { get; set; }
}

public class WorkspaceMember : OrgEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public WorkspaceRole Role { get; set; }
}

public class Space : OrgEntity
{
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    /// <summary>jsonb (FR-WS-03).</summary>
    public AiPolicy AiPolicy { get; set; } = new();
    public bool IsConfidential { get; set; }
    /// <summary>A key of <see cref="Wiki.Appearance.Colors"/>; null shows a color derived from the id.</summary>
    public string? Color { get; set; }
    /// <summary>A key of <see cref="Wiki.Appearance.Icons"/>; null shows the name's initials.</summary>
    public string? Icon { get; set; }
    /// <summary>T166: a deleted space is hidden everywhere (query filter) and its pages are in the trash, purged after 30 days.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>A workspace or a space someone pinned to their side menu. Exactly one of WorkspaceId or SpaceId; personal.</summary>
public class Favorite : OrgEntity
{
    public Guid UserId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? SpaceId { get; set; }
}

/// <summary>What agents may do in a space (FR-WS-03). Each setting is independent; the default is read only.</summary>
public sealed record AiPolicy(bool Read = true, bool Write = false, bool DeleteToTrash = false);

/// <summary>Exactly one of UserId or TeamId.</summary>
public class SpaceMember : OrgEntity
{
    public Guid SpaceId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TeamId { get; set; }
    public SpaceRole Role { get; set; }
}

public class Page : OrgEntity
{
    public Guid SpaceId { get; set; }
    /// <summary>Same space as this page (composite FK on OrgId, SpaceId, ParentId).</summary>
    public Guid? ParentId { get; set; }
    public double Position { get; set; }
    public required string Title { get; set; }
    /// <summary>jsonb, the ProseMirror projection.</summary>
    public string? ContentJson { get; set; }
    public string? ContentMd { get; set; }
    public byte[]? Ydoc { get; set; }
    public long Revision { get; set; }
    public long LiveEpoch { get; set; }
    public long LiveSeq { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>
    /// Root-first ids of the ancestors (not this page). Maintained by AtharDbContext on tracked creates and moves, so access
    /// predicates can test ancestor restrictions in one query (FR-AC-01). Bulk moves call RecomputeAncestryAsync. Never set it by hand.
    /// </summary>
    public Guid[] AncestorIds { get; set; } = [];
    /// <summary>Phase U (U-1): a draft is visible only to people who can edit it. Published is the CLR default on purpose: EF
    /// treats the default as "not set" and lets the column default apply, which must never turn a draft into a published page.</summary>
    public PageStatus Status { get; set; } = PageStatus.Published;
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? PublishedBy { get; set; }
    /// <summary>Who made the page (the byline); null for pages made before Phase U.</summary>
    public Guid? CreatedBy { get; set; }
}

public enum PageStatus { Published, Draft }

/// <summary>Someone watching a page (U-3): notified of its updates, comments and trashing.</summary>
public class PageWatch : OrgEntity
{
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>The per-revision audit unit (FR-RT-04).</summary>
public class PageRevision : OrgEntity
{
    public Guid PageId { get; set; }
    public long Revision { get; set; }
    public long LiveSeq { get; set; }
    public Guid[] Contributors { get; set; } = [];
    public Guid[] AgentCredentials { get; set; } = [];
    public string[] BatchIds { get; set; } = [];
    public string[] OpIds { get; set; } = [];
}

public enum RestrictionLevel { View, Edit }

/// <summary>Exactly one of UserId or TeamId.</summary>
public class PageRestriction : OrgEntity
{
    public Guid PageId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TeamId { get; set; }
    public RestrictionLevel Level { get; set; }
}

public enum ShareLevel { View, Comment, Edit }

/// <summary>
/// A direct share of exactly this page (not its children, in the pilot) with a user or a team (FR-SH-01, FR-AC-01).
/// It grants access only to workspace members. Exactly one of UserId or TeamId.
/// </summary>
public class PageShare : OrgEntity
{
    public Guid PageId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TeamId { get; set; }
    public ShareLevel Level { get; set; }
    public Guid? CreatedBy { get; set; }
}

public enum VersionReason { Auto, Manual, Restore }

public class PageVersion : OrgEntity
{
    public Guid PageId { get; set; }
    public long Revision { get; set; }
    public string? ContentJson { get; set; }
    public string? ContentMd { get; set; }
    public VersionReason Reason { get; set; }
    public Guid? CreatedBy { get; set; }
}

public enum ThreadStatus { PendingAnchor, Open, Resolved, Orphaned, AnchorFailed }

public class CommentThread : OrgEntity
{
    public Guid PageId { get; set; }
    /// <summary>Unique per org. Null for page-level threads.</summary>
    public string? AnchorId { get; set; }
    public string? Quote { get; set; }
    public ThreadStatus Status { get; set; }
    public string? OpId { get; set; }
    public Guid? ResolvedBy { get; set; }
}

public class Comment : OrgEntity
{
    public Guid ThreadId { get; set; }
    public Guid AuthorId { get; set; }
    public required string BodyMd { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public enum MentionSource { Comment, Page }

public class Mention : OrgEntity
{
    public MentionSource SourceKind { get; set; }
    public Guid SourceId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>Exactly one of PageId or SpaceId.</summary>
public class ShareLink : OrgEntity
{
    public Guid? PageId { get; set; }
    public Guid? SpaceId { get; set; }
    public bool IncludeDescendants { get; set; }
    public required string TokenHash { get; set; }
    public string? PasswordHash { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public class Notification : OrgEntity
{
    public Guid UserId { get; set; }
    /// <summary>Who caused it (the comment author, the sharer); null on notifications from before it was recorded.</summary>
    public Guid? ActorUserId { get; set; }
    public required string Kind { get; set; }
    public required string Ref { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public enum ExtractStatus { Pending, Done, Failed, Skipped }

public class Attachment : OrgEntity
{
    public Guid PageId { get; set; }
    public required string FileName { get; set; }
    public required string MimeSniffed { get; set; }
    public long Size { get; set; }
    /// <summary>Random, never derived from the file name.</summary>
    public required string StorageKey { get; set; }
    public string? TextContent { get; set; }
    public ExtractStatus ExtractStatus { get; set; }
}

/// <summary>Append-only (the interceptor arrives with T014). No FKs, so events outlive what they describe.</summary>
public class AuditEvent : OrgEntity
{
    public Guid? ActorUserId { get; set; }
    public Guid? CredentialId { get; set; }
    public Guid? OauthClientId { get; set; }
    /// <summary>Untrusted label the client claimed.</summary>
    public string? ClaimedClient { get; set; }
    public required string Action { get; set; }
    public required string ResourceType { get; set; }
    public required string ResourceId { get; set; }
    /// <summary>jsonb.</summary>
    public string? Before { get; set; }
    /// <summary>jsonb.</summary>
    public string? After { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}
