using System.Security.Claims;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;

namespace Athar.Web.Services;

/// <summary>
/// The handbook (FR-AG-10, T144): the user and admin guides from docs/handbook for /help, and the admin action that adds the
/// "Getting started with agents" page (English and Arabic) to a space. That is how the production server gets it (OD-5:
/// no seeding there).
/// </summary>
public sealed class HelpService(PageService pages, PageOpsService ops, OrgAdminService org)
{
    /// <summary>A guide's Markdown: <c>user-guide</c> or <c>admin-guide</c>, in Arabic when <paramref name="lang"/> is <c>ar</c>.</summary>
    public static string Guide(string name, string? lang = null)
    {
        var asm = typeof(HelpService).Assembly;
        using var stream = (lang == "ar" ? asm.GetManifestResourceStream($"handbook/{name}.ar.md") : null)
            ?? asm.GetManifestResourceStream($"handbook/{name}.md")
            ?? throw new ArgumentException($"No handbook guide '{name}'.", nameof(name));
        return new StreamReader(stream).ReadToEnd();
    }

    public async Task<bool> IsAdminAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        OrgRules.IsAdmin(await org.RoleOfAsync(user.OrgId(), user.UserId(), ct));

    /// <summary>The spaces an admin can add the page to: those where they may create pages (editor or admin).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Name)>> TargetSpacesAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(user, ct)) return [];
        return [.. (await pages.SpacesAsync(new WikiActor(user.UserId(), user.OrgId()), ct))
            .Where(s => s.Role is SpaceRole.Editor or SpaceRole.Admin)
            .Select(s => (s.Space.Id, $"{s.Workspace} › {s.Space.Name}"))];
    }

    /// <summary>
    /// Creates both pages at the root of <paramref name="spaceId"/> through the page operations (audited, indexed, the full
    /// Markdown converter). The op ids are fixed per space and language, so a second click returns the same pages.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AddGettingStartedAsync(ClaimsPrincipal user, Guid spaceId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(user, ct)) throw new AtharError("forbidden", "Only organization admins add the getting-started page.");
        var actor = Actor.ForUser(user);
        var ids = new List<Guid>();
        foreach (var lang in GettingStarted.Languages)
            ids.Add((await ops.CreatePageAsync(actor, spaceId, null, GettingStarted.Title(lang), GettingStarted.Body(lang), $"getting-started:{spaceId}:{lang}", ct)).PageId);
        return ids;
    }
}
