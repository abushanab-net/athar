using Athar.Data.Seed;
using Athar.Web.Components.Shared;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T144 (FR-AG-10, AT-66): the handbook at /help and the seeded "Getting started with agents" page. The page and the admin action are in the smoke tests.</summary>
[Collection(AtharCollection.Name)]
public class HandbookTests(AtharApp athar)
{
    [Fact]
    public void AT_66_the_guides_are_embedded_and_their_anchors_are_stable()
    {
        // Lane B's inline help and tours link to these anchors (I-3): keep them.
        var user = MarkdownView.Render(HelpService.Guide("user-guide"), "");
        foreach (var anchor in new[] { "agents", "capture", "my-sessions", "skills", "review", "memories", "wiki" })
            Assert.Contains($"id=\"{anchor}\"", user);
        var admin = MarkdownView.Render(HelpService.Guide("admin-guide"), "admin-");
        Assert.Contains("id=\"admin-users\"", admin);
        Assert.Contains("id=\"admin-capture-and-privacy\"", admin);
        Assert.DoesNotContain("<script", user + admin);
        // The Arabic guides carry the English anchors ({#agents}), section for section, so deep links work in both languages.
        foreach (var name in new[] { "user-guide", "admin-guide" })
        {
            var en = System.Text.RegularExpressions.Regex.Matches(HelpService.Guide(name), @"^## (.+)$", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value.Trim().ToLowerInvariant(), @"[^\p{L}\p{Nd}\s_-]", ""), @"\s", "-"));
            var ar = System.Text.RegularExpressions.Regex.Matches(HelpService.Guide(name, "ar"), @"^## .+\{#([a-z0-9-]+)\}\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value);
            Assert.Equal(en, ar);
        }
        // Without a prefix the view stays as it was: no ids (memory bodies).
        Assert.DoesNotContain("id=", MarkdownView.Render("## Agents"));
    }

    [Fact]
    public async Task AT_66_the_qa_seed_has_the_getting_started_page_in_english_and_arabic()
    {
        var ct = AtharApp.Timeout();
        await using var db = athar.Db(QaSeed.Org);
        var pages = await db.Pages.Where(p => GettingStarted.QaPages.Contains(p.Id)).OrderBy(p => p.Position).ToListAsync(ct);
        Assert.Equal(["Getting started with agents", "البدء مع الوكلاء"], pages.Select(p => p.Title));
        Assert.All(pages, p =>
        {
            Assert.Equal(QaSeed.OpsWiki, p.SpaceId);
            Assert.Contains("\"heading\"", p.ContentJson); // jsonb reformats the JSON
            Assert.Contains("\"bulletList\"", p.ContentJson);
            Assert.DoesNotContain("# ", p.ContentMd![..2]); // the title heading isn't repeated in the body
        });
        Assert.Contains("My sessions", pages[0].ContentMd);
        Assert.Contains("جلساتي", pages[1].ContentMd);
        Assert.True(await db.Chunks.AnyAsync(c => c.OwnerId == pages[1].Id && c.SearchText.Contains("الوكلاء"), ct)); // searchable
    }
}
