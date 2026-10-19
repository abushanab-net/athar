using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athar.Data;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athar.Web.Services;

// Contract shapes from docs/06 §6.2, serialized snake_case (McpEndpoint.Json).
// Nullable members default to null so the generated output schema marks them optional: nulls are omitted on the wire.
public sealed record RememberResult(string Result, Guid? MemoryId = null, Guid? ProposalId = null);
public sealed record UpdateResult(string Result, int? Revision = null, Guid? ProposalId = null);
public sealed record ShadowResult(Guid MemoryId, int BaseRevision);
public sealed record RecallResult(bool Degraded, IReadOnlyList<RecallGroup> Groups);
public sealed record RecallGroup(IReadOnlyList<RecallItem> Items, Guid? ProjectId = null);
public sealed record RecallItem(string Kind, Guid Id, string Title, string Snippet, string Scope, string Status,
    RecallAnnotations Annotations, Provenance Provenance, string Url);
public sealed record RecallAnnotations(OverrideAnnotation? Override = null, bool? WithheldOverride = null,
    ConflictAnnotation? Conflict = null, bool? Pending = null, bool? Binding = null);
public sealed record OverrideAnnotation(Guid BaseId, bool Stale);
public sealed record ConflictAnnotation(Guid? OtherId = null);
public sealed record Provenance(string Origin, IReadOnlyList<string> Links, Guid? Author = null, string? CredentialKind = null);

public sealed record RememberInput(string Title, string BodyMd, string Type, string Scope, string OpId, Guid? OwnerId, string[]? Links);
public sealed record UpdateInput(Guid MemoryId, string? Title, string? BodyMd, int ExpectedRevision, string OpId);
public sealed record ShadowInput(Guid CompanyMemoryId, Guid ProjectId, string BodyMd, string Reason, string OpId);
public sealed record RecallInput(string Query, Guid[]? ProjectIds, string[]? Scopes, string[]? Types, int Limit, string? RepoRemote = null);

/// <summary>A domain failure carrying a docs/06 §6.6 code.</summary>
public sealed class AtharError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Memory use cases (FR-MM-01..04). Every write runs in one transaction: access re-checked inside it, the mutation,
/// the audit row and the op_id row, committed last.
/// </summary>
// ponytail: Athar.Domain.Access (T012) and AuditWriter (T014) don't exist yet. Visibility is MemoryRules.Visible and the
// audit row is written inline in the same transaction. Swap both in when those tasks land.
public sealed partial class MemoryService(AtharDbContext db, ProjectService projects, SearchService search)
{
    public Task<RememberResult> RememberAsync(Actor actor, RememberInput input, CancellationToken ct)
    {
        Require(actor, "memory:write");
        NotBlank(input.Title, "title");
        var type = ParseEnum<MemoryType>(input.Type, "type");
        var scope = ParseEnum<Scope>(input.Scope, "scope");
        if (scope is Scope.Team or Scope.Project && input.OwnerId is null)
            throw new AtharError("invalid_input", $"owner_id is required for {input.Scope} scope");

        return WriteOnce(actor, input.OpId, new { tool = "remember", input = input with { OpId = "" } }, async who =>
        {
            var owner = scope == Scope.Personal ? actor.UserId : input.OwnerId;
            switch (MemoryRules.CanWrite(who, scope, owner))
            {
                case WriteDecision.Deny:
                    throw new AtharError("forbidden", "You can't write memories in that scope.");
                case WriteDecision.Propose:
                    var p = AddProposal(actor, ProposalKind.New, null, null, input.Title, input.BodyMd, input.Type, input.Links);
                    return (new RememberResult("proposed", ProposalId: p.Id), OperationTarget.Proposal, p.Id);
                default:
                    var m = AddMemory(actor.UserId, actor.CredentialId, scope, owner, type, input.Title, input.BodyMd, "remember", input.Links);
                    Audit(actor, "memory.create", "memory", m.Id, null, new { m.Title, scope = input.Scope, type = input.Type });
                    return (new RememberResult("published", MemoryId: m.Id), OperationTarget.Memory, m.Id);
            }
        }, ct);
    }

