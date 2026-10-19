namespace Athar.Domain.Model;

/// <summary>Every tenant row: key (OrgId, Id), uuid v7 ids, timestamps set by the DbContext (docs/04 conventions).</summary>
public abstract class OrgEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrgId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The tenancy root, keyed by Id alone.</summary>
public class Organization
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset? BootstrapCompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum OrgRole { Owner, Admin, Member }
public enum WorkspaceRole { Admin, Member }
public enum SpaceRole { Viewer, Commenter, Editor, Admin }
public enum ProjectRole { Member, Admin }

/// <summary>Memories and skills: the owner column must match the scope (check constraint).</summary>
public enum Scope { Personal, Team, Project, Company }
