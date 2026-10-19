using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T112 (U-1): a draft is visible only to people who can edit it, never to agents; publishing shows it.</summary>
[Collection(AtharCollection.Name)]
public class DraftTests(AtharApp athar)
{
    private static readonly WikiActor Member = new(QaSeed.Member, QaSeed.Org);        // EngWiki editor
    private static readonly WikiActor Guest = new(QaSeed.Guest, QaSeed.Org);          // EngWiki viewer

    private PageService Pages() => new(athar.Db(QaSeed.Org), TimeProvider.System);

    private async Task<bool> Can(Caller caller, Guid page, PageAction action)
    {
        await using var db = athar.Db(QaSeed.Org);
        return await db.CanAsync(caller, page, action, AtharApp.Timeout());
    }

    [Fact]
    public async Task A_draft_is_for_its_editors_until_published()
    {
        var ct = AtharApp.Timeout();
        var id = await Pages().CreateAsync(Member, QaSeed.EngWiki, null, "T112 draft", ct, draft: true);
        var node = (await Pages().TreeAsync(Member, QaSeed.EngWiki, ct: ct)).Single(p => p.Id == id);
        Assert.Equal(PageStatus.Draft, node.Status);

        // Editors: the author and the space admin. Not the viewer or the commenter, for any action; never an agent.
        Assert.True(await Can(new(QaSeed.Member), id, PageAction.Read));
        Assert.True(await Can(new(QaSeed.Admin), id, PageAction.Edit));
        Assert.False(await Can(new(QaSeed.Guest), id, PageAction.Read));
        Assert.False(await Can(new(QaSeed.KnowledgeOwner), id, PageAction.Read));
        Assert.False(await Can(new(QaSeed.KnowledgeOwner), id, PageAction.Comment));
        Assert.False(await Can(new(QaSeed.Member, Agent: true), id, PageAction.Read));
        Assert.DoesNotContain(await Pages().TreeAsync(Guest, QaSeed.EngWiki, ct: ct), p => p.Id == id);
        await using (var db = athar.Db(QaSeed.Org))
            Assert.False(await db.PagesFor(new Caller(QaSeed.Guest)).AnyAsync(p => p.Id == id, ct)); // the EF filter agrees

        // Only an editor publishes.
        await Assert.ThrowsAsync<AtharError>(() => Pages().PublishAsync(Guest, id, ct));
        await Pages().PublishAsync(Member, id, ct);
        Assert.True(await Can(new(QaSeed.Guest), id, PageAction.Read));
        Assert.True(await Can(new(QaSeed.KnowledgeOwner), id, PageAction.Comment));
        Assert.True(await Can(new(QaSeed.Member, Agent: true), id, PageAction.Read)); // EngWiki lets agents read

        await Pages().UnpublishAsync(Member, id, ct);
        Assert.False(await Can(new(QaSeed.Guest), id, PageAction.Read));

        await using var check = athar.Db(QaSeed.Org);
        var page = await check.Pages.SingleAsync(p => p.Id == id, ct);
        Assert.Equal((QaSeed.Member, PageStatus.Draft), (page.CreatedBy!.Value, page.Status));
        Assert.Equal(QaSeed.Member, page.PublishedBy);
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == "page.publish" && e.ResourceId == id.ToString(), ct));
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == "page.unpublish" && e.ResourceId == id.ToString(), ct));
    }

    [Fact]
    public async Task Pages_made_without_draft_are_published()
    {
        var ct = AtharApp.Timeout();
        var id = await Pages().CreateAsync(Member, QaSeed.EngWiki, null, "T112 published", ct);
        Assert.True(await Can(new(QaSeed.Guest), id, PageAction.Read));
    }
}
