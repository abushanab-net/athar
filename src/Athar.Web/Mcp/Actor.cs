using System.Security.Claims;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Auth;

namespace Athar.Web.Mcp;

/// <summary>
/// The authenticated caller of one request: user, org, credential, token scopes and disclosure class (docs/06 §6.1).
/// A signed-in person (cookie) has no credential, every scope, and is never an external agent.
/// </summary>
public sealed record Actor(Guid UserId, Guid OrgId, Guid? CredentialId, IReadOnlySet<string> Scopes, DisclosureClass Disclosure, string? ClaimedClient)
{
    /// <summary>The page-access caller: an agent whenever a credential is used (space AI policy), external per its disclosure class.</summary>
    public Caller Caller => new(UserId, Disclosure == DisclosureClass.ExternalAgent, Agent: CredentialId is not null);

    private static readonly HashSet<string> AllScopes = ["read", "write", "memory:write", "pages:delete"];

    /// <summary>The person behind a Blazor circuit (Identity cookie, T010).</summary>
    public static Actor ForUser(ClaimsPrincipal user) =>
        new(Auth.AtharClaims.UserId(user), Auth.AtharClaims.OrgId(user), null, AllScopes, DisclosureClass.TrustedLocal, null);

    public static Actor From(HttpContext http)
    {
        var u = http.User;
        Guid G(string type) => Guid.Parse(u.FindFirstValue(type)!);
        return new(G(PatAuth.UserClaim), G(PatAuth.OrgClaim), G(PatAuth.CredentialClaim),
            // PAT scopes are "scope" claims; OAuth access tokens carry OpenIddict's private scope claims (T031).
            u.FindAll(c => c.Type is PatAuth.ScopeClaim or OpenIddict.Abstractions.OpenIddictConstants.Claims.Private.Scope).Select(c => c.Value).ToHashSet(),
            Enum.Parse<DisclosureClass>(u.FindFirstValue(PatAuth.DisclosureClaim)!),
            // Untrusted attribution label only (FR-AG-03).
            http.Request.Headers["X-Athar-Client"].FirstOrDefault());
    }
}
