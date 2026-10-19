using Athar.Domain.Model;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Athar.Data;

/// <summary>
/// ASP.NET Identity credentials (password hash, lockout, security stamp) for a <see cref="User"/>: same Id, linked 1:1
/// by (org_id, id) (docs/04 conventions). Roles live in org_members, so there are no Identity role tables.
/// </summary>
public class AtharIdentityUser : IdentityUser<Guid>
{
    public Guid OrgId { get; set; }
}

public partial class AtharDbContext
{
    public DbSet<AtharIdentityUser> IdentityUsers => Set<AtharIdentityUser>();
    public DbSet<IdentityUserClaim<Guid>> IdentityUserClaims => Set<IdentityUserClaim<Guid>>();
    public DbSet<IdentityUserLogin<Guid>> IdentityUserLogins => Set<IdentityUserLogin<Guid>>();
    public DbSet<IdentityUserToken<Guid>> IdentityUserTokens => Set<IdentityUserToken<Guid>>();

    // What IdentityUserContext would configure, on this context (UserOnlyStore works with any DbContext).
    private static void Identity(ModelBuilder mb)
    {
        mb.Entity<AtharIdentityUser>(b =>
        {
            b.HasKey(u => u.Id);
            b.HasIndex(u => u.NormalizedUserName).IsUnique();
            b.HasIndex(u => u.NormalizedEmail);
            b.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            b.HasOne<User>().WithOne().HasForeignKey<AtharIdentityUser>(u => new { u.OrgId, u.Id });
        });
        mb.Entity<IdentityUserClaim<Guid>>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne<AtharIdentityUser>().WithMany().HasForeignKey(c => c.UserId);
        });
        mb.Entity<IdentityUserLogin<Guid>>(b =>
        {
            b.HasKey(l => new { l.LoginProvider, l.ProviderKey });
            b.HasOne<AtharIdentityUser>().WithMany().HasForeignKey(l => l.UserId);
        });
        mb.Entity<IdentityUserToken<Guid>>(b =>
        {
            b.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });
            b.HasOne<AtharIdentityUser>().WithMany().HasForeignKey(t => t.UserId);
        });
    }
}
