using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Athar.Data;

// OpenIddict's entities with an org_id (docs/04, FR-ID-07). Clients are pre-registered per org; authorizations (grants,
// which are the persistent consents) and tokens take the org of their client. OpenIddict keeps its own keys and relations.

public class OAuthApplication : OpenIddictEntityFrameworkCoreApplication<Guid, OAuthAuthorization, OAuthToken>
{
    public Guid OrgId { get; set; }
}

public class OAuthAuthorization : OpenIddictEntityFrameworkCoreAuthorization<Guid, OAuthApplication, OAuthToken>
{
    public Guid OrgId { get; set; }
}

/// <summary>Unused (scopes are registered in code), but OpenIddict's model needs the type.</summary>
public class OAuthScope : OpenIddictEntityFrameworkCoreScope<Guid>
{
    public Guid OrgId { get; set; }
}

public class OAuthToken : OpenIddictEntityFrameworkCoreToken<Guid, OAuthApplication, OAuthAuthorization>
{
    public Guid OrgId { get; set; }
}

public partial class AtharDbContext
{
    public DbSet<OAuthApplication> OauthApplications => Set<OAuthApplication>();
    public DbSet<OAuthAuthorization> OauthAuthorizations => Set<OAuthAuthorization>();
    public DbSet<OAuthScope> OauthScopes => Set<OAuthScope>();
    public DbSet<OAuthToken> OauthTokens => Set<OAuthToken>();

    private static void OAuth(ModelBuilder mb)
    {
        mb.UseOpenIddict<OAuthApplication, OAuthAuthorization, OAuthScope, OAuthToken, Guid>();
        mb.Entity<OAuthApplication>(b => { b.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrgId); b.HasIndex(e => e.OrgId); });
        mb.Entity<OAuthAuthorization>(b => { b.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrgId); b.HasIndex(e => new { e.OrgId, e.Subject }); });
        mb.Entity<OAuthScope>(b => b.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrgId));
        mb.Entity<OAuthToken>(b => { b.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrgId); b.HasIndex(e => e.OrgId); });
    }

    /// <summary>
    /// FR-ID-08: revokes every OAuth grant of the user and the tokens issued under them. With token and authorization
    /// entry validation on, the next request with any of them fails. Part of the caller's unit of work.
    /// </summary>
    public async Task RevokeOAuthGrantsAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        var subject = userId.ToString();
        foreach (var grant in await OauthAuthorizations.Where(a => a.OrgId == orgId && a.Subject == subject && a.Status == "valid").ToListAsync(ct))
            grant.Status = "revoked";
        foreach (var token in await OauthTokens.Where(t => t.OrgId == orgId && t.Subject == subject && t.Status == "valid").ToListAsync(ct))
            token.Status = "revoked";
    }

    // OpenIddict creates grants and tokens without knowing about orgs: they take their client's org.
    private static void StampOAuth(object entity)
    {
        switch (entity)
        {
            case OAuthAuthorization { OrgId: var o } a when o == Guid.Empty:
                a.OrgId = a.Application?.OrgId ?? throw new InvalidOperationException("An OAuth grant needs its client.");
                break;
            case OAuthToken { OrgId: var o } t when o == Guid.Empty:
                t.OrgId = t.Application?.OrgId ?? t.Authorization?.OrgId ?? throw new InvalidOperationException("An OAuth token needs its client.");
                break;
        }
    }
}
