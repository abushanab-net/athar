using System.Text.Json.Nodes;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Athar.IntegrationTests;

/// <summary>T117 (U-5): Duplicate and Copy to space make drafts with new ids, subpages, content, attachments and an audit.</summary>
[Collection(AtharCollection.Name)]
public class PageCopyTests(AtharApp athar)
{
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, new HashSet<string> { "read", "write" }, DisclosureClass.TrustedLocal, null);
    private static readonly Actor Member = Person(QaSeed.Member);   // EngWiki and AgentSandbox editor
    private static readonly Actor Admin = Person(QaSeed.Admin);     // EngWiki admin, EngConfidential editor
    private static readonly AttachmentStore Store = new(new ConfigurationBuilder().Build());

    private async Task<PageCopyService> Copier(AtharDbContext db) => new(db, await athar.OpsAsync(db: db), new PageService(db, TimeProvider.System), Store);

    [Fact]
    public async Task Duplicate_and_copy_to_space_make_draft_copies_with_subpages_content_and_files()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = athar.Db(QaSeed.Org);
        var ops = await athar.OpsAsync(db: db);

        var root = (await ops.CreatePageAsync(Member, QaSeed.EngWiki, null, $"Copy source {tag}", "First.", Guid.NewGuid().ToString(), ct)).PageId;
        await ops.CreatePageAsync(Member, QaSeed.EngWiki, root, "Copy child", "Child text.", Guid.NewGuid().ToString(), ct);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var file = await new AttachmentService(athar.Db(QaSeed.Org), Store, TimeProvider.System)
            .UploadAsync(Member, root, "dot.png", "image/png", new MemoryStream(png), ct);
        var token = (await ops.GetPageAsync(Member, root, ct: ct)).LiveToken;
        await ops.RunAsync(Member, root, Guid.NewGuid().ToString(), new PageOp("replace", $"Root text {tag}.\n\n[note](/attachments/{file.AttachmentId})", ExpectedLiveToken: token), ct);

        // Duplicate: beside the original, a draft, the person's, subpages included, files copied and links rewritten.
        var copies = await Copier(db);
        var op = Guid.NewGuid().ToString();
        var dup = await copies.DuplicateAsync(Member, root, op, ct: ct);
        Assert.Equal(2, dup.Pages);
        var copy = await db.Pages.AsNoTracking().SingleAsync(p => p.Id == dup.PageId, ct);
        Assert.Equal(($"Copy source {tag} (copy)", QaSeed.EngWiki, (Guid?)null, PageStatus.Draft, (Guid?)QaSeed.Member),
            (copy.Title, copy.SpaceId, copy.ParentId, copy.Status, copy.CreatedBy));
        var content = (await ops.GetPageAsync(Member, dup.PageId, ct: ct)).Content;
        Assert.Contains($"Root text {tag}", content);
        var copiedFile = await db.Attachments.AsNoTracking().SingleAsync(a => a.PageId == dup.PageId, ct);
        Assert.NotEqual(file.AttachmentId, copiedFile.Id);
        Assert.Contains($"/attachments/{copiedFile.Id}", content);
        Assert.DoesNotContain($"/attachments/{file.AttachmentId}", content);
        using (var stored = new MemoryStream()) { await using (var f = Store.Open(copiedFile.StorageKey)) await f.CopyToAsync(stored, ct); Assert.Equal(png, stored.ToArray()); }
        var child = await db.Pages.AsNoTracking().SingleAsync(p => p.ParentId == dup.PageId, ct);
        Assert.Equal(("Copy child", PageStatus.Draft), (child.Title, child.Status));
        Assert.Contains("Child text", (await ops.GetPageAsync(Member, child.Id, ct: ct)).Content);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "page.copy" && e.ResourceId == dup.PageId.ToString(), ct));

        // The same op id again lands on the same pages.
        Assert.Equal(dup.PageId, (await copies.DuplicateAsync(Member, root, op, ct: ct)).PageId);
        Assert.Equal(1, await db.Pages.CountAsync(p => p.ParentId == dup.PageId, ct));

        // Copy to space: same workspace and editor there; the title stays.
        var targets = (await copies.TargetsAsync(Member, root, ct)).Select(t => t.SpaceId).ToList();
        Assert.Contains(QaSeed.AgentSandbox, targets);
        Assert.DoesNotContain(QaSeed.OpsWiki, targets);          // another workspace
        Assert.DoesNotContain(QaSeed.EngConfidential, targets);  // not an editor there
        var moved = await copies.CopyToSpaceAsync(Member, root, QaSeed.AgentSandbox, null, Guid.NewGuid().ToString(), ct);
        var there = await db.Pages.AsNoTracking().SingleAsync(p => p.Id == moved.PageId, ct);
        Assert.Equal((QaSeed.AgentSandbox, $"Copy source {tag}", PageStatus.Draft), (there.SpaceId, there.Title, there.Status));
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<AtharError>(() => copies.CopyToSpaceAsync(Member, root, QaSeed.OpsWiki, null, Guid.NewGuid().ToString(), ct))).Code);

        // A viewer can read the page but can't place a copy.
        await Assert.ThrowsAsync<AtharError>(() => copies.DuplicateAsync(Person(QaSeed.Guest), root, Guid.NewGuid().ToString(), ct: ct));
    }

    [Fact]
    public async Task Restricted_and_confidential_pages_keep_their_protection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        await using var db = athar.Db(QaSeed.Org);
        var copies = await Copier(db);

        // The admin reads the restricted Incident reviews, but a copy of the home page leaves it (and its child) out.
        var home = await copies.DuplicateAsync(Admin, QaSeed.HomePage, Guid.NewGuid().ToString(), ct: ct);
        var copied = await db.Pages.AsNoTracking().Where(p => p.Id == home.PageId || p.AncestorIds.Contains(home.PageId)).Select(p => p.Title).ToListAsync(ct);
        Assert.Contains("Deploy runbook", copied);
        Assert.DoesNotContain("Incident reviews", copied);
        Assert.DoesNotContain("2026-09 disk outage review", copied);
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<AtharError>(() => copies.DuplicateAsync(Admin, QaSeed.IncidentReviewsPage, Guid.NewGuid().ToString(), ct: ct))).Code);

        // Confidential only goes to confidential.
        var ops = await athar.OpsAsync(db: db);
        var secret = (await ops.CreatePageAsync(Admin, QaSeed.EngConfidential, null, "Salaries draft", "Numbers.", Guid.NewGuid().ToString(), ct)).PageId;
        Assert.DoesNotContain(await copies.TargetsAsync(Admin, secret, ct), t => t.SpaceId == QaSeed.EngWiki);
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<AtharError>(() => copies.CopyToSpaceAsync(Admin, secret, QaSeed.EngWiki, null, Guid.NewGuid().ToString(), ct))).Code);
    }

    [Fact]
    public void Comment_anchors_are_dropped_from_a_copy()
    {
        var doc = JsonNode.Parse("""
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"text","text":"kept","marks":[{"type":"bold"},{"type":"commentAnchor","attrs":{"threadId":"t1"}}]},
              {"type":"text","text":"plain","marks":[{"type":"commentAnchor","attrs":{"threadId":"t2"}}]}]}]}
            """);
        PageCopyService.WithoutCommentAnchors(doc);
        var text = doc!.ToJsonString();
        Assert.DoesNotContain("commentAnchor", text);
        Assert.Contains("\"bold\"", text);
        Assert.Null(doc["content"]![0]!["content"]![1]!["marks"]);
    }
}
