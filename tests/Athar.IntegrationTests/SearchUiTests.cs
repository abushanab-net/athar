using Aspire.Hosting;
using Athar.Data.Seed;

namespace Athar.IntegrationTests;

/// <summary>T047: the search page (FR-SR-01) lists only what the signed-in person may read, and filters by kind.</summary>
[Collection(AtharCollection.Name)]
public class SearchUiTests(AtharApp athar)
{
    private async Task<Browser> SignInAsync(string who, CancellationToken ct)
    {
        var browser = new Browser(athar.App.GetEndpoint("web", "https"));
        await browser.SubmitAsync("/account/login", new() { ["Input.Email"] = QaSeed.Email(who), ["Input.Password"] = QaSeed.Password }, ct);
        return browser;
    }

    [Fact]
    public async Task Search_page_shows_readable_hits_filters_by_kind_and_hides_confidential_pages()
    {
        var ct = AtharApp.Timeout();
        using var member = await SignInAsync("Qa Member", ct);
        using var admin = await SignInAsync("Qa Admin", ct);
        string Link(Guid page) => $"href=\"/wiki/p/{page}\"";

        // Seeded pages are searchable (FTS chunks in the qa seed), and the prerendered page carries the hits.
        var html = await member.Http.GetStringAsync("/search?q=runbook", ct);
        Assert.Contains(Link(QaSeed.DeployRunbookPage), html);
        Assert.Contains("role=\"search\"", html);
        Assert.DoesNotContain(Link(QaSeed.DeployRunbookPage), await member.Http.GetStringAsync("/search?q=runbook&kind=memory", ct));

        // A confidential page reaches only people with a role in its space: not even its title or link leaks. (Pages only: a
        // confidential company memory that mentions the bands is readable by every member, as it should be.)
        var forMember = await member.Http.GetStringAsync("/search?q=salary%20bands&kind=page", ct);
        Assert.DoesNotContain(Link(QaSeed.CompensationPage), forMember);
        Assert.DoesNotContain("Compensation bands", forMember);
        Assert.Contains(Link(QaSeed.CompensationPage), await admin.Http.GetStringAsync("/search?q=salary%20bands&kind=page", ct));
    }
}
