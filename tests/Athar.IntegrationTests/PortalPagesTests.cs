using System.Net;
using Athar.Data.Seed;

namespace Athar.IntegrationTests;

/// <summary>
/// T151 (Phase R): pages for back-end features that had no UI. Each one prerenders the caller's own view, with the same
/// visibility rules as the API: conflicts on readable memories (FR-MM-08), projects and their sources (FR-PJ-01, FR-KS-01).
/// </summary>
[Collection(AtharCollection.Name)]
public class PortalPagesTests(AtharApp athar)
{
    [Fact]
    public async Task Conflicts_and_projects_have_pages()
    {
        var ct = AtharApp.Timeout();
        using var member = await SignedIn("Qa Member", ct);

        // Review › Conflicts shows both sides of the seeded pair, with the four resolutions.
        var conflicts = await member.Http.GetStringAsync("/review/conflicts", ct);
        Assert.Contains("Session transcripts are kept 30 days", conflicts);
        Assert.Contains("Session transcripts are kept 90 days", conflicts);
        Assert.Contains("href=\"/review/conflicts\"", conflicts); // the Review tab

        // The member's projects, and the pilot project with its linked repository and members.
        var projects = await member.Http.GetStringAsync("/projects", ct);
        Assert.Contains("Athar pilot", projects);
        Assert.DoesNotContain("Billing", projects);
        Assert.Contains("Qa Admin", await member.Http.GetStringAsync($"/projects/{QaSeed.PilotProject}", ct));
        using var admin = await SignedIn("Qa Admin", ct); // English UI; the member's is Arabic
        var pilot = await admin.Http.GetStringAsync($"/projects/{QaSeed.PilotProject}", ct);
        Assert.Contains("readable by this project", pilot);
        foreach (var action in new[] { "Add a web page", "Upload a document" }) Assert.Contains(action, pilot); // T142: URL and document sources
    }

    [Fact]
    public async Task Pages_link_to_their_handbook_section()
    {
        // T153: page headers and tours deep-link into /help (T144); every anchor they use must exist there.
        var ct = AtharApp.Timeout();
        using var admin = await SignedIn("Qa Admin", ct);
        var help = await admin.Http.GetStringAsync("/help", ct);
        var anchors = new HashSet<string>();
        foreach (var path in new[] { "/", "/wiki", "/search", "/memories", "/review/inbox", "/review/proposals", "/review/conflicts", "/projects",
                     "/notifications", "/account/tokens", "/agents", "/sessions", "/skills", "/admin/users", "/admin/teams", "/workspaces", "/admin/tokens", "/admin/audit" })
        {
            var html = await admin.Http.GetStringAsync(path, ct);
            var found = System.Text.RegularExpressions.Regex.Matches(html, "href=\"/help#([a-z-]+)\"").Select(m => m.Groups[1].Value).ToList();
            Assert.True(found.Count > 0, $"{path} has no help link");
            anchors.UnionWith(found);
        }
        foreach (var a in anchors) Assert.Contains($"id=\"{a}\"", help);
    }

    [Fact]
    public async Task A_project_is_hidden_from_people_outside_it()
    {
        var ct = AtharApp.Timeout();
        using var owner = await SignedIn("Qa Knowledge Owner", ct); // a plain member, in Billing only
        Assert.Contains("isn&#x27;t shared with you", await owner.Http.GetStringAsync($"/projects/{QaSeed.PilotProject}", ct));
    }

    private async Task<Browser> SignedIn(string displayName, CancellationToken ct)
    {
        var browser = new Browser(athar.App.GetEndpoint("web", "https"));
        var response = await browser.SubmitAsync("/account/login",
            new() { ["Input.Email"] = QaSeed.Email(displayName), ["Input.Password"] = QaSeed.Password }, ct);
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.OK);
        return browser;
    }
}
