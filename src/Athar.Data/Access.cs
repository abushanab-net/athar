using Athar.Domain.Access;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.Data;

/// <summary>
/// <see cref="Athar.Domain.Access"/> on this context (FR-AC-02). Filters compose into LINQ for lists, trees and search
/// (materialize the visible ids for raw vector/FTS SQL). Decisions load the facts about one page fresh on every call and
/// evaluate the same predicate in memory: no decision cache, and anything missing fails closed.
/// Set <see cref="AtharDbContext.CurrentOrgId"/> to the caller's org first.
/// </summary>
public sealed record PageRights(bool Read, bool Comment, bool Edit, bool Placeholder);

public static class AccessExtensions
{
    public static AccessSource AccessSource(this AtharDbContext db) => new(
        db.Users, db.OrgMembers, db.TeamMembers, db.WorkspaceMembers, db.Spaces, db.SpaceMembers, db.Pages, db.PageRestrictions, db.PageShares);

    /// <summary>Pages the caller may read, comment on or edit.</summary>
    public static IQueryable<Page> PagesFor(this AtharDbContext db, Caller caller, PageAction action = PageAction.Read) =>
        db.Pages.Where(PageAccess.Can(db.AccessSource(), caller, action));

    /// <summary>Restricted pages the caller manages but can't read: show them as placeholders (AT-06).</summary>
    public static IQueryable<Page> PlaceholdersFor(this AtharDbContext db, Caller caller) =>
        db.Pages.Where(PageAccess.SeesPlaceholder(db.AccessSource(), caller));

    public static IQueryable<Space> SpacesFor(this AtharDbContext db, Caller caller) =>
        db.Spaces.Where(SpaceAccess.CanSee(db.AccessSource(), caller));

    /// <summary>The decision method for one page.</summary>
    public static async Task<bool> CanAsync(this AtharDbContext db, Caller caller, Guid pageId, PageAction action, CancellationToken ct = default) =>
        await FactsAsync(db, caller, pageId, ct) is { } f && PageAccess.Decide(f.Source, caller, action, f.Page);

    public static async Task<bool> SeesPlaceholderAsync(this AtharDbContext db, Caller caller, Guid pageId, CancellationToken ct = default) =>
        await FactsAsync(db, caller, pageId, ct) is { } f && PageAccess.DecidePlaceholder(f.Source, caller, f.Page);

    /// <summary>Everything a page view needs to know, from one load of the facts. A missing page has no rights.</summary>
    public static async Task<PageRights> RightsAsync(this AtharDbContext db, Caller caller, Guid pageId, CancellationToken ct = default) =>
        await FactsAsync(db, caller, pageId, ct) is { } f
            ? new(PageAccess.Decide(f.Source, caller, PageAction.Read, f.Page), PageAccess.Decide(f.Source, caller, PageAction.Comment, f.Page),
                PageAccess.Decide(f.Source, caller, PageAction.Edit, f.Page), PageAccess.DecidePlaceholder(f.Source, caller, f.Page))
            : new(false, false, false, false);

    /// <summary>The caller's effective space role (highest of direct and team grants), or null.</summary>
    public static async Task<SpaceRole?> SpaceRoleAsync(this AtharDbContext db, Guid userId, Guid spaceId, CancellationToken ct = default)
    {
        var roles = await SpaceAccess.Roles(db.AccessSource(), userId, spaceId).ToListAsync(ct);
        return roles.Count == 0 ? null : roles.Max();
    }

    /// <summary>
    /// FR-ID-08: is this actor still allowed to act at all? An active org member, and when a PAT is given, one that's
    /// neither revoked nor expired: then the caller is an agent and its disclosure class decides <see cref="Caller.External"/>. Jobs and agent operations
    /// call this before running and stop on null (AT-08). OAuth credentials join with T031.
    /// </summary>
    public static async Task<Caller?> ActorAsync(this AtharDbContext db, Guid userId, Guid? credentialId, CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.DeactivatedAt == null && db.OrgMembers.Any(m => m.UserId == userId), ct)) return null;
        if (credentialId is null) return new Caller(userId);
        var now = DateTimeOffset.UtcNow;
        var disclosure = await db.AccessTokens
            .Where(t => t.Id == credentialId && t.UserId == userId && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now))
            .Select(t => (DisclosureClass?)t.DisclosureClass).SingleOrDefaultAsync(ct);
        if (disclosure is not null) return new Caller(userId, disclosure == DisclosureClass.ExternalAgent, Agent: true);
        // An OAuth grant (the credential of OAuth access tokens): always external-agent (FR-ID-06).
        var subject = userId.ToString();
        return await db.OauthAuthorizations.AnyAsync(a => a.Id == credentialId && a.OrgId == db.CurrentOrgId && a.Subject == subject && a.Status == "valid", ct)
            ? new Caller(userId, External: true, Agent: true)
            : null;
    }

    // Everything the predicates can touch for this caller and page, in two queries: the page with its space, workspace
    // membership, grants, restrictions on it and its ancestors and its shares; then the caller with their org membership and teams.
    private static async Task<(AccessSource Source, Page Page)?> FactsAsync(AtharDbContext db, Caller caller, Guid pageId, CancellationToken ct)
    {
        var u = caller.UserId;
        var page = await db.Pages.AsNoTracking().Where(p => p.Id == pageId).Select(p => new
        {
            Page = p,
            Spaces = db.Spaces.Where(s => s.Id == p.SpaceId).ToList(),
            WorkspaceMembers = db.WorkspaceMembers.Where(w => w.UserId == u && db.Spaces.Any(s => s.Id == p.SpaceId && s.WorkspaceId == w.WorkspaceId)).ToList(),
            SpaceMembers = db.SpaceMembers.Where(m => m.SpaceId == p.SpaceId).ToList(),
            Restrictions = db.PageRestrictions.Where(r => r.PageId == p.Id || p.AncestorIds.Contains(r.PageId)).ToList(),
            Shares = db.PageShares.Where(s => s.PageId == p.Id).ToList(),
        }).AsSingleQuery().SingleOrDefaultAsync(ct);
        if (page is null) return null;
        var who = await db.Users.AsNoTracking().Where(x => x.Id == u).Select(x => new
        {
            User = x,
            OrgMembers = db.OrgMembers.Where(m => m.UserId == u).ToList(),
            Teams = db.TeamMembers.Where(t => t.UserId == u).ToList(),
        }).AsSingleQuery().SingleOrDefaultAsync(ct);

        var source = new AccessSource(
            (who is null ? [] : new[] { who.User }).AsQueryable(), (who?.OrgMembers ?? []).AsQueryable(), (who?.Teams ?? []).AsQueryable(),
            page.WorkspaceMembers.AsQueryable(), page.Spaces.AsQueryable(), page.SpaceMembers.AsQueryable(),
            new[] { page.Page }.AsQueryable(), page.Restrictions.AsQueryable(), page.Shares.AsQueryable());
        return (source, page.Page);
    }}
