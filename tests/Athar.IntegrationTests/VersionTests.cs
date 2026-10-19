using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>T045 versions (FR-PG-05): automatic and manual snapshots, list, preview, restore with a restore version.</summary>
[Collection(AtharCollection.Name)]
public class VersionTests(AtharApp athar)
{
    private static readonly HashSet<string> Scopes = ["read", "write"];
    private static Actor Person(Guid user) => new(user, QaSeed.Org, null, Scopes, DisclosureClass.TrustedLocal, null);
    private static readonly Actor Editor = Person(QaSeed.Member);
    private static readonly Actor Viewer = Person(QaSeed.Guest);

    private async Task<VersionService> Versions()
    {
        var db = athar.Db(QaSeed.Org);
        return new VersionService(db, await athar.OpsAsync(db: db));
    }

    [Fact]
    public async Task Versions_list_preview_snapshot_and_restore()
    {
        var ct = AtharApp.Timeout();
        var ops = await athar.OpsAsync();
        var page = (await ops.CreatePageAsync(Editor, QaSeed.EngWiki, null, "Versions", "First text", Guid.NewGuid().ToString(), ct)).PageId;

        // The first persist made an automatic version.
        var list = await (await Versions()).ListAsync(Editor, page, ct);
        var auto = Assert.Single(list);
        Assert.Equal("auto", auto.Reason);
        Assert.Equal("First text", (await (await Versions()).PreviewAsync(Viewer, page, auto.Id, ct)).Markdown);

        // A manual snapshot of the current (newer) content, audited.
        await (await athar.OpsAsync()).RunAsync(Editor, page, Guid.NewGuid().ToString(), new PageOp("append", "Second text"), ct);
        var manual = await (await Versions()).SnapshotAsync(Editor, page, ct);
        var snap = await (await Versions()).PreviewAsync(Editor, page, manual, ct);
        Assert.Equal("manual", snap.Reason);
        Assert.Contains("Second text", snap.Markdown);

        // Restore the automatic version: next revision, old content, a restore version once even if retried.
        var op = Guid.NewGuid().ToString();
        var restored = await (await Versions()).RestoreAsync(Editor, page, auto.Id, op, ct);
        var again = await (await Versions()).RestoreAsync(Editor, page, auto.Id, op, ct);
        Assert.Equal(restored.Revision, again.Revision);
        Assert.Equal("First text", (await (await athar.OpsAsync()).GetPageAsync(Editor, page, ct: ct)).Content);
        list = await (await Versions()).ListAsync(Editor, page, ct);
        Assert.Single(list, v => v.Reason == "restore" && v.Revision == restored.Revision);
        Assert.Equal(list.Max(v => v.Revision), restored.Revision);

        await using var db = athar.Db(QaSeed.Org);
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "page.snapshot" && a.ResourceId == page.ToString(), ct));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "page.restore" && a.ResourceId == page.ToString(), ct));
    }

    [Fact]
    public async Task A_viewer_sees_versions_but_cannot_snapshot_or_restore()
    {
        var ct = AtharApp.Timeout();
        var page = (await (await athar.OpsAsync()).CreatePageAsync(Editor, QaSeed.EngWiki, null, "Versions viewer", "Text", Guid.NewGuid().ToString(), ct)).PageId;
        var version = Assert.Single(await (await Versions()).ListAsync(Viewer, page, ct));

        var e = await Assert.ThrowsAsync<AtharError>(async () => await (await Versions()).SnapshotAsync(Viewer, page, ct));
        Assert.Equal("forbidden", e.Code);
        e = await Assert.ThrowsAsync<AtharError>(async () => await (await Versions()).RestoreAsync(Viewer, page, version.Id, Guid.NewGuid().ToString(), ct));
        Assert.Equal("forbidden", e.Code);
    }
}
