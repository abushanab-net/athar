using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Athar.Data;
using Athar.Domain.Model;
using Athar.Web.Mcp;
using Athar.Web.Sharing;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Services.Import;

public sealed record ExportFile(byte[] Bytes, string Mime, string Name);

/// <summary>
/// FR-PG-10 (T157): a page as Markdown or HTML, or a page with its subpages (or a whole space) as a zip that
/// <see cref="WikiImport"/> reads back: <c>Page.md</c> starts with <c># Title</c>, its subpages sit in <c>Page/</c>, files in
/// <c>files/{id}/{name}</c>, and links between exported pages and to files are relative. Only pages the person reads are
/// exported. Content is the stored Markdown projection (FR-PG-04), so edits from the last seconds may not be in it yet.
/// Links to pages that aren't in the export point at the page in Athar (<see cref="Mailer.PublicUrl"/>).
/// </summary>
public sealed partial class WikiExportService(AtharDbContext db, AttachmentStore store, Auth.Mailer mailer)
{
    /// <summary>ponytail: built in memory in the request; a background job if spaces outgrow this.</summary>
    public const int MaxPages = 2000;

    /// <summary>
    /// One page as <c>.md</c> or <c>.html</c>. With attachments it's a zip of the page and its <c>files/</c>, so images and
    /// files work offline (T161); links to other pages go to Athar.
    /// </summary>
    public async Task<ExportFile> PageAsync(Actor actor, Guid pageId, string format, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var page = await db.PagesFor(actor.Caller).AsNoTracking().SingleOrDefaultAsync(p => p.Id == pageId && p.DeletedAt == null, ct)
            ?? throw new AtharError("not_found", "Page not found.");
        var name = FileName(page.Title);
        var files = await db.Attachments.AsNoTracking().Where(a => a.PageId == pageId).ToListAsync(ct);
        var filePaths = files.ToDictionary(a => a.Id, a => $"files/{a.Id:N}/{FileName(a.FileName)}");
        string text, mime, ext;
        if (format == "html")
        {
            var body = ShareHtml.Content(page.ContentJson, s =>
                s.StartsWith("/attachments/") ? Guid.TryParse(s["/attachments/".Length..], out var id) && filePaths.TryGetValue(id, out var to) ? Uri.EscapeDataString(to).Replace("%2F", "/") : null
                : s.StartsWith('/') ? mailer.PublicUrl + s : $"{mailer.PublicUrl}/wiki/p/{s}");
            text = $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(page.Title)}</title></head>\n<body>\n<h1>{WebUtility.HtmlEncode(page.Title)}</h1>\n{body}\n</body></html>\n";
            (mime, ext) = ("text/html", ".html");
        }
        else
        {
            var paths = new Dictionary<Guid, string>();
            text = Links(Markdown(page), name + ".md", paths, filePaths);
            (mime, ext) = ("text/markdown", ".md");
        }
        if (files.Count == 0) return new(Encoding.UTF8.GetBytes(text), mime, name + ext);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var entry = zip.CreateEntry(name + ext).Open()) await entry.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await WriteFilesAsync(zip, files, filePaths, ct);
        }
        return new(ms.ToArray(), "application/zip", name + ".zip");
    }

    /// <summary>The page and its subpages, or the whole space when <paramref name="rootId"/> is null, as a zip.</summary>
    public async Task<ExportFile> ZipAsync(Actor actor, Guid spaceId, Guid? rootId, CancellationToken ct = default)
    {
        db.CurrentOrgId = actor.OrgId;
        var space = await db.SpacesFor(actor.Caller).AsNoTracking().SingleOrDefaultAsync(s => s.Id == spaceId, ct)
            ?? throw new AtharError("not_found", "Space not found.");
        var query = db.PagesFor(actor.Caller).AsNoTracking().Where(p => p.SpaceId == spaceId && p.DeletedAt == null);
        if (rootId is { } r) query = query.Where(p => p.Id == r || p.AncestorIds.Contains(r));
        var pages = await query.ToListAsync(ct);
        if (rootId is not null && pages.All(p => p.Id != rootId)) throw new AtharError("not_found", "Page not found.");
        if (pages.Count > MaxPages) throw new AtharError("too_large", $"{pages.Count} pages; at most {MaxPages} can be exported at once.");

        // Paths: siblings by position, a page's subpages in a folder named like its file. A page whose parent isn't
        // exported (the root, or under a page the person can't read) goes at the top.
        var ids = pages.Select(p => p.Id).ToHashSet();
        var kids = pages.ToLookup(p => p.ParentId is { } parent && ids.Contains(parent) ? parent : (Guid?)null);
        var paths = new Dictionary<Guid, string>();
        void Place(Guid? parent, string dir)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in kids[parent].OrderBy(p => p.Position))
            {
                var name = FileName(p.Title);
                for (var n = 2; !used.Add(name); n++) name = $"{FileName(p.Title)} ({n})";
                paths[p.Id] = $"{dir}{name}.md";
                Place(p.Id, $"{dir}{name}/");
            }
        }
        Place(null, "");

        var files = await db.Attachments.AsNoTracking().Where(a => ids.Contains(a.PageId)).ToListAsync(ct);
        var filePaths = files.ToDictionary(a => a.Id, a => $"files/{a.Id:N}/{FileName(a.FileName)}");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var p in pages)
            {
                var path = paths[p.Id];
                await using var entry = zip.CreateEntry(path).Open();
                await entry.WriteAsync(Encoding.UTF8.GetBytes(Links(Markdown(p), path, paths, filePaths)), ct);
            }
            await WriteFilesAsync(zip, files, filePaths, ct);
        }
        var root = rootId is { } id2 ? pages.Single(p => p.Id == id2).Title : space.Name;
        return new(ms.ToArray(), "application/zip", FileName(root) + ".zip");
    }

    /// <summary>
    /// Links in the file at <paramref name="path"/>: files and exported pages become relative paths, other pages Athar URLs.
    /// </summary>
    private string Links(string md, string path, Dictionary<Guid, string> paths, Dictionary<Guid, string> filePaths)
    {
        md = AttachmentLink().Replace(md, m => Guid.TryParse(m.Groups[1].Value, out var id) && filePaths.TryGetValue(id, out var to) ? $"]({Relative(path, to)})" : m.Value);
        return PageLink().Replace(md, m => Guid.TryParse(m.Groups["id"].Value, out var id)
            ? $"[{m.Groups["text"].Value}]({(paths.TryGetValue(id, out var to) ? Relative(path, to) : $"{mailer.PublicUrl}/wiki/p/{id}")})"
            : m.Value);
    }

    private async Task WriteFilesAsync(ZipArchive zip, List<Attachment> files, Dictionary<Guid, string> filePaths, CancellationToken ct)
    {
        foreach (var a in files)
        {
            await using var entry = zip.CreateEntry(filePaths[a.Id], CompressionLevel.Fastest).Open();
            await using var file = store.Open(a.StorageKey);
            await file.CopyToAsync(entry, ct);
        }
    }

    private static string Markdown(Page p) => $"# {p.Title}\n\n{p.ContentMd}".TrimEnd() + "\n";

    /// <summary>A link from the file at <paramref name="from"/> to <paramref name="to"/>, both zip paths, with spaces escaped.</summary>
    private static string Relative(string from, string to)
    {
        var a = from.Split('/')[..^1];
        var b = to.Split('/');
        var common = 0;
        while (common < a.Length && common < b.Length - 1 && a[common] == b[common]) common++;
        var rel = string.Concat(Enumerable.Repeat("../", a.Length - common)) + string.Join('/', b[common..]);
        return Uri.EscapeDataString(rel).Replace("%2F", "/");
    }

    /// <summary>A file name from a title: no path or reserved characters, at most 100 characters.</summary>
    private static string FileName(string title)
    {
        var s = Reserved().Replace(title, "_").Trim().TrimEnd('.');
        return s.Length == 0 ? "page" : s.Length > 100 ? s[..100] : s;
    }

    [GeneratedRegex(@"\]\(/attachments/([0-9a-fA-F-]{36})\)")] private static partial Regex AttachmentLink();
    /// <summary>A wiki link <c>[text](/wiki/p/id)</c> or a mention <c>[[Title]](page:id)</c>; both become <c>[text](path)</c>.</summary>
    [GeneratedRegex(@"\[?\[(?<text>[^\[\]]*)\]\]?\((?:page:|/wiki/p/)(?<id>[0-9a-fA-F-]{36})\)")] private static partial Regex PageLink();
    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]")] private static partial Regex Reserved();
}

