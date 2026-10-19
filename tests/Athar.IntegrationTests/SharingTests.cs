using System.Net;
using System.Text.RegularExpressions;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Athar.Web.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Athar.IntegrationTests;

/// <summary>T044 sharing (FR-SH-01..04): AT-49 direct page shares, AT-20 share links run locally.</summary>
[Collection(AtharCollection.Name)]
public partial class SharingTests(AtharApp athar)
{
    private static readonly HashSet<string> Scopes = ["read", "write"];
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, Scopes, DisclosureClass.TrustedLocal, null);
    private static readonly Actor LabAdmin = Person(QaSeed.LabAdmin);

    private ShareService Shares(AtharDbContext? db = null)
    {
        db ??= athar.Db(QaSeed.Org);
        return new ShareService(db, new NotificationService(db), TimeProvider.System);
    }

    private PageService Pages() => new(athar.Db(QaSeed.Org), TimeProvider.System);

    private async Task<bool> Can(Guid user, Guid page, PageAction action)
    {
        await using var db = athar.Db(QaSeed.Org);
        return await db.CanAsync(new Caller(user), page, action, AtharApp.Timeout());
    }

    [Fact]
    public async Task AT_49_a_direct_page_share_grants_exactly_that_page_to_workspace_members_only()
    {
        var ct = AtharApp.Timeout();
        var page = await Pages().CreateAsync(new(QaSeed.LabAdmin, QaSeed.Org), QaSeed.AccessLab, QaSeed.LabRoot, "AT-49 shared", ct);
        var child = await Pages().CreateAsync(new(QaSeed.LabAdmin, QaSeed.Org), QaSeed.AccessLab, page, "AT-49 child", ct);
        Assert.False(await Can(QaSeed.LabShareUser, page, PageAction.Read));

        await Shares().ShareAsync(LabAdmin, page, QaSeed.LabShareUser, null, ShareLevel.View, ct);
        Assert.True(await Can(QaSeed.LabShareUser, page, PageAction.Read));
        Assert.False(await Can(QaSeed.LabShareUser, page, PageAction.Comment));
        Assert.False(await Can(QaSeed.LabShareUser, child, PageAction.Read)); // only that page

        await Shares().ShareAsync(LabAdmin, page, QaSeed.LabShareUser, null, ShareLevel.Comment, ct);
        Assert.True(await Can(QaSeed.LabShareUser, page, PageAction.Comment));
        Assert.False(await Can(QaSeed.LabShareUser, page, PageAction.Edit));
        await Shares().ShareAsync(LabAdmin, page, QaSeed.LabShareUser, null, ShareLevel.Edit, ct);
        Assert.True(await Can(QaSeed.LabShareUser, page, PageAction.Edit));

        // Not a member of the workspace: a share doesn't let them in, and they aren't told about it.
        await Shares().ShareAsync(LabAdmin, page, QaSeed.LabOutsider, null, ShareLevel.Edit, ct);
        Assert.False(await Can(QaSeed.LabOutsider, page, PageAction.Read));

        await using var db = athar.Db(QaSeed.Org);
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == QaSeed.LabShareUser && n.Kind == "share" && n.Ref == $"page:{page}", ct));
        Assert.False(await db.Notifications.AnyAsync(n => n.UserId == QaSeed.LabOutsider && n.Ref == $"page:{page}", ct));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "page.share" && a.ActorUserId == QaSeed.LabAdmin, ct));

        // Only editors and space admins share; a viewer can't.
        var e = await Assert.ThrowsAsync<AtharError>(() => Shares().ShareAsync(Person(QaSeed.LabViewer), page, QaSeed.LabCommenter, null, ShareLevel.View, ct));
        Assert.Equal("forbidden", e.Code);
    }

    [Fact]
    public async Task AT_20_a_share_link_locally()
    {
        var ct = AtharApp.Timeout();
        // LabRoot's subtree holds a restricted page (LabRestricted) and our own pages.
        var stays = await Pages().CreateAsync(new(QaSeed.LabAdmin, QaSeed.Org), QaSeed.AccessLab, QaSeed.LabRoot, "AT-20 stays", ct);
        var leaves = await Pages().CreateAsync(new(QaSeed.LabAdmin, QaSeed.Org), QaSeed.AccessLab, QaSeed.LabRoot, "AT-20 moves out", ct);
        var link = await Shares().CreateLinkAsync(LabAdmin, QaSeed.LabRoot, null, includeDescendants: true, expiresAt: null, password: "open sesame", ct);
        using var web = Client();

        // Password first: the form, then a wrong password is refused.
        var form = await web.GetAsync(link.Path, ct);
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Equal("no-store", form.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", string.Join(",", form.Headers.GetValues("Referrer-Policy")));
        var wrong = await Unlock(web, link.Path, await form.Content.ReadAsStringAsync(ct), "nope", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // The right password unlocks it for this link: read-only content, restricted pages absent.
        var right = await Unlock(web, link.Path, await (await web.GetAsync(link.Path, ct)).Content.ReadAsStringAsync(ct), "open sesame", ct);
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        var root = await (await web.GetAsync(link.Path, ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains("Lab root", root);
        Assert.Contains("AT-20 stays", root);
        Assert.DoesNotContain("Lab restricted", root);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync($"{link.Path}/{QaSeed.LabRestricted}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync($"{link.Path}/{stays}", ct)).StatusCode);
        Assert.DoesNotContain("<script", root); // no diagram on this page, so no script (T120)

        // A page moved out of the shared subtree leaves the share.
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync($"{link.Path}/{leaves}", ct)).StatusCode);
        await Pages().MoveAsync(new(QaSeed.LabAdmin, QaSeed.Org), leaves, QaSeed.AccessLab, null, ct);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync($"{link.Path}/{leaves}", ct)).StatusCode);

        // Views are audited, never with the token.
        var token = link.Path["/s/".Length..];
        await using (var db = athar.Db(QaSeed.Org))
        {
            Assert.True(await db.AuditEvents.CountAsync(a => a.Action == "share_link.view" && a.ResourceId == link.Id.ToString(), ct) >= 3);
            var linkAudit = await db.AuditEvents.Where(a => a.ResourceId == link.Id.ToString()).ToListAsync(ct);
            Assert.DoesNotContain(linkAudit, a => (a.After ?? "").Contains(token) || (a.Before ?? "").Contains(token));
        }

        // Revoked: 410.
        await Shares().RevokeLinkAsync(LabAdmin, link.Id, ct);
        Assert.Equal(HttpStatusCode.Gone, (await web.GetAsync(link.Path, ct)).StatusCode);
    }

    [Fact]
    public async Task T120_a_shared_diagram_loads_only_the_diagram_script()
    {
        var ct = AtharApp.Timeout();
        var page = await Pages().CreateAsync(new(QaSeed.LabAdmin, QaSeed.Org), QaSeed.AccessLab, QaSeed.LabRoot, "T120 diagram", ct);
        await using (var db = athar.Db(QaSeed.Org))
        {
            var p = await db.Pages.SingleAsync(x => x.Id == page, ct);
            p.ContentJson = """{"type":"doc","content":[{"type":"codeBlock","attrs":{"language":"mermaid"},"content":[{"type":"text","text":"graph TD; A-->B"}]}]}""";
            await db.SaveChangesAsync(ct);
        }
        using var web = Client();

        var withDiagram = await web.GetAsync((await Shares().CreateLinkAsync(LabAdmin, page, null, false, null, null, ct)).Path, ct);
        var html = await withDiagram.Content.ReadAsStringAsync(ct);
        Assert.Contains("<pre class=\"mermaid-source\"><code class=\"language-mermaid\">graph TD; A--&gt;B</code></pre>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<script"));
        Assert.Contains($"<script type=\"module\" src=\"{ShareHtml.MermaidScript}\"></script>", html);
        Assert.EndsWith("; script-src 'self'", withDiagram.Headers.GetValues("Content-Security-Policy").Single());

        // A page without a diagram stays scriptless, and so does its CSP.
        var plain = await web.GetAsync((await Shares().CreateLinkAsync(LabAdmin, QaSeed.LabRoot, null, false, null, null, ct)).Path, ct);
        Assert.DoesNotContain("<script", await plain.Content.ReadAsStringAsync(ct));
        Assert.DoesNotContain("script-src", plain.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public void T120_page_text_cannot_fake_the_diagram_marker()
    {
        var json = $$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{ShareHtml.MermaidMarker.Replace("\"", "\\\"")}}"}]},{"type":"codeBlock","attrs":{"language":"js"},"content":[{"type":"text","text":"x"}]}]}""";
        var body = ShareHtml.Content(json, _ => null);
        Assert.False(ShareHtml.HasMermaid(body));
        Assert.Contains("<pre><code>x</code></pre>", body); // other languages render as before
    }

    [Fact]
    public async Task AT_20_wrong_passwords_are_rate_limited_and_expired_links_are_gone()
    {
        var ct = AtharApp.Timeout();
        var link = await Shares().CreateLinkAsync(LabAdmin, QaSeed.LabRoot, null, false, null, "right one", ct);
        using var web = Client();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 6; i++)
            last = await Unlock(web, link.Path, await (await web.GetAsync(link.Path, ct)).Content.ReadAsStringAsync(ct), "wrong", ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);

        var expiring = await Shares().CreateLinkAsync(LabAdmin, QaSeed.LabRoot, null, false, DateTimeOffset.UtcNow.AddSeconds(2), null, ct);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync(expiring.Path, ct)).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.Equal(HttpStatusCode.Gone, (await web.GetAsync(expiring.Path, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync("/s/not-a-real-token", ct)).StatusCode);
    }

    [Fact]
    public async Task AT_20_attachments_only_through_shared_pages()
    {
        var ct = AtharApp.Timeout();
        var admin = Person(QaSeed.Admin); // Engineering wiki admin
        var ops = await athar.OpsAsync();
        var shared = (await ops.CreatePageAsync(admin, QaSeed.EngWiki, null, "AT-20 files", "Files.", Guid.NewGuid().ToString(), ct)).PageId;
        var other = (await ops.CreatePageAsync(admin, QaSeed.EngWiki, null, "AT-20 not shared", "Other.", Guid.NewGuid().ToString(), ct)).PageId;
        // Same folder as web's store: both default to the machine's temp folder (AttachmentStore).
        async Task<Guid> Upload(Guid page) => (await new AttachmentService(athar.Db(QaSeed.Org), new Data.AttachmentStore(new ConfigurationBuilder().Build()), TimeProvider.System)
            .UploadAsync(admin, page, "dot.png", "image/png", new MemoryStream(Png), ct)).AttachmentId;
        var mine = await Upload(shared);
        var theirs = await Upload(other);
        // The editor inserts uploads as /attachments/{id}; a page may also point at a file of a page outside the share.
        await ops.RunAsync(admin, shared, Guid.NewGuid().ToString(), new PageOp("append", Markdown: $"![dot](/attachments/{mine})\n\n[elsewhere](/attachments/{theirs})"), ct);

        var link = await Shares().CreateLinkAsync(admin, shared, null, false, null, null, ct);
        using var web = Client();
        var html = await (await web.GetAsync(link.Path, ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains($"<img src=\"{link.Path}/attachments/{mine}\"", html);
        Assert.DoesNotContain(theirs.ToString(), html); // dropped, the link text stays
        Assert.Contains("elsewhere", html);

        using (var file = await web.GetAsync($"{link.Path}/attachments/{mine}", ct))
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);
            Assert.Equal("default-src 'none'; sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-store", file.Headers.CacheControl?.ToString());
            Assert.Equal(Png, await file.Content.ReadAsByteArrayAsync(ct));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync($"{link.Path}/attachments/{theirs}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync($"{link.Path}/attachments/{Guid.NewGuid()}", ct)).StatusCode);

        // A password link serves files only once unlocked; a revoked link serves nothing.
        var locked = await Shares().CreateLinkAsync(admin, shared, null, false, null, "files please", ct);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync($"{locked.Path}/attachments/{mine}", ct)).StatusCode);
        await Unlock(web, locked.Path, await (await web.GetAsync(locked.Path, ct)).Content.ReadAsStringAsync(ct), "files please", ct);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync($"{locked.Path}/attachments/{mine}", ct)).StatusCode);
        await Shares().RevokeLinkAsync(admin, link.Id, ct);
        Assert.Equal(HttpStatusCode.Gone, (await web.GetAsync($"{link.Path}/attachments/{mine}", ct)).StatusCode);
    }

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Space_links_are_for_space_admins()
    {
        var ct = AtharApp.Timeout();
        var e = await Assert.ThrowsAsync<AtharError>(() => Shares().CreateLinkAsync(Person(QaSeed.LabEditor), null, QaSeed.AccessLab, false, null, null, ct));
        Assert.Equal("forbidden", e.Code);
        var link = await Shares().CreateLinkAsync(LabAdmin, null, QaSeed.AccessLab, false, null, null, ct);
        using var web = Client();
        var html = await (await web.GetAsync(link.Path, ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains("Lab root", html);
        Assert.DoesNotContain("Lab restricted", html);
        Assert.Contains(await Shares().ListLinksAsync(LabAdmin, null, QaSeed.AccessLab, ct), l => l.Id == link.Id);
    }

    private HttpClient Client() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = true, CookieContainer = new(),
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { BaseAddress = athar.App.GetEndpoint("web", "https") };

    [GeneratedRegex("name=\"(?<name>[^\"]+)\" value=\"(?<value>[^\"]+)\"")]
    private static partial Regex Hidden();

    /// <summary>Posts the password form with its antiforgery field.</summary>
    private static Task<HttpResponseMessage> Unlock(HttpClient web, string path, string formHtml, string password, CancellationToken ct)
    {
        var field = Hidden().Match(formHtml);
        Assert.True(field.Success, "no antiforgery field in the password form");
        return web.PostAsync($"{path}/unlock", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [field.Groups["name"].Value] = WebUtility.HtmlDecode(field.Groups["value"].Value),
            ["password"] = password,
        }), ct);
    }
}
