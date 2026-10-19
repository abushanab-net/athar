using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Athar.Web.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

/// <summary>
/// T119 (U-1), AT-34 style: a draft never leaves its editors. Search (even an editor's), MCP tools and resources, REST reads,
/// breadcrumbs and share links all leave it out; once published it shows up, so the checks aren't vacuous.
/// </summary>
[Collection(AtharCollection.Name)]
public class DraftLeakTests(AtharApp athar)
{
    private static readonly Actor Member = new(QaSeed.Member, QaSeed.Org, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);
    private static readonly WikiActor MemberWiki = new(QaSeed.Member, QaSeed.Org);

    private PageService Pages() => new(athar.Db(QaSeed.Org), TimeProvider.System);

    [Fact]
    public async Task A_draft_stays_out_of_search_mcp_rest_breadcrumbs_and_share_links()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var word = $"zephyrdraft{Guid.NewGuid():N}"[..20];

        // An EngWiki draft with a findable word, and a published child under it.
        var draft = await Pages().CreateAsync(MemberWiki, QaSeed.EngWiki, null, $"Secret draft {word}", ct, draft: true);
        var ops = await athar.OpsAsync();
        var token = (await ops.GetPageAsync(Member, draft, ct: ct)).LiveToken;
        await ops.RunAsync(Member, draft, Guid.NewGuid().ToString(), new PageOp("replace", $"Plans about {word}.", ExpectedLiveToken: token), ct);
        var child = await Pages().CreateAsync(MemberWiki, QaSeed.EngWiki, draft, "Published child", ct);
        while (true) // indexed like any page: only the readers filter it
        {
            await using var db = athar.Db(QaSeed.Org);
            if (await db.Chunks.AnyAsync(c => c.OwnerId == draft && c.Body.Contains(word), ct)) break;
            await Task.Delay(250, ct);
        }

        // Search: not even its own editor finds it (FTS here; the embedder is off, which doesn't matter for this word).
        await using (var db = athar.Db(QaSeed.Org))
            Assert.DoesNotContain((await Search(db).SearchAsync(Member, new(word, ["page"]), ct)).Hits, h => h.Id == draft);

        // Agents: MCP search, get_page and the page resource, and REST, as the same member's token.
        using var http = athar.Web();
        await using var agent = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.MemberPat, ct);
        Assert.DoesNotContain((await Mcp.Call(agent, "search", new() { ["query"] = word }, ct)).GetProperty("hits").EnumerateArray(), h => h.Str("id") == draft.ToString());
        await Mcp.CallError(agent, "get_page", new() { ["page_id"] = draft.ToString() }, ct);
        await Assert.ThrowsAnyAsync<Exception>(async () => await agent.ReadResourceAsync($"athar://page/{draft}", cancellationToken: ct));
        Assert.Contains((await Mcp.Get(http, $"/api/v1/pages/{draft}", QaSeed.MemberPat, ct)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });

        // A published page under a draft: readable, but the breadcrumb doesn't name the draft.
        var childView = await Mcp.Call(agent, "get_page", new() { ["page_id"] = child.ToString() }, ct);
        Assert.DoesNotContain(word, childView.ToString());

        // Share links: a space link leaves out the draft and what's under it; a link to the draft shows nothing.
        await using (var db = athar.Db(QaSeed.Org))
        {
            var shares = new ShareService(db, new NotificationService(db), TimeProvider.System);
            var admin = Member with { UserId = QaSeed.Admin };
            var space = await shares.CreateLinkAsync(admin, null, QaSeed.EngWiki, false, null, null, ct);
            var shown = (await shares.PagesAsync((await shares.FindAsync(space.Path["/s/".Length..], ct))!, ct)).Select(p => p.Id).ToList();
            Assert.NotEmpty(shown);
            Assert.DoesNotContain(draft, shown);
            Assert.DoesNotContain(child, shown);
            var direct = await shares.CreateLinkAsync(admin, draft, null, true, null, null, ct);
            Assert.Empty(await shares.PagesAsync((await shares.FindAsync(direct.Path["/s/".Length..], ct))!, ct));
        }

        // Published: now it's found, so the checks above weren't vacuous.
        await Pages().PublishAsync(MemberWiki, draft, ct);
        await using (var db = athar.Db(QaSeed.Org))
            Assert.Contains((await Search(db).SearchAsync(Member, new(word, ["page"]), ct)).Hits, h => h.Id == draft);
        Assert.Equal(HttpStatusCode.OK, (await Mcp.Get(http, $"/api/v1/pages/{draft}", QaSeed.MemberPat, ct)).Status);
    }

    private static SearchService Search(Athar.Data.AtharDbContext db) =>
        new(db, new QueryEmbedder(new ConfigurationBuilder().Build(), NullLogger<QueryEmbedder>.Instance));
}