/// <summary>The wiki's download, with the person's cookie (not part of /api/v1): <c>/wiki/export?page=&amp;format=md|html|zip</c> or <c>?space=</c>.</summary>
public static class WikiExportEndpoints
{
    public static void MapWikiExport(this WebApplication app) =>
        app.MapGet("/wiki/export", async (HttpContext http, WikiExportService export, AtharDbContext db, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            var actor = Actor.ForUser(http.User);
            try
            {
                ExportFile file;
                if (Guid.TryParse(q["space"], out var spaceId)) file = await export.ZipAsync(actor, spaceId, null, ct);
                else if (!Guid.TryParse(q["page"], out var pageId)) return Results.Problem(title: "invalid_input", statusCode: StatusCodes.Status400BadRequest);
                else if (q["format"] == "zip")
                {
                    db.CurrentOrgId = actor.OrgId;
                    var spaceOf = await db.PagesFor(actor.Caller).Where(p => p.Id == pageId).Select(p => (Guid?)p.SpaceId).SingleOrDefaultAsync(ct)
                        ?? throw new AtharError("not_found", "Page not found.");
                    file = await export.ZipAsync(actor, spaceOf, pageId, ct);
                }
                else file = await export.PageAsync(actor, pageId, q["format"] == "html" ? "html" : "md", ct);
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(file.Bytes, file.Mime, file.Name);
            }
            // With a body, so status-code pages don't re-execute into the Blazor not-found page.
            catch (AtharError e) when (e.Code is "not_found" or "forbidden") { return Results.Problem(title: "not_found", statusCode: StatusCodes.Status404NotFound); }
            catch (AtharError e) { return Results.Problem(title: e.Code, detail: e.Message, statusCode: StatusCodes.Status400BadRequest); }
        }).RequireAuthorization().ExcludeFromDescription();
}
