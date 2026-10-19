using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T087: the worker's daily trash purge (FR-PG-01) across every org.</summary>
[Collection(AtharCollection.Name)]
public class RetentionTests(AtharApp athar)
{
    [Fact]
    public async Task Trash_older_than_30_days_is_purged_in_every_org_children_first()
    {
        var ct = AtharApp.Timeout();
        var now = DateTimeOffset.UtcNow;
        Guid parent = Guid.CreateVersion7(), child = Guid.CreateVersion7(), recent = Guid.CreateVersion7(), other = Guid.CreateVersion7();
        Page Make(Guid id, Guid org, Guid space, Guid? parentId, int daysAgo) => new()
        {
            Id = id, OrgId = org, SpaceId = space, ParentId = parentId, Position = 99, Title = "T087 " + daysAgo,
            ContentJson = "{}", ContentMd = "", Revision = 1, DeletedAt = now.AddDays(-daysAgo),
        };
        await using (var db = athar.Db(QaSeed.Org))
        {
            db.Pages.AddRange(Make(parent, QaSeed.Org, QaSeed.EngWiki, null, 31), Make(child, QaSeed.Org, QaSeed.EngWiki, parent, 31), Make(recent, QaSeed.Org, QaSeed.EngWiki, null, 29));
            await db.SaveChangesAsync(ct);
        }
        await using (var db = athar.Db(QaSeed.OtherOrg))
        {
            db.Pages.Add(Make(other, QaSeed.OtherOrg, QaSeed.OtherOrgSpace, null, 40));
            await db.SaveChangesAsync(ct);
        }

        await using (var db = athar.Db(QaSeed.Org))
            Assert.True(await TrashRetention.PurgeAllAsync(db, now, ct) >= 3);

        await using var check = athar.Db(QaSeed.Org);
        var left = await check.Pages.IgnoreQueryFilters().Where(p => p.Id == parent || p.Id == child || p.Id == recent || p.Id == other).Select(p => p.Id).ToListAsync(ct);
        Assert.Equal([recent], left);
        var audits = await check.AuditEvents.IgnoreQueryFilters().Where(e => e.Action == "page.purge").ToListAsync(ct);
        Assert.Contains(audits, e => e.OrgId == QaSeed.Org && e.ActorUserId == null && e.ResourceId.Contains(child.ToString()));
        Assert.Contains(audits, e => e.OrgId == QaSeed.OtherOrg && e.ResourceId.Contains(other.ToString()));
    }
}
