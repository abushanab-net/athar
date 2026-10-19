using System.Text.Json;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Athar.Data;

/// <summary>
/// Where a change came from (FR-AU-02): client address and user agent, the credential (PAT or OAuth grant) and OAuth
/// client, and the agent label the client claimed, which is untrusted and stored apart. The web host sets it per request
/// or circuit through <see cref="AtharDbContext.OriginSource"/>; every audit event added in that unit of work gets it.
/// </summary>
public sealed record AuditOrigin(string? Ip, string? UserAgent, Guid? CredentialId = null, Guid? OAuthClientId = null, string? ClaimedClient = null);

/// <summary>
/// Every mutation writes an audit event in the same transaction (FR-AU-01): add it to the unit of work that
/// makes the change, before SaveChanges. Audit rows are append-only (<see cref="AuditImmutability"/>); only
/// <see cref="AuditRetention"/> deletes them.
/// </summary>
public static class AuditWriter
{
    public static void Audit(this AtharDbContext db, Guid orgId, Guid? actorUserId, string action, string resourceType, object resourceId,
        object? before = null, object? after = null, Guid? credentialId = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OrgId = orgId, ActorUserId = actorUserId, CredentialId = credentialId, Action = action,
            ResourceType = resourceType, ResourceId = resourceId.ToString()!,
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = after is null ? null : JsonSerializer.Serialize(after),
        });
}

/// <summary>AT-56: a tracked update or delete of an audit event fails the whole SaveChanges.</summary>
public sealed class AuditImmutability : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Check(DbContext? db)
    {
        if (db?.ChangeTracker.Entries<AuditEvent>().FirstOrDefault(e => e.State is EntityState.Modified or EntityState.Deleted) is { } e)
            throw new InvalidOperationException($"Audit events are append-only: refused to {(e.State == EntityState.Deleted ? "delete" : "update")} audit event {e.Entity.Id}.");
    }
}
