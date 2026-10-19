using Athar.Domain.Model;

namespace Athar.Domain.Memory;

/// <summary>A new memory and the rows that go with it; the caller adds them all in one transaction.</summary>
public sealed record NewMemory(Model.Memory Memory, MemoryVersion Version, IReadOnlyList<Chunk> Chunks, Job IndexJob);

/// <summary>
/// One way to make a memory (revision 1, its chunks and the embed job), shared by Web's <c>MemoryService</c> and the
/// worker's auto-accept (T137), so both index it the same way.
/// </summary>
public static class MemoryFactory
{
    public static NewMemory Create(Guid createdBy, Guid? credential, Scope scope, Guid? owner, MemoryType type, string title, string body,
        string origin, bool confidential, DateTimeOffset now)
    {
        var m = new Model.Memory
        {
            Scope = scope, Type = type, Title = title.Trim(), BodyMd = body, Status = MemoryStatus.Active,
            OwnerUserId = scope == Scope.Personal ? owner : null,
            OwnerTeamId = scope == Scope.Team ? owner : null,
            OwnerProjectId = scope == Scope.Project ? owner : null,
            Overridable = MemoryRules.DefaultOverridable(type), Origin = origin, IsConfidential = confidential,
            CreatedByUser = createdBy, CreatedByCredential = credential,
        };
        var version = new MemoryVersion { MemoryId = m.Id, Revision = 1, Title = m.Title, BodyMd = m.BodyMd, ChangedBy = createdBy };
        return new(m, version, Chunks(m, m.Title, m.BodyMd, 1), IndexJobs.Vectors(ChunkOwner.Memory, m.Id, 1, now));
    }

    /// <summary>A memory revision's chunks, carrying its scope keys and classification for filtering.</summary>
    public static IReadOnlyList<Chunk> Chunks(Model.Memory m, string title, string body, int revision) =>
        [.. Chunker.Build(ChunkOwner.Memory, m.Id, revision, title, body, c =>
        {
            (c.Scope, c.OwnerUserId, c.OwnerTeamId, c.OwnerProjectId) = (m.Scope, m.OwnerUserId, m.OwnerTeamId, m.OwnerProjectId);
            c.IsConfidential = m.IsConfidential;
        })];
}
