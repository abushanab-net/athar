using System.Text.Json;
using System.Text.Json.Serialization;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Auth;

/// <summary>
/// Personal access tokens (FR-ID-06, docs/06 §6.1). Anyone creates them for themselves, always as <c>external-agent</c>;
/// the plaintext is shown once and only its SHA-256 hash is kept. Owners and org admins revoke; only org owners
/// reclassify a token as <c>trusted-local</c>, with a reason. Every change is audited.
/// </summary>
public sealed class TokenService(IDbContextFactory<AtharDbContext> dbs)
{
    public static readonly string[] AllScopes = ["read", "write", "memory:write", "pages:delete"];
    public const int DefaultDays = 90;

    public sealed record TokenRow(Guid Id, Guid UserId, string Owner, string Name, string[] Scopes, DisclosureClass Disclosure,
        DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, string? ClassificationReason);

    public sealed record Created(Guid Id, string Token, DateTimeOffset? ExpiresAt);

    /// <summary><paramref name="expiresInDays"/>: null for the 90-day default, 0 for no expiry, at most 365.</summary>
    public async Task<(OrgDecision, Created?)> CreateAsync(Guid orgId, Guid userId, string name, IReadOnlyCollection<string> scopes, int? expiresInDays, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || scopes.Count == 0 || scopes.Except(AllScopes).Any() || expiresInDays is < 0 or > 365) return (OrgDecision.Invalid, null);
        await using var db = await OpenAsync(orgId, ct);
        if (await OrgAdminService.ActorRoleAsync(db, userId, ct) is null) return (OrgDecision.Forbidden, null);

