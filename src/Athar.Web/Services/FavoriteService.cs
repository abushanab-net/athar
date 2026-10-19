using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

/// <summary>A side-menu favorite: a workspace or a space, with its badge.</summary>
public sealed record FavoriteItem(string Kind, Guid Id, string Name, string Href, string? Color, string? Icon);

/// <summary>
/// Workspaces and spaces a person pins to the side menu. Personal: only the person sees them, and only while they can
/// still see the target (a space through the access rules, a workspace as a member or org admin). Scoped per circuit;
/// <see cref="Changed"/> tells the side menu to reload after a toggle anywhere on the page.
/// </summary>
public sealed class FavoriteService(IDbContextFactory<AtharDbContext> dbs)
{
    public event Action? Changed;

    /// <summary>Something the side menu shows changed elsewhere (a badge): reload it.</summary>
    public void Refresh() => Changed?.Invoke();

    public async Task<List<FavoriteItem>> ListAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var favs = await db.Favorites.Where(f => f.UserId == userId).OrderBy(f => f.CreatedAt).ToListAsync(ct);
        if (favs.Count == 0) return [];
        var spaceIds = favs.Where(f => f.SpaceId != null).Select(f => f.SpaceId!.Value).ToList();
        var spaces = await db.SpacesFor(new Caller(userId)).Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var workspaces = await VisibleWorkspacesAsync(db, userId, favs.Where(f => f.WorkspaceId != null).Select(f => f.WorkspaceId!.Value).ToList(), ct);
        return [.. favs.Select(f => f.SpaceId is { } s
                ? spaces.TryGetValue(s, out var space) ? new FavoriteItem("space", s, space.Name, $"/wiki/s/{s}", space.Color, space.Icon) : null
                : workspaces.TryGetValue(f.WorkspaceId!.Value, out var w) ? new FavoriteItem("workspace", w.Id, w.Name, $"/wiki#ws-{w.Id}", w.Color, w.Icon) : null)
            .OfType<FavoriteItem>()];
    }

    public async Task<bool> IsFavoriteAsync(Guid orgId, Guid userId, string kind, Guid id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        return await Of(db, userId, kind, id).AnyAsync(ct);
    }

    /// <summary>Adds or removes a favorite; returns whether it is one now. Not found when the person can't see the target.</summary>
    public async Task<bool> ToggleAsync(Guid orgId, Guid userId, string kind, Guid id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var visible = kind switch
        {
            "space" => await db.SpacesFor(new Caller(userId)).AnyAsync(s => s.Id == id, ct),
            "workspace" => (await VisibleWorkspacesAsync(db, userId, [id], ct)).Count == 1,
            _ => false,
        };
        if (!visible) throw new AtharError("not_found", "Not found.");
        var existing = await Of(db, userId, kind, id).SingleOrDefaultAsync(ct);
        if (existing is null)
            db.Favorites.Add(new Favorite { OrgId = orgId, UserId = userId, SpaceId = kind == "space" ? id : null, WorkspaceId = kind == "workspace" ? id : null });
        else
            db.Favorites.Remove(existing);
        db.Audit(orgId, userId, existing is null ? "favorite.add" : "favorite.remove", kind, id);
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return existing is null;
    }

    private static IQueryable<Favorite> Of(AtharDbContext db, Guid userId, string kind, Guid id) =>
        db.Favorites.Where(f => f.UserId == userId && (kind == "space" ? f.SpaceId == id : f.WorkspaceId == id));

    /// <summary>Workspaces among <paramref name="ids"/> the person belongs to; org admins see them all (as on the Workspaces page).</summary>
    private static async Task<Dictionary<Guid, Workspace>> VisibleWorkspacesAsync(AtharDbContext db, Guid userId, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var admin = OrgRules.IsAdmin(await db.OrgMembers.Where(m => m.UserId == userId).Select(m => (OrgRole?)m.Role).SingleOrDefaultAsync(ct));
        var member = db.WorkspaceMembers.Where(m => m.UserId == userId).Select(m => m.WorkspaceId);
        return await db.Workspaces.Where(w => ids.Contains(w.Id) && (admin || member.Contains(w.Id))).ToDictionaryAsync(w => w.Id, ct);
    }

    private async Task<AtharDbContext> OpenAsync(Guid orgId, CancellationToken ct)
    {
        var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = orgId;
        return db;
    }
}