    /// <summary><c>update_memory</c>: a new revision if the caller can publish, otherwise a replacement proposal. 409 when the revision has moved.</summary>
    public Task<UpdateResult> UpdateAsync(Actor actor, UpdateInput input, CancellationToken ct)
    {
        Require(actor, "memory:write");
        if (input.Title is null && input.BodyMd is null) throw new AtharError("invalid_input", "title or body_md is required");
        if (input.Title is not null) NotBlank(input.Title, "title");

        return WriteOnce(actor, input.OpId, new { tool = "update_memory", input = input with { OpId = "" } }, async who =>
        {
            var m = await FindVisible(who, input.MemoryId, ct);
            if (m.Revision != input.ExpectedRevision) throw RevisionMoved(m);
            switch (MemoryRules.CanWrite(who, m.Scope, MemoryRules.OwnerOf(m)))
            {
                case WriteDecision.Deny:
                    throw new AtharError("forbidden", "You can't edit this memory.");
                case WriteDecision.Propose:
                    var p = AddProposal(actor, ProposalKind.Replace, m.Id, m.Revision, input.Title ?? m.Title, input.BodyMd ?? m.BodyMd, Snake(m.Type), null,
                        confidential: m.IsConfidential);
                    return (new UpdateResult("proposed", ProposalId: p.Id), OperationTarget.Proposal, p.Id);
                default:
                    if (!await ReviseAsync(actor, m, input.ExpectedRevision, input.Title, input.BodyMd, null, ct)) throw RevisionMoved(m);
                    return (new UpdateResult("published", Revision: input.ExpectedRevision + 1), OperationTarget.Memory, m.Id);
            }
        }, ct);
    }

    /// <summary><c>shadow_memory</c> (FR-MM-03): a project admin overrides an overridable company memory for one project.</summary>
    public Task<ShadowResult> ShadowAsync(Actor actor, ShadowInput input, CancellationToken ct)
    {
        Require(actor, "memory:write");
        NotBlank(input.BodyMd, "body_md");
        NotBlank(input.Reason, "reason");

        return WriteOnce(actor, input.OpId, new { tool = "shadow_memory", input = input with { OpId = "" } }, async who =>
        {
            var company = await FindVisible(who, input.CompanyMemoryId, ct);
            if (company.Scope != Scope.Company) throw new AtharError("not_found", "No such company memory.");
            if (!MemoryRules.CanShadow(who, input.ProjectId)) throw new AtharError("forbidden", "Only a project admin can shadow a company memory.");
            if (!company.Overridable) throw new AtharError("not_overridable", "This company memory can't be overridden.");
            if (await db.MemoryShadows.Where(s => s.ProjectId == input.ProjectId && s.CompanyMemoryId == company.Id && s.DeletedAt == null)
                    .Select(s => (Guid?)s.ShadowMemoryId).FirstOrDefaultAsync(ct) is { } existing)
                throw new AtharError("conflict_revision", $"This project already shadows that memory: edit memory {existing} instead.");

            var m = AddMemory(actor.UserId, actor.CredentialId, Scope.Project, input.ProjectId, company.Type, company.Title, input.BodyMd, "shadow", null,
                company.IsConfidential);
            var shadow = new MemoryShadow { ProjectId = input.ProjectId, CompanyMemoryId = company.Id, ShadowMemoryId = m.Id, BaseRevision = company.Revision };
            db.MemoryShadows.Add(shadow);
            Audit(actor, "memory.shadow", "memory", m.Id, null, new { company_memory_id = company.Id, project_id = input.ProjectId, base_revision = company.Revision, reason = input.Reason });
            return (new ShadowResult(m.Id, company.Revision), OperationTarget.Shadow, shadow.Id);
        }, ct);
    }

    // ---- shared write helpers ----

