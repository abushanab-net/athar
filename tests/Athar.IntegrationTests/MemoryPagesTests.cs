using System.Net;
using Athar.Data.Seed;
using Athar.Web.Components.Brain;

namespace Athar.IntegrationTests;

/// <summary>T022 pages (FR-MM-09): signed in with the Identity cookie, each page prerenders the caller's data.</summary>
[Collection(AtharCollection.Name)]
public class MemoryPagesTests(AtharApp athar)
{
    [Fact]
    public void Line_diff_marks_removed_and_added_lines()
    {
        var diff = LineDiff.Compute("a\nb\nc", "a\nB\nc\nd");
        Assert.Equal(
            [(DiffOp.Same, "a"), (DiffOp.Removed, "b"), (DiffOp.Added, "B"), (DiffOp.Same, "c"), (DiffOp.Added, "d")],
            diff.Select(l => (l.Op, l.Text)));
        Assert.Equal([(DiffOp.Added, "x"), (DiffOp.Added, "y")], LineDiff.Compute("", "x\ny").Select(l => (l.Op, l.Text)));
    }

    [Fact]
    public async Task FR_MM_09_pages_render_for_the_signed_in_user()
    {
        var ct = AtharApp.Timeout();
        using var member = await SignedIn("Qa Member", ct);
        using (var anonymous = new Browser(athar.App.GetEndpoint("web", "https")))
            Assert.Equal(HttpStatusCode.Redirect, (await anonymous.Http.GetAsync("/memories", ct)).StatusCode);

        // The browser lists what each person may see: the member's own personal memory, hidden from the knowledge owner.
        Assert.Contains("Athar uses MudBlazor", await member.Http.GetStringAsync("/memories?scope=project&type=decision", ct));
        Assert.Contains("I prefer small PRs", await member.Http.GetStringAsync("/memories?scope=personal&type=preference", ct));

        var detail = await member.Http.GetStringAsync($"/memories/{QaSeed.MemoryProject}", ct);
        Assert.Contains("MudBlazor (MIT) over Fluent UI", detail);

        // The member follows their own proposals; the knowledge owner reviews the open queue with approve and reject.
        Assert.Contains("Releases are tagged vX.Y.Z", await member.Http.GetStringAsync("/review/proposals", ct));
        using var owner = await SignedIn("Qa Knowledge Owner", ct);
        Assert.DoesNotContain("I prefer small PRs", await owner.Http.GetStringAsync("/memories?scope=personal&type=preference", ct));
        var queue = await owner.Http.GetStringAsync("/review/proposals", ct);
        Assert.Contains("Releases are tagged vX.Y.Z", queue);
        Assert.Contains("Use pnpm 9 for Node packages", queue); // needs_rereview
        Assert.Equal(HttpStatusCode.OK, (await member.Http.GetAsync("/review/inbox", ct)).StatusCode);
        // T150: the old addresses (bookmarks, emails) redirect to the Review tabs.
        using var old = await member.Http.GetAsync("/inbox", ct);
        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal("/review/inbox", old.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Notifications_page_lists_every_notification_with_a_link()
    {
        var ct = AtharApp.Timeout();
        // T150: the bell shows the latest ten; /notifications shows them all, each row a link to what it is about.
        using var admin = await SignedIn("Qa Admin", ct);
        var page = await admin.Http.GetStringAsync("/notifications", ct);
        Assert.Contains("mentioned you", page);
        Assert.Contains("href=\"/wiki/p/", page);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync("/notifications/unread", ct)).StatusCode);
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