        var token = PatAuth.Prefix + Tokens.New();
        var days = expiresInDays ?? DefaultDays;
        var row = new AccessToken
        {
            UserId = userId, Name = name.Trim(), TokenHash = Tokens.Hash(token), Scopes = [.. scopes.Distinct()],
            DisclosureClass = DisclosureClass.ExternalAgent, ExpiresAt = days == 0 ? null : DateTimeOffset.UtcNow.AddDays(days),
        };
        db.AccessTokens.Add(row);
        db.Audit(orgId, userId, "token.create", "token", row.Id, after: new { row.Name, row.Scopes, row.ExpiresAt, disclosure = "external-agent" });
        await db.SaveChangesAsync(ct);
        return (OrgDecision.Allow, new(row.Id, token, row.ExpiresAt));
    }

    public async Task<List<TokenRow>> MineAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        return await Rows(db, t => t.UserId == userId).ToListAsync(ct);
    }

    /// <summary>Every token in the org, for owners and admins; null for anyone else.</summary>
    public async Task<List<TokenRow>?> AllAsync(Guid orgId, Guid actorId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        return OrgRules.IsAdmin(await OrgAdminService.ActorRoleAsync(db, actorId, ct)) ? await Rows(db, t => true).ToListAsync(ct) : null;
    }

    /// <summary>The token's owner or an org admin. Takes effect on the token's next request (FR-ID-08).</summary>
    public Task<OrgDecision> RevokeAsync(Guid orgId, Guid actorId, Guid tokenId, CancellationToken ct = default) =>
        ChangeAsync(orgId, tokenId, ct, async (db, token) =>
        {
            if (token.UserId != actorId && !OrgRules.IsAdmin(await OrgAdminService.ActorRoleAsync(db, actorId, ct))) return OrgDecision.Forbidden;
            if (token.RevokedAt is not null) return OrgDecision.Allow;
            token.RevokedAt = DateTimeOffset.UtcNow;
            db.Audit(orgId, actorId, "token.revoke", "token", token.Id, after: new { token.Name, owner = token.UserId });
            return OrgDecision.Allow;
        });

    /// <summary>FR-ID-06: owners only, with a reason; for clients that run entirely on company-controlled hosts and models.</summary>
    public Task<OrgDecision> ClassifyAsync(Guid orgId, Guid actorId, Guid tokenId, DisclosureClass disclosure, string? reason, CancellationToken ct = default) =>
        ChangeAsync(orgId, tokenId, ct, async (db, token) =>
        {
            var decision = OrgRules.ClassifyToken(await OrgAdminService.ActorRoleAsync(db, actorId, ct), reason);
            if (decision != OrgDecision.Allow || token.DisclosureClass == disclosure) return decision;
            db.Audit(orgId, actorId, "token.classify", "token", token.Id,
                new { disclosure = token.DisclosureClass.ToString() }, new { disclosure = disclosure.ToString(), reason = reason!.Trim(), owner = token.UserId });
            token.DisclosureClass = disclosure;
            token.ClassifiedBy = actorId;
            token.ClassificationReason = reason.Trim();
            return decision;
        });

    // Filter before projecting: EF can't translate a filter over the TokenRow constructor.
    public sealed record GrantRow(Guid Id, string Client, string[] Scopes, DateTimeOffset? CreatedAt);

    /// <summary>The user's OAuth grants (persistent consents) still in force: the apps connected as them.</summary>
    public async Task<List<GrantRow>> GrantsAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var subject = userId.ToString();
        var rows = await db.OauthAuthorizations.Where(a => a.OrgId == orgId && a.Subject == subject && a.Status == "valid")
            .Select(a => new { a.Id, Client = a.Application!.DisplayName ?? a.Application.ClientId!, a.Scopes, a.CreationDate }).ToListAsync(ct);
        return rows.Select(r => new GrantRow(r.Id, r.Client, JsonSerializer.Deserialize<string[]>(r.Scopes ?? "[]")!,
            r.CreationDate is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : null)).ToList();
    }

    /// <summary>Disconnects an app: the grant and every token issued under it stop working on their next use (FR-ID-08).</summary>
    public Task<OrgDecision> RevokeGrantAsync(Guid orgId, Guid actorId, Guid grantId, CancellationToken ct = default) =>
        InTxAsync(orgId, ct, async db =>
        {
            var grant = await db.OauthAuthorizations.SingleOrDefaultAsync(a => a.Id == grantId && a.OrgId == orgId, ct);
            if (grant is null || grant.Subject != actorId.ToString() && !OrgRules.IsAdmin(await OrgAdminService.ActorRoleAsync(db, actorId, ct)))
                return OrgDecision.Forbidden;
            grant.Status = "revoked";
            foreach (var token in await db.OauthTokens.Where(t => t.Authorization!.Id == grantId && t.Status == "valid").ToListAsync(ct))
                token.Status = "revoked";
            db.Audit(orgId, actorId, "oauth.revoke", "oauth_grant", grantId, after: new { subject = grant.Subject });
            return OrgDecision.Allow;
        });

    private async Task<OrgDecision> InTxAsync(Guid orgId, CancellationToken ct, Func<AtharDbContext, Task<OrgDecision>> change)
    {
        await using var db = await OpenAsync(orgId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var decision = await change(db);
        if (decision != OrgDecision.Allow) return decision;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return decision;
    }

    private static IQueryable<TokenRow> Rows(AtharDbContext db, System.Linq.Expressions.Expression<Func<AccessToken, bool>> filter) =>
        from t in db.AccessTokens.Where(filter)
        join u in db.Users on t.UserId equals u.Id
        orderby t.CreatedAt descending
        select new TokenRow(t.Id, t.UserId, u.DisplayName, t.Name, t.Scopes, t.DisclosureClass, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt, t.ClassificationReason);

    private async Task<AtharDbContext> OpenAsync(Guid orgId, CancellationToken ct)
    {
        var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = orgId;
        return db;
    }

    private async Task<OrgDecision> ChangeAsync(Guid orgId, Guid tokenId, CancellationToken ct, Func<AtharDbContext, AccessToken, Task<OrgDecision>> change)
    {
        await using var db = await OpenAsync(orgId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.AccessTokens.SingleOrDefaultAsync(t => t.Id == tokenId, ct) is not { } token) return OrgDecision.Forbidden;
        var decision = await change(db, token);
        if (decision != OrgDecision.Allow) return decision;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return decision;
    }
}