    /// <summary>
    /// Durable, idempotent write (FR-RT-07): the same actor and payload get the stored result, another payload gets
    /// <c>op_id_reused</c>, another actor gets <c>not_found</c>. The work, its audit row and the op_id row commit together.
    /// </summary>
    private async Task<T> WriteOnce<T>(Actor actor, string opId, object payload,
        Func<MemoryActor, Task<(T Result, OperationTarget Kind, Guid Target)>> work, CancellationToken ct) where T : class
    {
        NotBlank(opId, "op_id");
        db.CurrentOrgId = actor.OrgId;
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
        if (await Stored<T>(actor, opId, hash, ct) is { } stored) return stored;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Memberships and knowledge-owner status are read inside the transaction (FR-MM-04).
            var (result, kind, target) = await work(await LoadActor(actor, ct));
            db.Operations.Add(new Operation
            {
                OpId = opId, ActorUserId = actor.UserId, CredentialId = actor.CredentialId, RequestHash = hash,
                TargetKind = kind, TargetId = target.ToString(), Status = OperationStatus.Done,
                Result = JsonSerializer.Serialize(result, McpEndpoint.Json),
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            // A concurrent request with the same op_id won: answer with what it stored. Any other unique key lost a race.
            if (pg.ConstraintName?.Contains("op_id") == true)
                return await Stored<T>(actor, opId, hash, ct) ?? throw new AtharError("pending", "The operation is still in progress.");
            throw new AtharError("conflict_revision", "A concurrent change won; reload and retry.");
        }
    }

    private async Task<T?> Stored<T>(Actor actor, string opId, string hash, CancellationToken ct) where T : class
    {
        var op = await db.Operations.AsNoTracking().FirstOrDefaultAsync(o => o.OpId == opId, ct);
        if (op is null) return null;
        // Another actor's op_id looks like nothing at all.
        if (op.ActorUserId != actor.UserId) throw new AtharError("not_found", "Unknown op_id.");
        if (op.RequestHash != hash) throw new AtharError("op_id_reused", "This op_id was already used with a different payload.");
        // A replay gets a fresh access and disclosure check (FR-RT-07, FR-CL-01): if what it made has since become hidden,
        // the stored result (and its IDs) isn't returned.
        if (!await TargetVisible(await LoadActor(actor, ct), op, ct)) throw new AtharError("not_found", "Unknown op_id.");
        return JsonSerializer.Deserialize<T>(op.Result!, McpEndpoint.Json);
    }

    private async Task<bool> TargetVisible(MemoryActor who, Operation op, CancellationToken ct)
    {
        if (!Guid.TryParse(op.TargetId, out var id)) return true;
        return op.TargetKind switch
        {
            OperationTarget.Memory => await db.Memories.Where(MemoryRules.Visible(who)).AnyAsync(m => m.Id == id, ct),
            OperationTarget.Shadow => await db.MemoryShadows.Where(s => s.Id == id)
                .AnyAsync(s => db.Memories.Where(MemoryRules.Visible(who)).Any(m => m.Id == s.ShadowMemoryId), ct),
            OperationTarget.Proposal => await db.MemoryProposals.AnyAsync(p => p.Id == id && (!who.ExternalAgent || !p.IsConfidential), ct),
            _ => true,
        };
    }

    /// <summary>A new memory at revision 1 with its version row, links and search chunk.</summary>
    private Memory AddMemory(Guid createdBy, Guid? credential, Scope scope, Guid? owner, MemoryType type, string title, string body,
        string origin, IEnumerable<string>? links, bool confidential = false)
    {
        var n = MemoryFactory.Create(createdBy, credential, scope, owner, type, title, body, origin, confidential, DateTimeOffset.UtcNow);
        db.Memories.Add(n.Memory);
        db.MemoryVersions.Add(n.Version);
        foreach (var link in links ?? [])
            db.MemoryLinks.Add(new MemoryLink { MemoryId = n.Memory.Id, TargetKind = "url", TargetId = link });
        db.Chunks.AddRange(n.Chunks);
        db.Jobs.Add(n.IndexJob);
        return n.Memory;
    }

    /// <summary>
    /// Moves <paramref name="m"/> from <paramref name="expected"/> to the next revision with one conditional update; false if
    /// the revision has moved. A company revision flags its active shadows stale and notifies their project admins (FR-MM-03).
    /// </summary>
    private async Task<bool> ReviseAsync(Actor actor, Memory m, int expected, string? title, string? body, string? reason, CancellationToken ct)
    {
        var (newTitle, newBody, now) = (title?.Trim() ?? m.Title, body ?? m.BodyMd, DateTimeOffset.UtcNow);
        var moved = await db.Memories.Where(x => x.Id == m.Id && x.Revision == expected && x.Status == MemoryStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision + 1)
                .SetProperty(x => x.Title, newTitle).SetProperty(x => x.BodyMd, newBody).SetProperty(x => x.UpdatedAt, now), ct);
        if (moved == 0) return false;

        db.MemoryVersions.Add(new MemoryVersion { MemoryId = m.Id, Revision = expected + 1, Title = newTitle, BodyMd = newBody, ChangedBy = actor.UserId, Reason = reason });
        // ponytail: reindex inline, one chunk per memory. T051 moves this to the embed job, keyed by owner revision.
        await db.Chunks.Where(c => c.OwnerKind == ChunkOwner.Memory && c.OwnerId == m.Id).ExecuteDeleteAsync(ct);
        AddChunk(m, newTitle, newBody, expected + 1);

        if (m.Scope == Scope.Company)
        {
            var shadows = await db.MemoryShadows.Where(s => s.CompanyMemoryId == m.Id && s.DeletedAt == null).ToListAsync(ct);
            foreach (var s in shadows) s.Stale = true;
            var projects = shadows.Select(s => s.ProjectId).ToArray();
            var admins = await db.ProjectMembers.Where(pm => projects.Contains(pm.ProjectId) && pm.Role == ProjectRole.Admin)
                .Select(pm => new { pm.UserId, pm.ProjectId }).ToListAsync(ct);
            foreach (var s in shadows)
            foreach (var a in admins.Where(a => a.ProjectId == s.ProjectId))
                db.Notifications.Add(new Notification { UserId = a.UserId, Kind = "shadow_stale", Ref = s.ShadowMemoryId.ToString() });
        }
        Audit(actor, "memory.revise", "memory", m.Id, new { m.Title, revision = expected }, new { title = newTitle, revision = expected + 1, reason });
        return true;
    }

