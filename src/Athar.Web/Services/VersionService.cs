using Athar.Data;
using Athar.Domain.Access;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services;

public sealed record VersionRow(Guid Id, long Revision, string Reason, string? By, DateTimeOffset CreatedAt);

public sealed record VersionPreview(Guid Id, long Revision, string Reason, string Markdown, DateTimeOffset CreatedAt);

/// <summary>
/// Version history (FR-PG-05): automatic snapshots (one at most every 5 minutes, written by the persist), manual snapshots,
/// list, preview and restore. Restore goes through the live-document ops path and makes current_revision + 1.
/// </summary>
public sealed class VersionService(AtharDbContext db, PageOpsService ops)
{
    /// <summary>
    /// The page's versions, newest first. <paramref name="limit"/> caps the rows read: a busy page gains an automatic
    /// version every 5 minutes (T103).
    /// </summary>
    public async Task<List<VersionRow>> ListAsync(Actor actor, Guid pageId, CancellationToken ct = default, int? limit = null)
    {
        await RequireAsync(actor, pageId, PageAction.Read, ct);
        var query = db.PageVersions.AsNoTracking().Where(v => v.PageId == pageId).OrderByDescending(v => v.Revision).ThenByDescending(v => v.CreatedAt);
        var rows = await (limit is { } n ? query.Take(n) : query).ToListAsync(ct);
        var people = rows.Select(v => v.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. rows.Select(v => new VersionRow(v.Id, v.Revision, Reason(v.Reason), v.CreatedBy is { } by ? names.GetValueOrDefault(by) : null, v.CreatedAt))];
    }

    public async Task<VersionPreview> PreviewAsync(Actor actor, Guid pageId, Guid versionId, CancellationToken ct = default)
    {
        await RequireAsync(actor, pageId, PageAction.Read, ct);
        var v = await db.PageVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId && x.PageId == pageId, ct)
            ?? throw new AtharError("not_found", "Version not found.");
        return new(v.Id, v.Revision, Reason(v.Reason), v.ContentMd ?? "", v.CreatedAt);
    }

    /// <summary>A manual snapshot of the current content, live from collab when the page is open (FR-PG-05).</summary>
    public async Task<Guid> SnapshotAsync(Actor actor, Guid pageId, CancellationToken ct = default)
    {
        await RequireAsync(actor, pageId, PageAction.Edit, ct);
        var md = await ops.GetPageAsync(actor, pageId, "markdown", ct);
        var json = await ops.GetPageAsync(actor, pageId, "json", ct);
        var version = new PageVersion
        {
            PageId = pageId, Revision = md.Revision, ContentMd = md.Content, ContentJson = json.Content, Reason = VersionReason.Manual, CreatedBy = actor.UserId,
        };
        db.PageVersions.Add(version);
        db.Audit(actor.OrgId, actor.UserId, "page.snapshot", "page", pageId, after: new { version_id = version.Id, revision = md.Revision }, credentialId: actor.CredentialId);
        await db.SaveChangesAsync(ct);
        return version.Id;
    }

    /// <summary>Restores a version through the ops path, and records the restored content as a "restore" version.</summary>
    public async Task<PageOpResult> RestoreAsync(Actor actor, Guid pageId, Guid versionId, string opId, CancellationToken ct = default)
    {
        var result = await ops.RestoreAsync(actor, pageId, versionId, opId, ct);
        db.CurrentOrgId = actor.OrgId;
        // A retry of the same op returns the stored result; record the restore version once.
        if (!await db.PageVersions.AnyAsync(v => v.PageId == pageId && v.Revision == result.Revision && v.Reason == VersionReason.Restore, ct))
        {
            var source = await db.PageVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, ct);
            db.PageVersions.Add(new PageVersion
            {
                PageId = pageId, Revision = result.Revision, ContentMd = source.ContentMd, ContentJson = source.ContentJson,
                Reason = VersionReason.Restore, CreatedBy = actor.UserId,
            });
            await db.SaveChangesAsync(ct);
        }
        return result;
    }

    private async Task RequireAsync(Actor actor, Guid pageId, PageAction action, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var caller = await db.ActorAsync(actor.UserId, actor.CredentialId, ct) ?? throw new AtharError("unauthorized", "The user is inactive or the credential is revoked.");
        if (await db.CanAsync(caller, pageId, action, ct)) return;
        throw action != PageAction.Read && await db.CanAsync(caller, pageId, PageAction.Read, ct)
            ? new AtharError("forbidden", "You can't change this page.")
            : new AtharError("not_found", "Page not found.");
    }

    private static string Reason(VersionReason r) => r.ToString().ToLowerInvariant();
}
