using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Domain.Wiki;
using Athar.Web.Auth;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Sharing;

/// <summary>Someone to share with: a user or a team.</summary>
public sealed record SharePrincipal(Guid? UserId, Guid? TeamId, string Name);

public sealed record PageShareRow(Guid Id, Guid? UserId, Guid? TeamId, string Name, string Level);

public sealed record ShareLinkRow(Guid Id, Guid? PageId, Guid? SpaceId, bool IncludeDescendants, bool HasPassword, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset CreatedAt);

/// <summary>A new link and its URL path. The token is shown once; only its hash is stored.</summary>
public sealed record CreatedLink(Guid Id, string Path);

/// <summary>
/// Sharing (FR-SH-01..04). Internal: a page shared with a user or team at view, comment or edit, by its editors and space
/// admins (the access rules honour it, T012). External: read-only links to a page (optionally with descendants) or a space,
/// with optional expiry and password; space admins and page editors manage them. Everything is audited; tokens never are.
/// </summary>
public sealed class ShareService(AtharDbContext db, NotificationService notifications, TimeProvider clock)
{
    private static readonly PasswordHasher<ShareLink> Hasher = new();

    // ---- internal shares ----

    public async Task<List<PageShareRow>> ListSharesAsync(Actor actor, Guid pageId, CancellationToken ct = default)
    {
        await RequireAsync(actor, pageId, PageAction.Edit, ct);
        var shares = await db.PageShares.AsNoTracking().Where(s => s.PageId == pageId).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(u => shares.Select(s => s.UserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var teams = await db.Teams.AsNoTracking().Where(t => shares.Select(s => s.TeamId).Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return [.. shares.Select(s => new PageShareRow(s.Id, s.UserId, s.TeamId,
            s.UserId is { } u ? users.GetValueOrDefault(u, "?") : teams.GetValueOrDefault(s.TeamId!.Value, "?"), Level(s.Level)))];
    }

    /// <summary>Shares the page with a user or a team (exactly one), or changes the level of an existing share.</summary>
    public async Task<Guid> ShareAsync(Actor actor, Guid pageId, Guid? userId, Guid? teamId, ShareLevel level, CancellationToken ct = default)
    {
        if ((userId is null) == (teamId is null)) throw new AtharError("invalid_input", "Share with exactly one user or team.");
        await RequireAsync(actor, pageId, PageAction.Edit, ct);
        if (userId is { } u && !await db.OrgMembers.AnyAsync(m => m.UserId == u, ct)) throw new AtharError("not_found", "User not found.");
        if (teamId is { } t && !await db.Teams.AnyAsync(x => x.Id == t, ct)) throw new AtharError("not_found", "Team not found.");

        var share = await db.PageShares.SingleOrDefaultAsync(s => s.PageId == pageId && s.UserId == userId && s.TeamId == teamId, ct);
        var before = share is null ? null : new { level = Level(share.Level) };
        if (share is null)
        {
            share = new PageShare { PageId = pageId, UserId = userId, TeamId = teamId, Level = level, CreatedBy = actor.UserId };
            db.PageShares.Add(share);
        }
        else share.Level = level;
        db.Audit(actor.OrgId, actor.UserId, "page.share", "page_share", share.Id, before, new { page_id = pageId, user_id = userId, team_id = teamId, level = Level(level) }, actor.CredentialId);
        await db.SaveChangesAsync(ct);

        // FR-NT-01 "shares with you": once the share is stored, so the visibility check sees it. A workspace prerequisite
        // still applies (AT-49), so someone outside the workspace isn't told about a page they can't open.
        var recipients = userId is { } one ? [one] : await db.TeamMembers.Where(m => m.TeamId == teamId).Select(m => m.UserId).ToListAsync(ct);
        await notifications.NotifyAsync(actor.OrgId, recipients, "share", $"page:{pageId}", pageId, actor.UserId, ct);
        await db.SaveChangesAsync(ct);
        return share.Id;
    }

    /// <summary>Org users and teams whose name matches, for the share picker.</summary>
    public async Task<List<SharePrincipal>> PrincipalsAsync(Actor actor, string query, int limit = 10, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var like = "%" + query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var users = await db.Users.Where(u => u.DeactivatedAt == null && EF.Functions.ILike(u.DisplayName, like, @"\"))
            .OrderBy(u => u.DisplayName).Take(limit).Select(u => new SharePrincipal(u.Id, null, u.DisplayName)).ToListAsync(ct);
        var teams = await db.Teams.Where(t => EF.Functions.ILike(t.Name, like, @"\"))
            .OrderBy(t => t.Name).Take(limit).Select(t => new SharePrincipal(null, t.Id, t.Name)).ToListAsync(ct);
        return [.. users.Concat(teams).Take(limit)];
    }

    public async Task UnshareAsync(Actor actor, Guid shareId, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var share = await db.PageShares.SingleOrDefaultAsync(s => s.Id == shareId, ct) ?? throw new AtharError("not_found", "Share not found.");
        await RequireAsync(actor, share.PageId, PageAction.Edit, ct);
        db.PageShares.Remove(share);
        db.Audit(actor.OrgId, actor.UserId, "page.unshare", "page_share", share.Id, new { page_id = share.PageId, user_id = share.UserId, team_id = share.TeamId, level = Level(share.Level) }, null, actor.CredentialId);
        await db.SaveChangesAsync(ct);
    }

    // ---- external links ----

    /// <summary>A page link (page editors and space admins) or, with <paramref name="spaceId"/>, a space link (space admins).</summary>
    public async Task<CreatedLink> CreateLinkAsync(Actor actor, Guid? pageId, Guid? spaceId, bool includeDescendants, DateTimeOffset? expiresAt, string? password, CancellationToken ct = default)
    {
        if ((pageId is null) == (spaceId is null)) throw new AtharError("invalid_input", "A link is for a page or a space.");
        if (expiresAt is { } e && e <= clock.GetUtcNow()) throw new AtharError("invalid_input", "The expiry is in the past.");
        if (pageId is { } p) await RequireAsync(actor, p, PageAction.Edit, ct);
        else await RequireSpaceAdminAsync(actor, spaceId!.Value, ct);

        var token = Tokens.New();
        var link = new ShareLink
        {
            PageId = pageId, SpaceId = spaceId, IncludeDescendants = pageId is not null && includeDescendants, TokenHash = Tokens.Hash(token),
            ExpiresAt = expiresAt, CreatedBy = actor.UserId,
        };
        if (!string.IsNullOrEmpty(password)) link.PasswordHash = Hasher.HashPassword(link, password);
        db.ShareLinks.Add(link);
        db.Audit(actor.OrgId, actor.UserId, "share_link.create", "share_link", link.Id,
            after: new { page_id = pageId, space_id = spaceId, include_descendants = link.IncludeDescendants, expires_at = expiresAt, password = link.PasswordHash is not null }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return new(link.Id, $"/s/{token}");
    }

    /// <summary>Links on a page (its editors), or every link in a space (space admins, FR-SH-04).</summary>
    public async Task<List<ShareLinkRow>> ListLinksAsync(Actor actor, Guid? pageId, Guid? spaceId, CancellationToken ct = default)
    {
        IQueryable<ShareLink> links;
        if (pageId is { } p)
        {
            await RequireAsync(actor, p, PageAction.Edit, ct);
            links = db.ShareLinks.Where(l => l.PageId == p);
        }
        else
        {
            await RequireSpaceAdminAsync(actor, spaceId!.Value, ct);
            links = db.ShareLinks.Where(l => l.SpaceId == spaceId || db.Pages.Any(pg => pg.Id == l.PageId && pg.SpaceId == spaceId));
        }
        return await links.AsNoTracking().OrderByDescending(l => l.CreatedAt)
            .Select(l => new ShareLinkRow(l.Id, l.PageId, l.SpaceId, l.IncludeDescendants, l.PasswordHash != null, l.ExpiresAt, l.RevokedAt, l.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>Space admins revoke any link in their space; page editors the links on their page. A revoked link answers 410.</summary>
    public async Task RevokeLinkAsync(Actor actor, Guid linkId, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var link = await db.ShareLinks.SingleOrDefaultAsync(l => l.Id == linkId, ct) ?? throw new AtharError("not_found", "Link not found.");
        if (link.PageId is { } p) await RequireAsync(actor, p, PageAction.Edit, ct);
        else await RequireSpaceAdminAsync(actor, link.SpaceId!.Value, ct);
        if (link.RevokedAt is not null) return;
        link.RevokedAt = clock.GetUtcNow();
        db.Audit(actor.OrgId, actor.UserId, "share_link.revoke", "share_link", link.Id, new { revoked = false }, new { revoked = true }, actor.CredentialId);
        await db.SaveChangesAsync(ct);
    }

    // ---- what a link shows ----

    /// <summary>The link for a token, whatever its state; null if there's no such token.</summary>
    public Task<ShareLink?> FindAsync(string token, CancellationToken ct = default)
    {
        var hash = Tokens.Hash(token);
        return db.ShareLinks.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(l => l.TokenHash == hash, ct);
    }

    public bool IsLive(ShareLink link) => link.RevokedAt is null && (link.ExpiresAt is null || link.ExpiresAt > clock.GetUtcNow());

    public static bool PasswordMatches(ShareLink link, string password) =>
        link.PasswordHash is not null && Hasher.VerifyHashedPassword(link, link.PasswordHash, password) != PasswordVerificationResult.Failed;

    /// <summary>
    /// The live pages the link covers, parents before children: the page (and its descendants when asked) or the whole space.
    /// Restricted pages and everything under them are always left out (FR-SH-02); a page moved out simply isn't found.
    /// </summary>
    public async Task<List<Page>> PagesAsync(ShareLink link, CancellationToken ct = default)
    {
        db.CurrentOrgId = link.OrgId;
        var spaceId = link.SpaceId ?? await db.Pages.Where(p => p.Id == link.PageId).Select(p => (Guid?)p.SpaceId).SingleOrDefaultAsync(ct);
        if (spaceId is null) return [];
        var pages = await db.Pages.AsNoTracking().Where(p => p.SpaceId == spaceId && p.DeletedAt == null).OrderBy(p => p.Position).ToListAsync(ct);
        var restricted = (await db.PageRestrictions.Where(r => pages.Select(p => p.Id).Contains(r.PageId)).Select(r => r.PageId).ToListAsync(ct)).ToHashSet();
        restricted.UnionWith(pages.Where(p => p.Status == PageStatus.Draft).Select(p => p.Id)); // U-1 (T119): drafts and what's under them too
        bool Hidden(Page p) => restricted.Contains(p.Id) || p.AncestorIds.Any(restricted.Contains);

        IEnumerable<Guid> ids = link.PageId is { } root
            ? link.IncludeDescendants ? PageTree.Subtree(root, pages.Select(p => (p.Id, p.ParentId))) : pages.Any(p => p.Id == root) ? [root] : []
            : pages.Where(p => p.ParentId is null).SelectMany(r => PageTree.Subtree(r.Id, pages.Select(p => (p.Id, p.ParentId))));
        var byId = pages.ToDictionary(p => p.Id);
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id]).Where(p => !Hidden(p))];
    }

    /// <summary>The attachments of the shared pages (FR-PG-06: attachments only through shared pages).</summary>
    public Task<List<Attachment>> AttachmentsAsync(ShareLink link, IEnumerable<Page> pages, CancellationToken ct = default)
    {
        db.CurrentOrgId = link.OrgId;
        var ids = pages.Select(p => p.Id).ToList();
        return db.Attachments.AsNoTracking().Where(a => ids.Contains(a.PageId)).ToListAsync(ct);
    }

    /// <summary>FR-SH: a view through the link, audited without the token.</summary>
    public async Task AuditViewAsync(ShareLink link, Guid pageId, CancellationToken ct = default)
    {
        db.Audit(link.OrgId, null, "share_link.view", "share_link", link.Id, after: new { page_id = pageId });
        await db.SaveChangesAsync(ct);
    }

    private async Task RequireAsync(Actor actor, Guid pageId, PageAction action, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var caller = await db.ActorAsync(actor.UserId, actor.CredentialId, ct) ?? throw new AtharError("unauthorized", "The user is inactive or the credential is revoked.");
        if (await db.CanAsync(caller, pageId, action, ct)) return;
        throw await db.CanAsync(caller, pageId, PageAction.Read, ct)
            ? new AtharError("forbidden", "Only the page's editors and space admins can share it.")
            : new AtharError("not_found", "Page not found.");
    }

    private async Task RequireSpaceAdminAsync(Actor actor, Guid spaceId, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        if (await db.SpaceRoleAsync(actor.UserId, spaceId, ct) != SpaceRole.Admin) throw new AtharError("forbidden", "Only space admins manage space links.");
    }

    private static string Level(ShareLevel l) => l.ToString().ToLowerInvariant();
}
