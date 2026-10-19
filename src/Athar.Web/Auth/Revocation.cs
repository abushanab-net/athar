using System.Security.Claims;
using Athar.Data;
using Athar.Domain.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Athar.Web.Auth;

/// <summary>
/// Revocation (FR-ID-08, AT-08). REST and MCP check credentials on every request (PatAuth), cookies check the security
/// stamp on every request (AuthSetup), and access predicates run fresh on every action. This adds the two long-lived
/// connections: collab leases and Blazor circuits.
/// </summary>
public static class Revocation
{
    private static readonly string[] Levels = ["view", "comment", "edit"];

    /// <summary>
    /// The collab level to grant now: the lower of the requested level and the actor's current rights, or null to close
    /// the connection. Collab renews every 30 s, so a revocation or an edit→view downgrade reaches it within 30 s.
    /// </summary>
    public static async Task<string?> CollabLevelAsync(AtharDbContext db, Guid userId, Guid? credentialId, Guid pageId, string requested, CancellationToken ct = default)
    {
        if (await db.ActorAsync(userId, credentialId, ct) is not { } caller) return null;
        var rights = await db.RightsAsync(caller, pageId, ct);
        var allowed = rights.Edit ? 2 : rights.Comment ? 1 : rights.Read ? 0 : -1;
        // An agent changes a live document only with the write scope (FR-ID-06, FR-AG-07); otherwise it watches.
        if (credentialId is not null && allowed > 0 && !await db.AccessTokens.AnyAsync(t => t.Id == credentialId && t.Scopes.Contains("write"), ct)) allowed = 0;
        var level = Math.Min(allowed, Math.Max(0, Array.IndexOf(Levels, requested)));
        return level < 0 ? null : Levels[level];
    }
}

/// <summary>
/// An open Blazor circuit re-checks the cookie's security stamp every 30 s. A password reset or a deactivation rotates the
/// stamp, so the circuit drops to signed-out and the router sends the user to sign-in.
/// </summary>
public sealed class RevalidatingAuthState(ILoggerFactory logs, IServiceScopeFactory scopes, IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(logs)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AtharIdentityUser>>();
        var user = await users.GetUserAsync(state.User);
        return user is not null && state.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType) == await users.GetSecurityStampAsync(user);
    }
}
