using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Athar.Web.Mcp;
using HtmlAgilityPack;

namespace Athar.Web.Services.Import;

/// <summary>
/// One page to import: <see cref="Key"/> is its path in the upload (folder pages end in '/'). <see cref="Files"/> are the
/// page's attachments with their real names (Confluence lists them; files no link uses are attached too), and
/// <see cref="SpaceKey"/> the Confluence space it came from, for a workspace import.
/// </summary>
public sealed record ImportDoc(string Key, string Title, string? ParentKey, string Markdown, IReadOnlyList<ImportFile>? Files = null, string? SpaceKey = null,
    IReadOnlyList<ImportComment>? Comments = null);

/// <summary>A Confluence comment (XML exports, T163): <see cref="ParentId"/> is the comment it replies to.</summary>
public sealed record ImportComment(string Id, string? ParentId, string Author, DateTimeOffset? At, string Markdown);

public sealed record ImportFile(string Path, string Name);

/// <summary>A Confluence space in the upload: a workspace import makes one Athar space of each.</summary>
public sealed record ImportSpace(string Key, string Name);

/// <summary>
/// FR-PG-09: reads an upload into pages, parents before children, siblings in order. Accepts one <c>.md</c> or <c>.html</c>
/// file, or a zip of them: a Docmost (or any) Markdown/HTML export, where <c>Page.md</c> has its subpages in <c>Page/</c>,
/// a Confluence HTML space export, whose <c>index.html</c> lists the page tree under "Available Pages", or a Confluence XML
/// export (<c>entities.xml</c>, a space or the whole site, T161). HTML becomes Markdown. Links stay as written;
/// <see cref="Resolve"/> maps them to pages and files in the upload.
/// </summary>
public sealed partial class WikiImport : IDisposable
{
    /// <summary>T164 (owner, 2026-10-15): a whole Confluence site with its files.</summary>
    public const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 32L * 1024 * 1024 * 1024;
    private const int MaxEntries = 200_000;

    public List<ImportDoc> Docs { get; } = [];
    public List<ImportSpace> Spaces { get; } = [];
    public bool Confluence { get; private set; }
    /// <summary>Pages that aren't files in the zip (Confluence XML): <c>pages/{id}</c>.</summary>
    private readonly HashSet<string> virtualPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZipArchiveEntry> files = new(StringComparer.OrdinalIgnoreCase);
    private ZipArchive? zip;

