using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T111: the Phase U schema (page status, byline, watchers, avatars) on real Postgres.</summary>
[Collection(AtharCollection.Name)]
public class PhaseUSchemaTests(AtharApp athar)
{
    [Fact]
    public async Task Pages_default_to_published_and_a_draft_stays_a_draft()
    {
        var ct = AtharApp.Timeout();
        Guid published = Guid.CreateVersion7(), draft = Guid.CreateVersion7();
        await using (var db = athar.Db(QaSeed.Org))
        {
            db.Pages.Add(new Page { Id = published, OrgId = QaSeed.Org, SpaceId = QaSeed.EngWiki, Title = "T111 published", CreatedBy = QaSeed.Admin });
            // Draft is not the CLR default, so EF sends it instead of letting the column default ('published') win.
            db.Pages.Add(new Page { Id = draft, OrgId = QaSeed.Org, SpaceId = QaSeed.EngWiki, Title = "T111 draft", Status = PageStatus.Draft });
            db.PageWatches.Add(new PageWatch { OrgId = QaSeed.Org, PageId = draft, UserId = QaSeed.Member });
            await db.SaveChangesAsync(ct);
        }
        await using var check = athar.Db(QaSeed.Org);
        var pages = await check.Pages.Where(p => p.Id == published || p.Id == draft).ToDictionaryAsync(p => p.Id, ct);
        Assert.Equal(PageStatus.Published, pages[published].Status);
        Assert.Equal(QaSeed.Admin, pages[published].CreatedBy);
        Assert.Equal(PageStatus.Draft, pages[draft].Status);
        Assert.True(await check.PageWatches.AnyAsync(w => w.PageId == draft && w.UserId == QaSeed.Member, ct));
        // Seeded pages (made before Phase U) are published.
        Assert.Equal(PageStatus.Published, (await check.Pages.SingleAsync(p => p.Id == QaSeed.DeployRunbookPage, ct)).Status);

        // One watch per person and page.
        check.PageWatches.Add(new PageWatch { OrgId = QaSeed.Org, PageId = draft, UserId = QaSeed.Member });
        await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync(ct));
    }
}
