using Athar.Data;
using Athar.Domain.Model;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Auth;

/// <summary>
/// Signed-out account flows (static SSR pages and the CLI): bootstrap, invitation redemption, sign-in and password reset.
/// One request, one scoped <see cref="AtharDbContext"/>, shared with Identity's stores so each flow is one transaction.
/// </summary>
public sealed class AccountService(AtharDbContext db, UserManager<AtharIdentityUser> users, SignInManager<AtharIdentityUser> signIn, Mailer mailer)
{
    /// <summary>
    /// FR-ID-01: creates the first Owner of the (single, seeded) organization. Atomic and audited. It runs once:
    /// afterwards <see cref="Organization.BootstrapCompletedAt"/> is set and every later run fails. There's no public sign-up.
    /// </summary>
    public static async Task<Guid> BootstrapAsync(AtharDbContext db, string email, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        if (!email.Contains('@')) throw new InvalidOperationException("Give a valid --email.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The organization is the tenancy root (no org_id column); the row lock serializes concurrent bootstraps.
        var orgs = await db.Organizations.FromSql($"SELECT * FROM organizations FOR UPDATE").ToListAsync(ct);
        if (orgs is not [var org]) throw new InvalidOperationException($"Expected one organization, found {orgs.Count}. Start web once with the minimal seed.");
        if (org.BootstrapCompletedAt is not null) throw new InvalidOperationException("Bootstrap has already run for this organization.");

        db.CurrentOrgId = org.Id;
        var id = Guid.CreateVersion7();
        db.Users.Add(new User { Id = id, Email = email, DisplayName = email });
        db.OrgMembers.Add(new OrgMember { UserId = id, Role = OrgRole.Owner, IsKnowledgeOwner = true });
        db.IdentityUsers.Add(NewIdentity(id, org.Id, email));
        org.BootstrapCompletedAt = DateTimeOffset.UtcNow;
        db.Audit(org.Id, null, "org.bootstrap", "organization", org.Id, after: new { owner = id, email });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>The CLI half of bootstrap: a one-time link where the owner sets their password.</summary>
    public async Task<string> SetPasswordLinkAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("Unknown user.");
        return ResetLink(user.Email!, await users.GeneratePasswordResetTokenAsync(user));
    }

    public enum SignInOutcome { Ok, Failed, LockedOut }

    /// <summary>FR-ID-02: email and password. 5 failures lock the account for 15 minutes. Deactivated users can't sign in.</summary>
    public async Task<SignInOutcome> SignInAsync(string email, string password)
    {
        var (outcome, user) = await CheckPasswordAsync(email, password, "auth.sign_in");
        if (outcome == SignInOutcome.Ok) await signIn.SignInAsync(user!, isPersistent: false);
        return outcome;
    }

    /// <summary>
    /// The password check behind every sign-in (the app cookie and the OAuth login): lockout, deactivated users, and an
    /// audit event (FR-AU-01) named <paramref name="action"/>, <c>…_failed</c> or <c>auth.locked_out</c>. Signs nothing in.
    /// </summary>
    public async Task<(SignInOutcome, AtharIdentityUser?)> CheckPasswordAsync(string email, string password, string action)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || await IsDeactivatedAsync(user)) return (SignInOutcome.Failed, null);
        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        var outcome = result.Succeeded ? SignInOutcome.Ok : result.IsLockedOut ? SignInOutcome.LockedOut : SignInOutcome.Failed;
        // Unknown addresses have no org to audit in.
        db.Audit(user.OrgId, user.Id, outcome switch { SignInOutcome.Ok => action, SignInOutcome.LockedOut => "auth.locked_out", _ => action + "_failed" }, "user", user.Id);
        await db.SaveChangesAsync();
        return (outcome, outcome == SignInOutcome.Ok ? user : null);
    }

    public enum InviteOutcome { Ok, Invalid, Expired, Used, PasswordRejected }

    /// <summary>The email an open invitation is bound to, or null.</summary>
    public async Task<string?> InvitationEmailAsync(string token) => (await InvitationAsync(token))?.Email;

    /// <summary>The invited address and whether the invitation has expired; null when the token is unknown or already used.</summary>
    public async Task<(string Email, bool Expired)?> InvitationAsync(string token) =>
        await db.Invitations.IgnoreQueryFilters().Where(i => i.TokenHash == Tokens.Hash(token) && i.RedeemedAt == null)
            .Select(i => new { i.Email, i.ExpiresAt }).SingleOrDefaultAsync() is { } i ? (i.Email, i.ExpiresAt <= DateTimeOffset.UtcNow) : null;

    /// <summary>
    /// FR-ID-02: an invitation is single-use, expires after 7 days and is bound to its address. Redeeming creates the
    /// user, the membership and the credentials in one transaction, then signs them in. A concurrent second redemption
    /// fails on the unique (org, email) index.
    /// </summary>
    public async Task<(InviteOutcome, IEnumerable<string>)> RedeemInvitationAsync(string token, string displayName, string password, CancellationToken ct)
    {
        var invitation = await db.Invitations.IgnoreQueryFilters().SingleOrDefaultAsync(i => i.TokenHash == Tokens.Hash(token), ct);
        if (invitation is null) return (InviteOutcome.Invalid, []);
        if (invitation.RedeemedAt is not null) return (InviteOutcome.Used, []);
        if (invitation.ExpiresAt <= DateTimeOffset.UtcNow) return (InviteOutcome.Expired, []);

        db.CurrentOrgId = invitation.OrgId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = Guid.CreateVersion7();
        db.Users.Add(new User { Id = id, Email = invitation.Email, DisplayName = displayName.Trim() is { Length: > 0 } n ? n : invitation.Email });
        db.OrgMembers.Add(new OrgMember { UserId = id, Role = invitation.Role });
        invitation.RedeemedAt = DateTimeOffset.UtcNow;
        db.Audit(invitation.OrgId, id, "invitation.redeem", "invitation", invitation.Id, after: new { user = id, role = invitation.Role.ToString() });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return (InviteOutcome.Used, []); }

        var identity = NewIdentity(id, invitation.OrgId, invitation.Email);
        var created = await users.CreateAsync(identity, password);
        if (!created.Succeeded) return (InviteOutcome.PasswordRejected, created.Errors.Select(e => e.Description));
        await tx.CommitAsync(ct);

        await signIn.SignInAsync(identity, isPersistent: false);
        return (InviteOutcome.Ok, []);
    }

    /// <summary>Always looks the same to the caller, whether or not the address exists.</summary>
    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || await IsDeactivatedAsync(user)) return;
        var link = ResetLink(user.Email!, await users.GeneratePasswordResetTokenAsync(user));
        await mailer.SendAsync(user.Email!, "Reset your Athar password", $"Set a new password: {link}\n\nIf you didn't ask for this, ignore this email.", ct);
    }

    /// <summary>
    /// FR-ID-02: a reset changes the security stamp, which ends the user's other sessions on their next request, and
    /// revokes their PATs. One transaction, audited.
    /// </summary>
    public async Task<IEnumerable<string>> ResetPasswordAsync(string email, string token, string password, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null) return ["The link is invalid or has expired."];

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reset = await users.ResetPasswordAsync(user, token, password);
        if (!reset.Succeeded) return reset.Errors.Select(e => e.Code == "InvalidToken" ? "The link is invalid or has expired." : e.Description);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);

        var now = DateTimeOffset.UtcNow;
        var tokens = await db.AccessTokens.IgnoreQueryFilters().Where(t => t.OrgId == user.OrgId && t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.RevokedAt = now;
        await db.RevokeOAuthGrantsAsync(user.OrgId, user.Id, ct);
        db.Audit(user.OrgId, user.Id, "user.password_reset", "user", user.Id, after: new { revoked_tokens = tokens.Select(t => t.Id) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return [];
    }

    private Task<bool> IsDeactivatedAsync(AtharIdentityUser user) =>
        db.Users.IgnoreQueryFilters().AnyAsync(u => u.OrgId == user.OrgId && u.Id == user.Id && u.DeactivatedAt != null);

    private string ResetLink(string email, string token) =>
        $"{mailer.PublicUrl}/account/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";

    internal static AtharIdentityUser NewIdentity(Guid id, Guid orgId, string email) => new()
    {
        Id = id, OrgId = orgId, UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email,
        NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, LockoutEnabled = true,
        SecurityStamp = Guid.NewGuid().ToString("N"), ConcurrencyStamp = Guid.NewGuid().ToString("N"),
    };
}