    /// <summary>
    /// Reads <paramref name="content"/> in place when it can seek (the upload's file); anything else is first copied to a temp
    /// file that's removed when the import is disposed. The import owns the stream.
    /// </summary>
    public static async Task<WikiImport> ReadAsync(string fileName, Stream content, CancellationToken ct)
    {
        var buffer = content;
        if (!content.CanSeek)
        {
            buffer = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, ct)) > 0)
            {
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
                if (buffer.Length > MaxBytes) { await buffer.DisposeAsync(); throw new AtharError("invalid_input", "Imports are limited to 4 GB."); }
            }
        }
        else if (content.Length - content.Position > MaxBytes) throw new AtharError("invalid_input", "Imports are limited to 4 GB.");
        if (buffer.CanSeek && buffer != content) buffer.Position = 0;
        var import = new WikiImport();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".zip") import.ReadZip(buffer);
        else if (IsPage(fileName)) { using var reader = new StreamReader(buffer); import.Docs.Add(import.Page(Path.GetFileName(fileName), reader.ReadToEnd(), null, null)); }
        else throw new AtharError("invalid_input", "Import a .md or .html file, or a .zip of them.");
        if (import.Docs.Count == 0) throw new AtharError("invalid_input", "The upload has no Markdown or HTML pages.");
        return import;
    }

    /// <summary>The page key or file path a link in <paramref name="fromKey"/> points at, or null (external, anchor, missing).</summary>
    public string? Resolve(string fromKey, string url)
    {
        if (url.Length == 0 || url.StartsWith('#') || UriScheme().IsMatch(url)) return null;
        url = Uri.UnescapeDataString(url.Split('#', '?')[0]).Replace('\\', '/');
        // Confluence's absolute download links point into the export's attachments/ folder.
        if (url.StartsWith("/download/attachments/", StringComparison.OrdinalIgnoreCase)) url = url["/download/".Length..];
        var dir = fromKey.EndsWith('/') ? fromKey.TrimEnd('/') : Dir(fromKey);
        foreach (var candidate in new[] { Normalize(Combine(dir, url)), Normalize(url.TrimStart('/')) })
            if (candidate is not null && (files.ContainsKey(candidate) || virtualPages.Contains(candidate))) return candidate;
        return null;
    }

    public bool IsFile(string path) => files.ContainsKey(path);
    public Stream OpenFile(string path) => files[path].Open();

    public void Dispose() => zip?.Dispose();

    private void ReadZip(Stream buffer)
    {
        try { zip = new ZipArchive(buffer, ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new AtharError("invalid_input", "The file isn't a valid zip."); }
        if (zip.Entries.Count > MaxEntries) throw new AtharError("invalid_input", $"The zip has more than {MaxEntries} files.");
        if (zip.Entries.Sum(e => e.Length) > MaxExpandedBytes) throw new AtharError("invalid_input", "The zip expands to more than 32 GB.");
        foreach (var e in zip.Entries)
            if (Normalize(e.FullName) is { } path && !path.EndsWith('/') && !path.StartsWith("__MACOSX/", StringComparison.Ordinal)) files[path] = e;

        var pages = files.Keys.Where(IsPage).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var indexes = pages.Where(p => Path.GetFileName(p).Equals("index.html", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path: p, Html: Text(p))).Where(i => i.Html.Contains("Available Pages", StringComparison.Ordinal)).ToList();
        var entities = files.Keys.Where(k => Path.GetFileName(k).Equals("entities.xml", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Length).FirstOrDefault();
        if (indexes.Count > 0) ReadConfluence(pages, indexes);
        else if (entities is not null) ReadConfluenceXml(entities);
        else ReadFolders(pages);
    }

    /// <summary>Confluence: the tree is the nested list under "Available Pages:" in each space's index.html.</summary>
    private void ReadConfluence(List<string> pages, List<(string Path, string Html)> indexes)
    {
        Confluence = true;
        var tree = new List<(string Key, string? Parent, string Title, string Space)>();
        var keyByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, html) in indexes)
        {
            var doc = Html(html);
            // Each index.html is a space: its folder is the key, its title the name.
            string? Detail(string label) => doc.DocumentNode.SelectNodes("//tr[th]")
                ?.FirstOrDefault(tr => Clean(tr.Element("th")!.InnerText).TrimEnd(':').Equals(label, StringComparison.OrdinalIgnoreCase))
                ?.Element("td") is { } td ? Clean(td.InnerText) : null;
            var spaceKey = Detail("Key") is { Length: > 0 } k ? k : SpaceKeyOf(path);
            var spaceName = Detail("Name") ?? "";
            if (spaceName.Length == 0 && Clean(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "") is { Length: > 0 } t && !t.StartsWith("Space Details", StringComparison.OrdinalIgnoreCase))
                spaceName = t;
            keyByDir[Dir(path)] = spaceKey;
            if (Spaces.All(s => !s.Key.Equals(spaceKey, StringComparison.OrdinalIgnoreCase))) Spaces.Add(new(spaceKey, spaceName.Length > 0 ? spaceName : spaceKey));
            var section = doc.DocumentNode.SelectNodes("//div[contains(@class,'pageSection')]")
                ?.FirstOrDefault(s => s.InnerText.Contains("Available Pages", StringComparison.Ordinal));
            // Confluence often gives each child its own <ul> under the parent's <li> (and the roots their own <ul>s), so every
            // list is walked, not just the first (T165: only the first child of each page kept its place).
            var roots = section?.Elements("ul").ToList() is { Count: > 0 } lists ? lists : section?.SelectSingleNode(".//ul") is { } first ? [first] : [];
            foreach (var list in roots) Walk(list, null);

            void Walk(HtmlNode ul, string? parent)
            {
                foreach (var li in ul.Elements("li"))
                {
                    var a = li.Element("a");
                    var key = a is null ? null : Resolve(path, a.GetAttributeValue("href", ""));
                    if (key is not null && IsPage(key) && seen.Add(key))
                        tree.Add((key, parent, Clean(a!.InnerText), spaceKey));
                    foreach (var sub in li.Elements("ul")) Walk(sub, key ?? parent);
                }
            }
        }
        // Pages the index doesn't list go at the top.
        var listed = tree.Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        tree.AddRange(pages.Where(p => !listed.Contains(p) && !Path.GetFileName(p).Equals("index.html", StringComparison.OrdinalIgnoreCase))
            .Select(p => (p, (string?)null, "", keyByDir.GetValueOrDefault(Dir(p)) ?? Spaces[0].Key)));
        foreach (var (key, parent, title, space) in tree) Docs.Add(Page(key, Text(key), parent, title.Length > 0 ? title : null) with { SpaceKey = space });
    }

    /// <summary>Folders: <c>a/Page.md</c> holds the subpages in <c>a/Page/</c>; a folder with no page of its own becomes one.</summary>
    private void ReadFolders(List<string> pages)
    {
        // One wrapper folder around everything isn't a page.
        var tops = files.Keys.Select(k => k.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var root = tops.Count == 1 && files.Keys.All(k => k.Contains('/')) ? tops[0] + "/" : "";

        // Each page by its node (path without extension); folders without a page get a placeholder ending in '/'.
        var nodes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pages) nodes.TryAdd(p[..^Path.GetExtension(p).Length], p);
        foreach (var node in nodes.Keys.ToList())
            for (var dir = Dir(node); dir.Length > root.TrimEnd('/').Length; dir = Dir(dir)) nodes.TryAdd(dir, dir + "/");
        void Emit(string parentNode, string? parentKey)
        {
            // ponytail: O(n²) over at most MaxPages nodes.
            foreach (var (node, key) in nodes.Where(n => Dir(n.Key).Equals(parentNode, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Docs.Add(key.EndsWith('/') ? new(key, Path.GetFileName(node), parentKey, "") : Page(key, Text(key), parentKey, null));
                Emit(node, key);
            }
        }
        Emit(root.TrimEnd('/'), null);
    }

    /// <summary>A page from Markdown or HTML; the title is <paramref name="title"/>, else the document's own, else the file name.</summary>
    private ImportDoc Page(string key, string text, string? parent, string? title)
    {
        var fallback = StripConfluenceId(Path.GetFileNameWithoutExtension(key));
        if (!Path.GetExtension(key).StartsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            // A leading "# Title" is the page title, not content.
            var m = LeadingHeading().Match(text);
            return m.Success ? new(key, title ?? m.Groups[1].Value.Trim(), parent, text[m.Length..].TrimStart()) : new(key, title ?? fallback, parent, text);
        }
        var doc = Html(text);
        var node = doc.DocumentNode;
        var docTitle = Clean(node.SelectSingleNode("//*[@id='title-text']")?.InnerText ?? node.SelectSingleNode("//title")?.InnerText ?? "");
        // Confluence titles read "Space : Page".
        if (node.SelectSingleNode("//*[@id='breadcrumbs']//a") is { } space && docTitle.StartsWith(Clean(space.InnerText) + " : ", StringComparison.Ordinal))
            docTitle = docTitle[(Clean(space.InnerText).Length + 3)..];
        var body = node.SelectSingleNode("//*[@id='main-content']") ?? node.SelectSingleNode("//body") ?? node;
        // Confluence names files by id (attachments/1002/2002.pdf): the real names are in the page's Attachments list and
        // on each link (data-linked-resource-default-alias). Listed files that no link uses are attached too.
        var found = new List<ImportFile>();
        foreach (var a in node.SelectNodes("//h2[@id='attachments']/ancestor::div[contains(concat(' ', normalize-space(@class), ' '), ' pageSection ')][1]//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            if (Resolve(key, a.GetAttributeValue("href", "")) is { } path && IsFile(path)) found.Add(new(path, Clean(a.InnerText)));
        foreach (var e in body.SelectNodes(".//*[@data-linked-resource-default-alias]") ?? Enumerable.Empty<HtmlNode>())
            if (Resolve(key, e.GetAttributeValue("href", e.GetAttributeValue("src", ""))) is { } path && IsFile(path))
                found.Add(new(path, e.GetAttributeValue("data-linked-resource-default-alias", "")));
        var named = found.Where(f => f.Name.Length > 0).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var h1 = body.SelectSingleNode(".//h1");
        title ??= docTitle.Length > 0 ? docTitle : h1 is not null ? Clean(h1.InnerText) : fallback;
        if (h1 is not null && Clean(h1.InnerText) == title) h1.Remove();
        return new(key, title, parent, HtmlToMarkdown(body), named.Count > 0 ? named : null);
    }

    /// <summary>HTML to the Markdown Athar stores (docs/06 §6.7), with Confluence's macros mapped where Athar has a match.</summary>
    public static string HtmlToMarkdown(HtmlNode body)
    {
        foreach (var n in body.SelectNodes(".//script|.//style|.//*[contains(@class,'page-metadata')]|.//*[contains(@class,'toc-macro')]")?.ToList() ?? [])
            n.Remove();
        foreach (var img in body.SelectNodes(".//img[contains(@class,'emoticon')]")?.ToList() ?? [])
            img.ParentNode.ReplaceChild(body.OwnerDocument.CreateTextNode(WebUtility.HtmlEncode(img.GetAttributeValue("alt", ""))), img);
        // Code macro: the language is in data-syntaxhighlighter-params ("brush: java; gutter: false").
        foreach (var pre in body.SelectNodes(".//pre[@data-syntaxhighlighter-params]")?.ToList() ?? [])
            if (Brush().Match(pre.GetAttributeValue("data-syntaxhighlighter-params", "")) is { Success: true } b)
                pre.SetAttributeValue("class", $"language-{b.Groups[1].Value}");

        // A Markdown table cell is one line: a cell's paragraphs and list items become lines joined with <br> (T162).
        foreach (var cell in body.SelectNodes(".//td|.//th")?.Reverse().ToList() ?? [])
            if (cell.SelectSingleNode("./p|./div|./ul|./ol|./table|./h1|./h2|./h3|./h4|./pre|./blockquote") is not null)
            {
                var lines = new List<string>();
                CellLines(cell, "", lines);
                cell.InnerHtml = string.Join("<br>", lines.Where(l => l.Trim().Length > 0));
            }

        // Info, note, warning and tip panels become callouts, converted on their own and put back after.
        var callouts = new List<string>();
        foreach (var panel in body.SelectNodes(".//div[contains(@class,'confluence-information-macro ')]")?.Reverse().ToList() ?? [])
        {
            var cls = panel.GetAttributeValue("class", "");
            var type = cls.Contains("-note") ? "warning" : cls.Contains("-warning") ? "danger" : cls.Contains("-tip") ? "success" : "info";
            var inner = Converter().Convert((panel.SelectSingleNode(".//*[contains(@class,'confluence-information-macro-body')]") ?? panel).InnerHtml).Trim();
            panel.ParentNode.ReplaceChild(HtmlNode.CreateNode($"<p>ATHARCALLOUT{callouts.Count}</p>"), panel);
            callouts.Add($":::callout{{type={type}}}\n{inner}\n:::");
        }
        var md = Converter().Convert(body.InnerHtml);
        for (var i = callouts.Count - 1; i >= 0; i--) md = md.Replace($"ATHARCALLOUT{i}", "\n" + callouts[i] + "\n");
        return md.Trim();
    }

    /// <summary>A table cell's blocks as lines of inline HTML: paragraphs as they are, list items with "1." or "•", nested deeper.</summary>
    private static void CellLines(HtmlNode node, string indent, List<string> lines)
    {
        var inline = new System.Text.StringBuilder();
        void Flush() { if (inline.ToString().Trim().Length > 0) lines.Add(indent + inline.ToString().Trim()); inline.Clear(); }
        foreach (var child in node.ChildNodes)
        {
            switch (child.Name)
            {
                case "ul" or "ol":
                    Flush();
                    var n = 1;
                    foreach (var li in child.Elements("li"))
                    {
                        var marker = child.Name == "ol" ? $"{n++}. " : "• ";
                        var item = new List<string>();
                        CellLines(li, "", item);
                        for (var i = 0; i < item.Count; i++) lines.Add(indent + (i == 0 ? marker : "\u00a0\u00a0\u00a0") + item[i]);
                    }
                    break;
                case "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "pre" or "blockquote" or "table" or "tbody" or "thead" or "tr" or "td" or "th":
                    Flush();
                    CellLines(child, indent, lines);
                    break;
                case "br":
                    Flush();
                    break;
                default:
                    inline.Append(child.OuterHtml);
                    break;
            }
        }
        Flush();
    }

    private static ReverseMarkdown.Converter Converter() => new(new ReverseMarkdown.Config
    {
        GithubFlavored = true, RemoveComments = true, SmartHrefHandling = true, ListBulletChar = '-',
        UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
        TableWithoutHeaderRowHandling = ReverseMarkdown.Config.TableWithoutHeaderRowHandlingOption.EmptyRow,
    });

    private string Text(string path)
    {
        using var reader = new StreamReader(files[path].Open());
        return reader.ReadToEnd();
    }

    /// <summary>A Confluence HTML export's space key: the folder its pages are in.</summary>
    private static string SpaceKeyOf(string path) => Dir(path) is { Length: > 0 } d ? Path.GetFileName(d) : "space";

    private static HtmlDocument Html(string html) { var d = new HtmlDocument(); d.LoadHtml(html); return d; }
    private static bool IsPage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".html" or ".htm";
    private static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    private static string Combine(string dir, string rel) => rel.StartsWith('/') || dir.Length == 0 ? rel : $"{dir}/{rel}";
    private static string Clean(string s) => Whitespace().Replace(WebUtility.HtmlDecode(s), " ").Trim();
    /// <summary>Confluence file names end in the page id: "Deploy-runbook_123456" → "Deploy-runbook".</summary>
    private static string StripConfluenceId(string name) => ConfluenceId().Replace(name, "");

    /// <summary>A zip path without ".", "..", backslashes or a leading slash; null when it climbs out of the zip.</summary>
    private static string? Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Replace('\\', '/').Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(p);
        }
        return string.Join('/', parts) + (path.EndsWith('/') ? "/" : "");
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")] private static partial Regex UriScheme();
    [GeneratedRegex(@"\A\s*#\s+([^\n]+)\n?")] private static partial Regex LeadingHeading();
    [GeneratedRegex(@"brush:\s*([\w+#-]+)")] private static partial Regex Brush();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"[_-]\d{3,}$")] private static partial Regex ConfluenceId();
}
