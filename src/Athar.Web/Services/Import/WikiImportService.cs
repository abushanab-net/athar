using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Athar.Data;
using Athar.Domain.Access;
using Athar.Web.Auth;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services.Import;

/// <param name="SpaceId">The space the first page is in (the new space, for a workspace import).</param>
public sealed record ImportResult(Guid FirstPageId, int Pages, int Files, List<string> Skipped, Guid SpaceId, int Spaces = 1);

/// <summary>
/// FR-PG-09 (T155, T161): imports a <see cref="WikiImport"/> under a page or at the top of a space (space managers:
/// space admins and workspace or org admins), or into new spaces of a workspace, one per Confluence space (workspace
/// managers). Pages are made parents first, published, with ids derived from the op id, so a retry with the same op id
/// carries on instead of duplicating. Each page's files are uploaded through <see cref="AttachmentService"/> (its type
/// rules apply; refused files are reported, not fatal), then its Markdown is written with links to pages of the import as
/// mentions or wiki links, and to files as attachments; files the page lists but no link uses go in an Attachments list.
/// Content goes through <see cref="PageOpsService"/>, so the save queues the page's embedding like any edit. People only,
/// from the wiki.
/// </summary>
public sealed partial class WikiImportService(AtharDbContext db, PageOpsService ops, PageService pages, AttachmentService attachments, WorkspaceAdminService admin)
{
    /// <summary>T164: big imports run as a background job (<see cref="WikiImportRunner"/>), with progress.</summary>
    public const int MaxPages = 20_000;

    /// <summary>Into an existing space, at the top or under <paramref name="parentId"/>. Space managers only (owner decision 2026-10-15).</summary>
    public async Task<ImportResult> ImportAsync(Actor actor, Guid spaceId, Guid? parentId, string fileName, Stream content, string opId, CancellationToken ct = default,
        Func<int, int, Task>? progress = null)
    {
        Check(actor, opId);
        if (!await admin.CanManageSpaceAsync(actor.OrgId, actor.UserId, spaceId, ct))
            throw new AtharError("forbidden", "Only the space's admins can import pages.");
        using var import = await ReadAsync(fileName, content, ct);
        var (first, uploaded, skipped) = await ImportDocsAsync(actor, import, import.Docs, _ => spaceId, parentId, opId, progress, ct);
        Audit(actor, first, new { space_id = spaceId, parent_id = parentId, file_name = fileName, confluence = import.Confluence, pages = import.Docs.Count, files = uploaded, skipped = skipped.Count });
        await db.SaveChangesAsync(ct);
        return new(first, import.Docs.Count, uploaded, skipped, spaceId);
    }

