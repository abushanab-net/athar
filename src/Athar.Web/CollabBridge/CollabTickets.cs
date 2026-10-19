using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Athar.Web.CollabBridge;

/// <summary>Who may open a live document, and at which level (`view`, `comment` or `edit`).</summary>
public sealed record CollabGrant(Guid User, Guid? Credential, Guid Org, Guid Page, string Level);

/// <summary>
/// Collab tickets (signed, 60 s, single use for the handshake) and authorization leases (30 s, renewed by collab).
/// See docs/03 §4.1 steps 1–3.
/// </summary>
public sealed class CollabTickets(IDataProtectionProvider dp)
{
    public const int LeaseSeconds = 30;
    private static readonly TimeSpan TicketLife = TimeSpan.FromSeconds(60);
    // Collab renews every 30 s; the grace covers a slow renewal round trip.
    private static readonly TimeSpan LeaseLife = TimeSpan.FromSeconds(LeaseSeconds + 15);

    private readonly ITimeLimitedDataProtector tickets = dp.CreateProtector("Athar.Collab.Ticket").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector leases = dp.CreateProtector("Athar.Collab.Lease").ToTimeLimitedDataProtector();
    // ponytail: in-process replay set, swept on every redeem. Needs a shared store if web ever runs more than one instance.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> redeemed = new();

    private sealed record Ticket(Guid Id, CollabGrant Grant);

    public string Issue(CollabGrant grant) => tickets.Protect(JsonSerializer.Serialize(new Ticket(Guid.NewGuid(), grant)), TicketLife);

    /// <summary>Returns the grant once; a replayed, expired or forged ticket returns null.</summary>
    public CollabGrant? Redeem(string ticket)
    {
        if (Unprotect(tickets, ticket) is not { } json) return null;
        var t = JsonSerializer.Deserialize<Ticket>(json)!;
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, expires) in redeemed)
            if (expires < now) redeemed.TryRemove(id, out _);
        return redeemed.TryAdd(t.Id, now + TicketLife) ? t.Grant : null;
    }

    public string Lease(CollabGrant grant) => leases.Protect(JsonSerializer.Serialize(grant), LeaseLife);

    public CollabGrant? Renew(string lease) => Unprotect(leases, lease) is { } json ? JsonSerializer.Deserialize<CollabGrant>(json) : null;

    private static string? Unprotect(ITimeLimitedDataProtector p, string token)
    {
        try { return p.Unprotect(token); }
        catch (CryptographicException) { return null; }
    }
}
