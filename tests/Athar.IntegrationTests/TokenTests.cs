using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T030: full PATs, owner-only trusted-local, scope and AI-policy denial (FR-ID-06, FR-AG-07, AT-43, AT-44).</summary>
[Collection(AtharCollection.Name)]
public class TokenTests(AtharApp athar)
{
    private static readonly Guid Org = QaSeed.Org;

    private async Task<Browser> SignedInAsync(string name, CancellationToken ct)
    {
        var browser = new Browser(athar.App.GetEndpoint("web", "https"));
        await browser.SubmitAsync("/account/login", new() { ["Input.Email"] = QaSeed.Email(name), ["Input.Password"] = QaSeed.Password }, ct);
        return browser;
    }

    private HttpClient Bearer(string token)
    {
        var http = athar.Web();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    [Fact]
    public async Task AT_43_only_an_owner_makes_a_token_trusted_local_with_a_reason_and_it_is_audited()
    {
        var ct = AtharApp.Timeout();

        // /api/v1 is bearer only: a signed-in person's cookie gets 401 there.
        using (var member = await SignedInAsync("Qa Member", ct))
            Assert.Equal(HttpStatusCode.Unauthorized, (await member.Http.PostAsJsonAsync("/api/v1/tokens", new { name = "x", scopes = new[] { "read" } }, ct)).StatusCode);

        // A PAT creates a narrower token: always external-agent, whatever the request says, and the plaintext comes back once.
        using var memberPat = Bearer(QaSeed.MemberPat);
        var created = await memberPat.PostAsJsonAsync("/api/v1/tokens",
            new { name = "laptop", scopes = new[] { "read" }, expires_in_days = (int?)null, disclosure_class = "trusted-local" }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        var (id, token) = (body.GetProperty("id").GetGuid(), body.GetProperty("token").GetString()!);
        Assert.Equal("external-agent", body.GetProperty("disclosure_class").GetString());
        Assert.StartsWith("athar_pat_", token);
        await using (var db = athar.Db(Org))
        {
            var row = await db.AccessTokens.SingleAsync(t => t.Id == id, ct);
            Assert.Equal(DisclosureClass.ExternalAgent, row.DisclosureClass);
            Assert.Equal(["read"], row.Scopes);
            Assert.InRange(row.ExpiresAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromDays(89), TimeSpan.FromDays(90));
            Assert.NotEqual(token, row.TokenHash);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await memberPat.PostAsJsonAsync("/api/v1/tokens", new { name = "x", scopes = new[] { "admin" } }, ct)).StatusCode);
        var mine = await memberPat.GetFromJsonAsync<JsonElement>("/api/v1/tokens", ct);
        Assert.Contains(mine.EnumerateArray(), t => t.GetProperty("id").GetGuid() == id && !t.TryGetProperty("token_hash", out _));
        // No escalation: a read token can't mint a write token.
        using (var readOnly = Bearer(token))
            Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/api/v1/tokens", new { name = "wider", scopes = new[] { "write" } }, ct)).StatusCode);

        // Reclassifying: external-agent credentials get 403 (the member's, and an admin's).
        object Classify(string? reason) => new { disclosure_class = "trusted-local", reason };
        Assert.Equal(HttpStatusCode.Forbidden, (await memberPat.PostAsJsonAsync($"/api/v1/admin/tokens/{id}/classify", Classify("mine"), ct)).StatusCode);
        using (var adminPat = Bearer(QaSeed.AdminPat))
            Assert.Equal(HttpStatusCode.Forbidden, (await adminPat.PostAsJsonAsync($"/api/v1/admin/tokens/{id}/classify", Classify("ops box"), ct)).StatusCode);

        // An owner, with a trusted-local token set up from the admin page (the service), and a reason.
        var tokens = new TokenService(new Factory(athar));
        var (_, ownerToken) = await tokens.CreateAsync(Org, QaSeed.Owner, "owner automation", ["read"], 1, ct);
        Assert.Equal(OrgDecision.Forbidden, await tokens.ClassifyAsync(Org, QaSeed.Admin, ownerToken!.Id, DisclosureClass.TrustedLocal, "admins can't", ct));
        Assert.Equal(OrgDecision.Allow, await tokens.ClassifyAsync(Org, QaSeed.Owner, ownerToken.Id, DisclosureClass.TrustedLocal, "Runs on the ops host with a local model", ct));
        using var owner = Bearer(ownerToken.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/v1/admin/tokens/{id}/classify", Classify(" "), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostAsJsonAsync($"/api/v1/admin/tokens/{id}/classify", Classify("Runs on the build server with a local Ollama model"), ct)).StatusCode);

        await using (var db = athar.Db(Org))
        {
            var row = await db.AccessTokens.SingleAsync(t => t.Id == id, ct);
            Assert.Equal((DisclosureClass.TrustedLocal, QaSeed.Owner), (row.DisclosureClass, row.ClassifiedBy));
            var audit = await db.AuditEvents.SingleAsync(e => e.Action == "token.classify" && e.ResourceId == id.ToString(), ct);
            Assert.Equal(QaSeed.Owner, audit.ActorUserId);
            Assert.Equal(ownerToken.Id, audit.CredentialId); // stamped from the request
            Assert.Contains("build server", audit.After);
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "token.create" && e.ResourceId == id.ToString(), ct));
        }
        Assert.Equal(new Caller(QaSeed.Member, External: false, Agent: true), await athar.Db(Org).ActorAsync(QaSeed.Member, id, ct));

        // Revoking: the next request with it is refused.
        Assert.Equal(HttpStatusCode.NoContent, (await memberPat.DeleteAsync($"/api/v1/tokens/{id}", ct)).StatusCode);
        using var revoked = Bearer(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" }, ct)).StatusCode);
    }
    [Fact]
    public async Task AT_44_scopes_and_the_space_ai_policy_narrow_what_agents_get()
    {
        var ct = AtharApp.Timeout();

        // A read token can't remember.
        using var http = athar.Web();
        await using (var mcp = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.MemberReadPat, ct))
            Assert.Contains("memory:write", await Mcp.CallError(mcp, "remember", new()
            {
                ["title"] = "nope", ["body_md"] = "nope", ["type"] = "fact", ["scope"] = "personal", ["op_id"] = Guid.NewGuid().ToString(),
            }, ct));

        // A read token watches a live document; a write token edits it (Member is an editor in the wiki, which allows AI write).
        await using var db = athar.Db(Org);
        Assert.Equal("view", await Revocation.CollabLevelAsync(db, QaSeed.Member, QaSeed.MemberReadPatId, QaSeed.HomePage, "edit", ct));
        Assert.Equal("edit", await Revocation.CollabLevelAsync(db, QaSeed.Member, QaSeed.MemberPatId, QaSeed.HomePage, "edit", ct));

        // AI read off (the handbook): the person reads it, their agent gets nothing.
        var person = new Caller(QaSeed.Admin);
        var agent = new Caller(QaSeed.Admin, External: true, Agent: true);
        Assert.True(await db.CanAsync(person, QaSeed.HandbookPage, PageAction.Read, ct));
        Assert.False(await db.CanAsync(agent, QaSeed.HandbookPage, PageAction.Read, ct));
        Assert.False(await db.PagesFor(agent).AnyAsync(p => p.SpaceId == QaSeed.OpsHandbook, ct));
        Assert.False(await db.SpacesFor(agent).AnyAsync(s => s.Id == QaSeed.OpsHandbook, ct));

        // AI write off (the access lab is read only for agents): an editor's agent reads but doesn't edit.
        var labAgent = new Caller(QaSeed.LabEditor, Agent: true);
        Assert.True(await db.CanAsync(labAgent, QaSeed.LabRoot, PageAction.Read, ct));
        Assert.False(await db.CanAsync(labAgent, QaSeed.LabRoot, PageAction.Edit, ct));
        Assert.True(await db.CanAsync(new Caller(QaSeed.LabEditor), QaSeed.LabRoot, PageAction.Edit, ct));
    }
}