    /// <summary>The memory's search chunks at <paramref name="revision"/> (normalized, FR-SR-05) and the job that embeds them.</summary>
    private void AddChunk(Memory m, string title, string body, int revision)
    {
        db.Chunks.AddRange(MemoryFactory.Chunks(m, title, body, revision));
        db.Jobs.Add(IndexJobs.Vectors(ChunkOwner.Memory, m.Id, revision, DateTimeOffset.UtcNow));
    }

    /// <summary>An active memory the actor can read, or <c>not_found</c> (hidden and missing look the same).</summary>
    private async Task<Memory> FindVisible(MemoryActor who, Guid id, CancellationToken ct) =>
        await db.Memories.AsNoTracking().Where(MemoryRules.Visible(who))
            .FirstOrDefaultAsync(m => m.Id == id && m.Status == MemoryStatus.Active, ct)
        ?? throw new AtharError("not_found", "No such memory.");

    private static AtharError RevisionMoved(Memory m) =>
        new("conflict_revision", $"The memory is at revision {m.Revision}; reload and retry.");

    private Task<MemoryActor> LoadActor(Actor a, CancellationToken ct) => db.MemoryActorAsync(a, ct);

    private void Audit(Actor a, string action, string type, object id, object? before, object? after) => db.AuditEvents.Add(new AuditEvent
    {
        ActorUserId = a.UserId, CredentialId = a.CredentialId, ClaimedClient = a.ClaimedClient,
        Action = action, ResourceType = type, ResourceId = id.ToString()!,
        Before = before is null ? null : JsonSerializer.Serialize(before), After = after is null ? null : JsonSerializer.Serialize(after),
    });

    private static void Require(Actor a, string scope)
    {
        if (!a.Scopes.Contains(scope)) throw new AtharError("forbidden", $"The token lacks the '{scope}' scope.");
    }

    private static void NotBlank(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new AtharError("invalid_input", $"{field} is required");
    }

    private static T ParseEnum<T>(string value, string field) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var v) && Enum.IsDefined(v) && !value.Any(char.IsDigit)
            ? v : throw new AtharError("invalid_input", $"{field} must be one of: {string.Join(", ", Enum.GetValues<T>().Select(Snake))}");

    private static string Snake<T>(T v) where T : struct, Enum => v.ToString().ToLowerInvariant();
}