    /// <summary>
    /// A whole Confluence export (several spaces, or the site) into <paramref name="workspaceId"/>: each Confluence space
    /// becomes a space named like it, with its key as the slug, and the importer as its admin. A space whose slug already
    /// exists there is imported into, if the importer manages it (so a retry or a re-import lands in the same space). Any
    /// other upload becomes one space named after the file. Workspace managers only.
    /// </summary>
    public async Task<ImportResult> ImportSpacesAsync(Actor actor, Guid workspaceId, string fileName, Stream content, string opId, CancellationToken ct = default,
        Func<int, int, Task>? progress = null)
    {
        Check(actor, opId);
        db.CurrentOrgId = actor.OrgId;
        if (!(await admin.WorkspacesAsync(actor.OrgId, actor.UserId, ct)).Any(w => w.Id == workspaceId && w.CanManage))
            throw new AtharError("forbidden", "Only the workspace's admins can import spaces.");
        using var import = await ReadAsync(fileName, content, ct);
        var groups = import.Spaces.Count > 0
            ? import.Spaces.Select(s => (s.Key, s.Name)).ToList()
            : [(Path.GetFileNameWithoutExtension(fileName), Path.GetFileNameWithoutExtension(fileName))];

        var spaceIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in groups)
        {
            var slug = Slug(key);
            var existing = await db.Spaces.AsNoTracking().Where(s => s.WorkspaceId == workspaceId && s.Slug == slug).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct);
            if (existing is { } id)
            {
                if (!await admin.CanManageSpaceAsync(actor.OrgId, actor.UserId, id, ct))
                    throw new AtharError("forbidden", $"A space with the slug \"{slug}\" exists and you don't manage it.");
                spaceIds[key] = id;
                continue;
            }
            var made = await admin.CreateSpaceAsync(actor.OrgId, actor.UserId, workspaceId, name, slug, holdsPersonalData: false, ct);
            if (made == OrgDecision.Forbidden) throw new AtharError("forbidden", "Only the workspace's admins can import spaces.");
            if (made != OrgDecision.Allow) throw new AtharError("invalid_input", $"The space \"{name}\" ({slug}) can't be created.");
            spaceIds[key] = await db.Spaces.AsNoTracking().Where(s => s.WorkspaceId == workspaceId && s.Slug == slug).Select(s => s.Id).SingleAsync(ct);
        }

        var fallback = spaceIds[groups[0].Key];
        var (first, uploaded, skipped) = await ImportDocsAsync(actor, import, import.Docs,
            d => d.SpaceKey is { } k && spaceIds.TryGetValue(k, out var id) ? id : fallback, null, opId, progress, ct);
        Audit(actor, first, new { workspace_id = workspaceId, spaces = spaceIds.Values, file_name = fileName, confluence = import.Confluence, pages = import.Docs.Count, files = uploaded, skipped = skipped.Count });
        await db.SaveChangesAsync(ct);
        var firstSpace = import.Docs[0].SpaceKey is { } fk && spaceIds.TryGetValue(fk, out var s0) ? s0 : fallback;
        return new(first, import.Docs.Count, uploaded, skipped, firstSpace, spaceIds.Count);
    }

    private static void Check(Actor actor, string opId)
    {
        if (string.IsNullOrWhiteSpace(opId) || opId.Length > 120) throw new AtharError("invalid_input", "op_id is required (at most 120 characters).");
        if (actor.Caller.Agent) throw new AtharError("forbidden", "Importing pages is a wiki action.");
    }

    private async Task<WikiImport> ReadAsync(string fileName, Stream content, CancellationToken ct)
    {
        var import = await WikiImport.ReadAsync(fileName, content, ct);
        if (import.Docs.Count > MaxPages)
        {
            import.Dispose();
            throw new AtharError("too_large", $"The upload has {import.Docs.Count} pages; at most {MaxPages} can be imported at once.");
        }
        return import;
    }

    private void Audit(Actor actor, Guid first, object after) => db.Audit(actor.OrgId, actor.UserId, "page.import", "page", first.ToString(), after: after);

    private async Task<(Guid First, int Uploaded, List<string> Skipped)> ImportDocsAsync(
        Actor actor, WikiImport import, List<ImportDoc> docs, Func<ImportDoc, Guid> spaceOf, Guid? parentId, string opId, Func<int, int, Task>? progress, CancellationToken ct)
    {
        db.CurrentOrgId = actor.OrgId;
        var ids = docs.ToDictionary(d => d.Key, d => NewId(opId, d.Key), StringComparer.OrdinalIgnoreCase);
        var titles = docs.ToDictionary(d => d.Key, d => d.Title, StringComparer.OrdinalIgnoreCase);
        var wiki = new WikiActor(actor.UserId, actor.OrgId);
        foreach (var d in docs)
            if (!await db.Pages.AnyAsync(p => p.Id == ids[d.Key], ct))
                await pages.CreateAsync(wiki, spaceOf(d), d.ParentKey is null ? parentId : ids[d.ParentKey], d.Title, ct, id: ids[d.Key]);

        var skipped = new List<string>();
        var uploaded = 0;
        var done = 0;
        foreach (var d in docs)
        {
            if (progress is not null && done++ % 10 == 0) await progress(done - 1, docs.Count);
            var id = ids[d.Key];
            var pageOp = $"{opId}:{id:N}";
            // Written on an earlier run of this import. ponytail: a crash between a page's uploads and its write re-uploads them.
            if (await db.Operations.AnyAsync(o => o.OpId == pageOp, ct)) continue;
            var names = (d.Files ?? []).ToDictionary(f => f.Path, f => f.Name, StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
            async Task<Guid?> Upload(string path)
            {
                if (files.TryGetValue(path, out var known)) return known;
                try
                {
                    await using var stream = import.OpenFile(path);
                    var name = names.GetValueOrDefault(path) ?? Path.GetFileName(path);
                    files[path] = (await attachments.UploadAsync(actor, id, name, null, stream, ct, maxBytes: long.MaxValue)).AttachmentId;
                    uploaded++;
                }
                catch (AtharError e) when (e.Code == "invalid_input")
                {
                    files[path] = null;
                    skipped.Add($"{names.GetValueOrDefault(path) ?? path}: {e.Message}");
                }
                return files[path];
            }

            var md = await ReplaceAsync(Link(), d.Markdown, async m =>
            {
                var (bang, text, url) = (m.Groups["bang"].Value, m.Groups["text"].Value, m.Groups["url"].Value);
                var target = import.Resolve(d.Key, url.Trim('<', '>'));
                if (target is null) return m.Value;
                if (ids.TryGetValue(target, out var page))
                    return bang.Length > 0 ? text : text == titles[target] ? $"[[{text}]](page:{page})" : $"[{text}](/wiki/p/{page})";
                if (!import.IsFile(target)) return m.Value;
                return await Upload(target) is { } file ? $"{bang}[{text}](/attachments/{file})" : text;
            });

            // Files the page lists (Confluence's Attachments) that no link used.
            var extra = new List<string>();
            foreach (var f in d.Files ?? [])
                if (!files.ContainsKey(f.Path) && await Upload(f.Path) is { } file) extra.Add($"- [{f.Name}](/attachments/{file})");
            if (extra.Count > 0) md = $"{md.TrimEnd()}\n\n## Attachments\n\n{string.Join('\n', extra)}\n";

            if (md.Trim().Length > 0) await ops.RunAsync(actor, id, pageOp, new PageOp("append", md), ct);
        }

        // T163: Confluence comments as page-level threads, one per top-level comment with its replies in date order. They're
        // written by the importer (Confluence's people aren't Athar users), each saying who wrote it and when, keep their
        // dates, and notify no one. A thread's op id makes a retry skip it.
        foreach (var d in docs.Where(d => d.Comments is { Count: > 0 }))
        {
            var byId = d.Comments!.ToDictionary(c => c.Id);
            string Root(ImportComment c) { for (var i = 0; i < 100 && c.ParentId is { } p && byId.TryGetValue(p, out var up); i++) c = up; return c.Id; }
            foreach (var thread in d.Comments!.GroupBy(Root))
            {
                var threadOp = $"{opId}:c:{thread.Key}";
                if (await db.CommentThreads.AnyAsync(t => t.OpId == threadOp, ct)) continue;
                var ordered = thread.OrderBy(c => c.At ?? DateTimeOffset.MaxValue).ToList();
                var t = new Athar.Domain.Model.CommentThread { PageId = ids[d.Key], Status = Athar.Domain.Model.ThreadStatus.Open, OpId = threadOp };
                if (ordered[0].At is { } first) t.CreatedAt = first;
                db.CommentThreads.Add(t);
                foreach (var c in ordered)
                {
                    var body = PageLinks(c.Markdown, import, d.Key, ids, titles);
                    var comment = new Athar.Domain.Model.Comment
                    {
                        ThreadId = t.Id, AuthorId = actor.UserId,
                        BodyMd = $"*{(c.At is { } at ? $"Originally by {c.Author}, {at:d MMM yyyy}" : $"Originally by {c.Author}")}:*\n\n{body}",
                    };
                    if (c.At is { } when) comment.CreatedAt = when;
                    db.Comments.Add(comment);
                }
                db.Audit(actor.OrgId, actor.UserId, "comment.import", "comment_thread", t.Id, after: new { page_id = ids[d.Key], comments = ordered.Count });
                await db.SaveChangesAsync(ct);
            }
        }
        return (ids[docs[0].Key], uploaded, skipped);
    }

    /// <summary>Links in a comment to pages of the import, as mentions or wiki links (files stay with the page).</summary>
    private static string PageLinks(string md, WikiImport import, string fromKey, Dictionary<string, Guid> ids, Dictionary<string, string> titles) =>
        Link().Replace(md, m => import.Resolve(fromKey, m.Groups["url"].Value.Trim('<', '>')) is { } target && ids.TryGetValue(target, out var page)
            ? m.Groups["text"].Value == titles[target] ? $"[[{titles[target]}]](page:{page})" : $"[{m.Groups["text"].Value}](/wiki/p/{page})"
            : m.Value);

    /// <summary>A space slug from a Confluence key or file name: lowercase letters, digits and dashes.</summary>
    private static string Slug(string key)
    {
        var s = SlugChars().Replace(key.ToLowerInvariant(), "-").Trim('-');
        return s.Length == 0 ? "imported" : s.Length > 60 ? s[..60] : s;
    }

    private static async Task<string> ReplaceAsync(Regex regex, string input, Func<Match, Task<string>> replace)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in regex.Matches(input))
        {
            sb.Append(input, last, m.Index - last).Append(await replace(m));
            last = m.Index + m.Length;
        }
        return sb.Append(input, last, input.Length - last).ToString();
    }

    /// <summary>A stable id for (op, path in the upload): a retry with the same op id finds the pages it already made.</summary>
    private static Guid NewId(string opId, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{opId}\n{key.ToLowerInvariant()}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // version 8 (custom)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>A Markdown link or image: <c>[text](url "title")</c>, one level of brackets in the text (an image inside a link).</summary>
    [GeneratedRegex("""(?<bang>!?)\[(?<text>(?:[^\[\]]|\[[^\]]*\])*)\]\((?<url><[^>]+>|[^)\s]+)(?:\s+"[^"]*")?\)""")]
    private static partial Regex Link();
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex SlugChars();
}
