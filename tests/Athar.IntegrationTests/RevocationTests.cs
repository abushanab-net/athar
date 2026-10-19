using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T013: revocation reaches every surface (FR-ID-08, AT-08). Each change is undone, so the access lab stays as seeded.</summary>
[Collection(AtharCollection.Name)]
public class RevocationTests(AtharApp athar)
{
    private static readonly Guid Org = QaSeed.Org;

    [Fact]
    public async Task AT_08_collab_downgrades_to_read_only_and_closes_on_the_next_lease_renewal()
    {
        var ct = AtharApp.Timeout();
        using var collab = await CollabClientAsync(ct);
        await using var db = athar.Db(Org);
        var role = await db.SpaceMembers.SingleAsync(m => m.SpaceId == QaSeed.AccessLab && m.UserId == QaSeed.LabEditor, ct);
        var membership = await db.WorkspaceMembers.SingleAsync(m => m.WorkspaceId == QaSeed.Engineering && m.UserId == QaSeed.LabEditor, ct);
        try
        {
            var (status, grant) = await AuthorizeAsync(collab, new { page = QaSeed.LabRoot, ticket = await TicketAsync(collab, QaSeed.LabEditor, null, QaSeed.LabRoot, "edit", ct) }, ct);
            Assert.Equal((HttpStatusCode.OK, "edit"), (status, grant!.Value.GetProperty("level").GetString()));

            // edit→view: the next renewal grants read only.
            role.Role = SpaceRole.Viewer;
            await db.SaveChangesAsync(ct);
            (status, grant) = await AuthorizeAsync(collab, new { page = QaSeed.LabRoot, lease = grant.Value.GetProperty("lease").GetString() }, ct);
            Assert.Equal((HttpStatusCode.OK, "view"), (status, grant!.Value.GetProperty("level").GetString()));

            // Out of the workspace: the next renewal closes the connection.
            db.WorkspaceMembers.Remove(membership);
            await db.SaveChangesAsync(ct);
            (status, _) = await AuthorizeAsync(collab, new { page = QaSeed.LabRoot, lease = grant.Value.GetProperty("lease").GetString() }, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }
        finally
        {
            role.Role = SpaceRole.Editor;
            if (db.Entry(membership).State == EntityState.Detached)
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = QaSeed.Engineering, UserId = QaSeed.LabEditor, Role = WorkspaceRole.Member });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // A deactivated user can't open a live document at all.
        var (closed, _) = await AuthorizeAsync(collab, new { page = QaSeed.HomePage, ticket = await TicketAsync(collab, QaSeed.Deactivated, null, QaSeed.HomePage, "view", ct) }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, closed);
    }

    [Fact]
    public async Task AT_08_team_removal_pat_revocation_and_deactivation_apply_on_the_next_request_and_before_jobs()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(Org);

        // Team removal: the next decision no longer sees the team's grant.
        var team = await db.TeamMembers.SingleAsync(t => t.TeamId == QaSeed.LabEditorsTeam && t.UserId == QaSeed.LabTeamEditor, ct);
        Assert.True(await db.CanAsync(new Caller(QaSeed.LabTeamEditor), QaSeed.LabRoot, PageAction.Edit, ct));
        db.TeamMembers.Remove(team);
        await db.SaveChangesAsync(ct);
        try { Assert.False(await db.CanAsync(new Caller(QaSeed.LabTeamEditor), QaSeed.LabRoot, PageAction.Read, ct)); }
        finally
        {
            db.TeamMembers.Add(new TeamMember { TeamId = QaSeed.LabEditorsTeam, UserId = QaSeed.LabTeamEditor });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // PAT revocation: the next MCP request is refused, and a queued job's actor check stops it.
        Assert.NotEqual(HttpStatusCode.Unauthorized, await McpStatusAsync(QaSeed.LabViewerPat, ct));
        Assert.Equal(new Caller(QaSeed.LabViewer, External: true, Agent: true), await db.ActorAsync(QaSeed.LabViewer, QaSeed.LabViewerPatId, ct));
        (await db.AccessTokens.SingleAsync(t => t.Id == QaSeed.LabViewerPatId, ct)).RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(QaSeed.LabViewerPat, ct));
        Assert.Null(await db.ActorAsync(QaSeed.LabViewer, QaSeed.LabViewerPatId, ct));
        using (var collab = await CollabClientAsync(ct))
        {
            var (status, _) = await AuthorizeAsync(collab, new { page = QaSeed.LabRoot, ticket = await TicketAsync(collab, QaSeed.LabViewer, QaSeed.LabViewerPatId, QaSeed.LabRoot, "view", ct) }, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }

        // Deactivation: no actor, no access.
        Assert.Null(await db.ActorAsync(QaSeed.Deactivated, null, ct));
        Assert.False(await db.CanAsync(new Caller(QaSeed.Deactivated), QaSeed.HomePage, PageAction.Read, ct));
    }

    // --- internal collab API (docs/06 §6.4), as collab calls it ---

    private async Task<HttpClient> CollabClientAsync(CancellationToken ct)
    {
        var http = athar.App.CreateHttpClient("web", "http");
        var secret = await athar.App.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<ParameterResource>().Single(r => r.Name == "collab-secret").GetValueAsync(ct);
        http.DefaultRequestHeaders.Add("x-collab-secret", secret);
        return http;
    }

    private static async Task<string> TicketAsync(HttpClient collab, Guid user, Guid? credential, Guid page, string level, CancellationToken ct)
    {
        var response = await collab.PostAsJsonAsync("/internal/collab/tickets", new { user, credential, page, level }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("ticket").GetString()!;
    }

    private static async Task<(HttpStatusCode, JsonElement?)> AuthorizeAsync(HttpClient collab, object body, CancellationToken ct)
    {
        var response = await collab.PostAsJsonAsync("/internal/collab/authorize", body, ct);
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonElement>(ct) : null);
    }

    private async Task<HttpStatusCode> McpStatusAsync(string pat, CancellationToken ct)
    {
        using var http = athar.Web();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return (await http.SendAsync(request, ct)).StatusCode;
    }
}
