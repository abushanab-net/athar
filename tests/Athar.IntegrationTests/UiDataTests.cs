using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web;
using Athar.Web.Auth;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athar.IntegrationTests;

/// <summary>
/// The names the pages show instead of ids (notification actor, memory and page briefs, pending invitations), and server-side
/// sorting of the memory list.
/// </summary>
[Collection(AtharCollection.Name)]
public class UiDataTests(AtharApp athar)
{
    private static readonly HashSet<string> Scopes = ["read", "write", "memory:write"];
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, Scopes, DisclosureClass.TrustedLocal, null);
    private static string At(string name, Guid id) => $"@[{name}](user:{id})";

    [Fact]
    public async Task A_mention_notification_names_who_and_where()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await athar.OpsAsync()).CreatePageAsync(Person(QaSeed.Admin), QaSeed.EngWiki, null, "Bell names", "Who and where.", Guid.NewGuid().ToString(), ct)).PageId;
        var db = athar.Db(QaSeed.Org);
        var comments = new CommentService(db, await athar.OpsAsync(db: db), TimeProvider.System, new NotificationService(db));
        await comments.AddPageThreadAsync(Person(QaSeed.KnowledgeOwner), page, $"{At("Qa Admin", QaSeed.Admin)}, please look", ct);

        await using var read = athar.Db(QaSeed.Org);
        var note = (await new NotificationService(read).ListAsync(QaSeed.Admin, QaSeed.Org, ct: ct)).First(n => n.Href == $"/wiki/p/{page}");
        Assert.Equal("Qa Knowledge Owner", note.Who);
        Assert.Equal("Bell names", note.Where);
        Assert.Equal(QaSeed.KnowledgeOwner, (await read.Notifications.SingleAsync(n => n.Id == note.Id, ct)).ActorUserId);
    }

    [Fact]
    public async Task Memory_briefs_respect_visibility()
    {
        var ct = AtharApp.Timeout();
        // The member's personal memory is theirs alone; the company policy is everyone's.
        Guid[] ids = [QaSeed.MemoryPersonal, QaSeed.MemoryCompanyPolicy];
        var mine = await (await Memories()).BriefsAsync(Person(QaSeed.Member), ids, ct);
        var admins = await (await Memories()).BriefsAsync(Person(QaSeed.Admin), ids, ct);
        Assert.Equal("I prefer small PRs", mine[QaSeed.MemoryPersonal].Title);
        Assert.Contains(QaSeed.MemoryCompanyPolicy, admins.Keys);
        Assert.DoesNotContain(QaSeed.MemoryPersonal, admins.Keys);
    }

    [Fact]
    public async Task Page_briefs_name_the_space_and_skip_pages_you_cannot_read()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await athar.OpsAsync()).CreatePageAsync(Person(QaSeed.Admin), QaSeed.EngWiki, null, "Brief me", "Body.", Guid.NewGuid().ToString(), ct)).PageId;
        var pages = new PageService(athar.Db(QaSeed.Org), TimeProvider.System);
        var brief = (await pages.BriefsAsync(new(QaSeed.Admin, QaSeed.Org), [page], ct))[page];
        Assert.Equal("Brief me", brief.Title);
        Assert.Equal("Engineering wiki", brief.Space);
        Assert.Empty(await pages.BriefsAsync(new(QaSeed.LabViewer, QaSeed.Org), [page], ct)); // no role in the Engineering wiki
    }

    [Fact]
    public async Task The_memory_list_sorts_before_it_pages()
    {
        var ct = AtharApp.Timeout();
        var memories = await Memories();
        MemoryFilter ByTitle(int skip, int take) => new(null, null, "active", null, null, skip, take, "title", Descending: false);
        var all = (await memories.ListAsync(Person(QaSeed.Admin), ByTitle(0, 100), ct)).Items.Select(i => i.Id).ToList();
        var first = (await memories.ListAsync(Person(QaSeed.Admin), ByTitle(0, 3), ct)).Items.Select(i => i.Id);
        var second = (await memories.ListAsync(Person(QaSeed.Admin), ByTitle(3, 3), ct)).Items.Select(i => i.Id);
        Assert.True(all.Count > 6);
        Assert.Equal(all.Take(6), first.Concat(second)); // pages continue the same order: sorted on the server, not per page

        // Descending is the reverse by title (equal titles keep a stable id order either way).
        var ascendingTitles = (await memories.ListAsync(Person(QaSeed.Admin), ByTitle(0, 100), ct)).Items.Select(i => i.Title).ToList();
        var descendingTitles = (await memories.ListAsync(Person(QaSeed.Admin), ByTitle(0, 100) with { Descending = true }, ct)).Items.Select(i => i.Title);
        Assert.Equal(Enumerable.Reverse(ascendingTitles), descendingTitles);
    }

    [Fact]
    public async Task Admins_see_resend_and_withdraw_pending_invitations()
    {
        var ct = AtharApp.Timeout();
        var admin = await Admin(ct);
        const string email = "pending.invitee@qa.athar.local";
        Assert.Equal(OrgDecision.Allow, await admin.InviteAsync(QaSeed.Org, QaSeed.Admin, email, OrgRole.Member, ct));
        var row = (await admin.InvitationsAsync(QaSeed.Org, QaSeed.Admin, ct)).Single(i => i.Email == email);
        Assert.Empty(await admin.InvitationsAsync(QaSeed.Org, QaSeed.Member, ct)); // members see none

        await using (var db = athar.Db(QaSeed.Org))
        {
            var before = (await db.Invitations.SingleAsync(i => i.Id == row.Id, ct)).TokenHash;
            Assert.Equal(OrgDecision.Allow, await admin.ResendInvitationAsync(QaSeed.Org, QaSeed.Admin, row.Id, ct));
            db.ChangeTracker.Clear();
            Assert.NotEqual(before, (await db.Invitations.SingleAsync(i => i.Id == row.Id, ct)).TokenHash); // the old link stops working
        }

        Assert.Equal(OrgDecision.Forbidden, await admin.RevokeInvitationAsync(QaSeed.Org, QaSeed.Member, row.Id, ct));
        Assert.Equal(OrgDecision.Allow, await admin.RevokeInvitationAsync(QaSeed.Org, QaSeed.Admin, row.Id, ct));
        Assert.DoesNotContain(await admin.InvitationsAsync(QaSeed.Org, QaSeed.Admin, ct), i => i.Id == row.Id);
        await using var check = athar.Db(QaSeed.Org);
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == "invitation.resend" && e.ResourceId == row.Id.ToString(), ct));
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == "invitation.revoke" && e.ResourceId == row.Id.ToString(), ct));
    }

    // --- helpers ---

    private async Task<MemoryService> Memories()
    {
        var db = athar.Db(QaSeed.Org);
        var embedder = new QueryEmbedder(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ollama"] = await athar.App.GetConnectionStringAsync("ollama", AtharApp.Timeout()),
        }).Build(), NullLogger<QueryEmbedder>.Instance);
        return new MemoryService(db, new ProjectService(db), new SearchService(db, embedder));
    }

    private async Task<OrgAdminService> Admin(CancellationToken ct)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:mailpit"] = await athar.App.GetConnectionStringAsync("mailpit", ct),
        }).Build();
        return new OrgAdminService(new Factory(athar), new Mailer(config));
    }
}
