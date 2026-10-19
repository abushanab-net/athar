using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Auth;

/// <summary>
/// Org administration (FR-ID-02..04) for the admin pages. Each call is one unit of work: a fresh context, the caller's
/// role re-read inside the transaction (FR-AC-02, no decision cache), the organization row locked for owner-affecting
/// changes (AT-03), the rule from <see cref="OrgRules"/>, the change and its audit event, then commit.
/// </summary>
public sealed class OrgAdminService(IDbContextFactory<AtharDbContext> dbs, Mailer mailer)
{
    public sealed record MemberRow(Guid UserId, string Email, string DisplayName, OrgRole Role, bool IsKnowledgeOwner, bool Deactivated);
    public sealed record TeamRow(Guid Id, string Name, IReadOnlyList<Guid> MemberIds);
    public sealed record InvitationRow(Guid Id, string Email, OrgRole Role, DateTimeOffset SentAt, DateTimeOffset ExpiresAt);

    public async Task<OrgRole?> RoleOfAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        return await ActorRoleAsync(db, userId, ct);
    }

    public async Task<List<MemberRow>> MembersAsync(Guid orgId, Guid actorId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        if (!OrgRules.IsAdmin(await ActorRoleAsync(db, actorId, ct))) return [];
        return await (from m in db.OrgMembers
                      join u in db.Users on m.UserId equals u.Id
                      orderby u.DisplayName
                      select new MemberRow(u.Id, u.Email, u.DisplayName, m.Role, m.IsKnowledgeOwner, u.DeactivatedAt != null)).ToListAsync(ct);
    }

    public Task<OrgDecision> ChangeRoleAsync(Guid orgId, Guid actorId, Guid userId, OrgRole role, CancellationToken ct = default) =>
        InOrgLockAsync(orgId, actorId, ct, async (db, actor) =>
        {
            var member = await db.OrgMembers.SingleAsync(m => m.UserId == userId, ct);
            var decision = OrgRules.ChangeRole(actor, member.Role, role, await ActiveOwnersAsync(db, ct));
            if (decision != OrgDecision.Allow || member.Role == role) return decision;
            db.Audit(orgId, actorId, "org_member.role", "user", userId, new { role = member.Role.ToString() }, new { role = role.ToString() });
            member.Role = role;
            return decision;
        });

    public Task<OrgDecision> SetKnowledgeOwnerAsync(Guid orgId, Guid actorId, Guid userId, bool value, CancellationToken ct = default) =>
        InOrgLockAsync(orgId, actorId, ct, async (db, actor) =>
        {
            var decision = OrgRules.SetKnowledgeOwner(actor);
            if (decision != OrgDecision.Allow) return decision;
            var member = await db.OrgMembers.SingleAsync(m => m.UserId == userId, ct);
            db.Audit(orgId, actorId, "org_member.knowledge_owner", "user", userId, new { member.IsKnowledgeOwner }, new { IsKnowledgeOwner = value });
            member.IsKnowledgeOwner = value;
            return decision;
        });

    /// <summary>Deactivation also rotates the security stamp, so open cookies fail on their next request (FR-ID-08). PATs check deactivation per request.</summary>
    public Task<OrgDecision> SetDeactivatedAsync(Guid orgId, Guid actorId, Guid userId, bool deactivate, CancellationToken ct = default) =>
        InOrgLockAsync(orgId, actorId, ct, async (db, actor) =>
        {
            var member = await db.OrgMembers.SingleAsync(m => m.UserId == userId, ct);
            var decision = deactivate
                ? OrgRules.Deactivate(actor, member.Role, await ActiveOwnersAsync(db, ct))
                : OrgRules.Reactivate(actor, member.Role);
            if (decision != OrgDecision.Allow) return decision;
            var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
            if (deactivate == (user.DeactivatedAt is not null)) return decision;
            user.DeactivatedAt = deactivate ? DateTimeOffset.UtcNow : null;
            var identity = await db.IdentityUsers.SingleAsync(i => i.Id == userId, ct);
            identity.SecurityStamp = Guid.NewGuid().ToString("N");
            if (deactivate) await db.RevokeOAuthGrantsAsync(orgId, userId, ct);
            if (deactivate) await SessionRetention.PurgeOwnerAsync(db, userId, ct); // FR-AG-04: transcripts go at once
            db.Audit(orgId, actorId, deactivate ? "user.deactivate" : "user.reactivate", "user", userId);
            return decision;
        });

    /// <summary>FR-ID-02: a single-use invitation bound to <paramref name="email"/>, valid 7 days, sent by email.</summary>
    public async Task<OrgDecision> InviteAsync(Guid orgId, Guid actorId, string email, OrgRole role, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        var token = Tokens.New();
        var decision = await InOrgLockAsync(orgId, actorId, ct, async (db, actor) =>
        {
            var d = OrgRules.Invite(actor, role);
            if (d != OrgDecision.Allow) return d;
            if (await db.Users.AnyAsync(u => u.Email == email, ct)) return OrgDecision.Forbidden;
            var invitation = new Invitation { Email = email, Role = role, TokenHash = Tokens.Hash(token), ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) };
            db.Invitations.Add(invitation);
            db.Audit(orgId, actorId, "invitation.create", "invitation", invitation.Id, after: new { email, role = role.ToString() });
            return d;
        });
        if (decision == OrgDecision.Allow)
            await mailer.SendAsync(email, "You're invited to Athar",
                $"You've been invited to Athar. Accept within 7 days: {mailer.PublicUrl}/account/accept-invite?token={token}", ct);
        return decision;
    }

    /// <summary>Invitations not accepted yet, pending or expired, newest first (the Users page). Admins only.</summary>
    public async Task<List<InvitationRow>> InvitationsAsync(Guid orgId, Guid actorId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        if (!OrgRules.IsAdmin(await ActorRoleAsync(db, actorId, ct))) return [];
        return await db.Invitations.Where(i => i.RedeemedAt == null).OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationRow(i.Id, i.Email, i.Role, i.CreatedAt, i.ExpiresAt)).ToListAsync(ct);
    }

    /// <summary>A new link for an invitation that hasn't been accepted: the old link stops working and the 7 days start again.</summary>
    public async Task<OrgDecision> ResendInvitationAsync(Guid orgId, Guid actorId, Guid invitationId, CancellationToken ct = default)
    {
        var token = Tokens.New();
        string? email = null;
        var decision = await InOrgLockAsync(orgId, actorId, ct, async (db, actor) =>
        {
            var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.RedeemedAt == null, ct);
            if (invitation is null) return OrgDecision.Invalid;
            var d = OrgRules.Invite(actor, invitation.Role);
            if (d != OrgDecision.Allow) return d;
            invitation.TokenHash = Tokens.Hash(token);
            invitation.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
            email = invitation.Email;
            db.Audit(orgId, actorId, "invitation.resend", "invitation", invitation.Id, after: new { invitation.Email });
            return d;
        });
        if (decision == OrgDecision.Allow)
            await mailer.SendAsync(email!, "You're invited to Athar",
                $"You've been invited to Athar. Accept within 7 days: {mailer.PublicUrl}/account/accept-invite?token={token}", ct);
        return decision;
    }

    /// <summary>Withdraws an invitation that hasn't been accepted; its link stops working.</summary>
    public Task<OrgDecision> RevokeInvitationAsync(Guid orgId, Guid actorId, Guid invitationId, CancellationToken ct = default) =>
        InOrgLockAsync(orgId, actorId, ct, async (db, actor) =>
        {
            var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.RedeemedAt == null, ct);
            if (invitation is null) return OrgDecision.Invalid;
            var d = OrgRules.Invite(actor, invitation.Role);
            if (d != OrgDecision.Allow) return d;
            db.Invitations.Remove(invitation);
            db.Audit(orgId, actorId, "invitation.revoke", "invitation", invitation.Id, before: new { invitation.Email, role = invitation.Role.ToString() });
            return d;
        });

    /// <summary>Display names for user ids in the org (names are shown to every member next to their work, e.g. comments).</summary>
    public async Task<Dictionary<Guid, string>> NamesAsync(Guid orgId, IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        await using var db = await OpenAsync(orgId, ct);
        return await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public async Task<List<TeamRow>> TeamsAsync(Guid orgId, Guid actorId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        if (!OrgRules.IsAdmin(await ActorRoleAsync(db, actorId, ct))) return [];
        var members = await db.TeamMembers.Select(m => new { m.TeamId, m.UserId }).ToListAsync(ct);
        return (await db.Teams.OrderBy(t => t.Name).ToListAsync(ct))
            .Select(t => new TeamRow(t.Id, t.Name, members.Where(m => m.TeamId == t.Id).Select(m => m.UserId).ToList())).ToList();
    }

    public Task<OrgDecision> CreateTeamAsync(Guid orgId, Guid actorId, string name, CancellationToken ct = default) =>
        InOrgLockAsync(orgId, actorId, ct, (db, actor) =>
        {
            var decision = OrgRules.ManageTeams(actor);
            if (decision != OrgDecision.Allow || string.IsNullOrWhiteSpace(name)) return Task.FromResult(decision);
            var team = new Team { Name = name.Trim() };
            db.Teams.Add(team);
            db.Audit(orgId, actorId, "team.create", "team", team.Id, after: new { team.Name });
            return Task.FromResult(decision);
        });

    public Task<OrgDecision> SetTeamMemberAsync(Guid orgId, Guid actorId, Guid teamId, Guid userId, bool member, CancellationToken ct = default) =>
        InOrgLockAsync(orgId, actorId, ct, async (db, actor) =>
        {
            var decision = OrgRules.ManageTeams(actor);
            if (decision != OrgDecision.Allow) return decision;
            var existing = await db.TeamMembers.SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct);
            if (member && existing is null) db.TeamMembers.Add(new TeamMember { TeamId = teamId, UserId = userId });
            else if (!member && existing is not null) db.TeamMembers.Remove(existing);
            else return decision;
            db.Audit(orgId, actorId, member ? "team.add_member" : "team.remove_member", "team", teamId, after: new { user = userId });
            return decision;
        });

    private async Task<AtharDbContext> OpenAsync(Guid orgId, CancellationToken ct)
    {
        var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = orgId;
        return db;
    }

    private async Task<OrgDecision> InOrgLockAsync(Guid orgId, Guid actorId, CancellationToken ct, Func<AtharDbContext, OrgRole?, Task<OrgDecision>> change)
    {
        await using var db = await OpenAsync(orgId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Organizations.FromSql($"SELECT * FROM organizations WHERE id = {orgId} FOR UPDATE").ToListAsync(ct);
        var decision = await change(db, await ActorRoleAsync(db, actorId, ct));
        if (decision != OrgDecision.Allow) return decision;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return decision;
    }

    // The caller's current role, or null when they aren't an active member.
    internal static Task<OrgRole?> ActorRoleAsync(AtharDbContext db, Guid userId, CancellationToken ct) =>
        (from m in db.OrgMembers
         join u in db.Users on m.UserId equals u.Id
         where m.UserId == userId && u.DeactivatedAt == null
         select (OrgRole?)m.Role).SingleOrDefaultAsync(ct);

    private static Task<int> ActiveOwnersAsync(AtharDbContext db, CancellationToken ct) =>
        db.OrgMembers.CountAsync(m => m.Role == OrgRole.Owner && db.Users.Any(u => u.Id == m.UserId && u.DeactivatedAt == null), ct);
}
