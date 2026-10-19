using System.Globalization;
using System.Resources;
using Athar.Data;
using Athar.Domain.Model;
using Athar.Web.Auth;

using Athar.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Components.Wiki;

/// <summary>Marker type for the wiki strings (WikiText.resx, WikiText.ar.resx).</summary>
public sealed class WikiText;

/// <summary>The signed-in user as a wiki actor, plus the wiki strings in the request's UI language.</summary>
public sealed class WikiSession(AuthenticationStateProvider auth, IServiceScopeFactory scopes)
{
    private Task<WikiActor>? _resolving;

    private static readonly ResourceManager Strings = new("Athar.Web.Components.Wiki.WikiText", typeof(WikiText).Assembly);

    public WikiActor? Actor { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>A UI string (keys in WikiText.resx). The language follows the user's locale (request localization, T010).</summary>
    public string T(string key) => Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>The signed-in person as a service actor (no credential, every scope, never an external agent).</summary>
    public Mcp.Actor AsActor() => new(Actor!.UserId, Actor.OrgId, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);

    /// <summary>
    /// One lookup per circuit, shared by every caller. Its own DbContext: the page and the shell (notification bell, nav)
    /// resolve at the same time, and a circuit's DbContext takes one query at a time (T085: /memories crashed its circuit).
    /// </summary>
    public Task<WikiActor> ResolveAsync() => _resolving ??= LoadAsync();

    private async Task<WikiActor> LoadAsync()
    {
        var user = (await auth.GetAuthenticationStateAsync()).User;
        var actor = new WikiActor(user.UserId(), user.OrgId());
        await using (var scope = scopes.CreateAsyncScope())
            Name = await scope.ServiceProvider.GetRequiredService<AtharDbContext>().Users.IgnoreQueryFilters()
                .Where(u => u.OrgId == actor.OrgId && u.Id == actor.UserId).Select(u => u.DisplayName).SingleAsync();
        return Actor = actor;
    }
}
