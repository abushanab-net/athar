using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Athar.Web.Services.Import;

/// <summary>
/// T161: Confluence XML exports (a space, or the whole site). <c>entities.xml</c> holds Hibernate objects: <c>Space</c>
/// (key, name), <c>Page</c> (title, parent, position, space), <c>BodyContent</c> (the page's storage-format XHTML) and
/// <c>Attachment</c> (title, page, version), whose bytes are at <c>attachments/{page}/{attachment}/{version}</c>, and
/// <c>Comment</c> (page, parent, creator, date; T163) with <c>ConfluenceUserImpl</c> for the creator's username. Only
/// current versions are read (no history, drafts or trash) and blog posts are skipped. Storage format is turned into the
/// same HTML a Confluence HTML export has, so <see cref="HtmlToMarkdown"/> maps code, panels and links the same way.
/// </summary>
public sealed partial class WikiImport
{
    private sealed class XmlPage
    {
        public required string Id;
        public string Title = "";
        public string? Parent, Space;
        public int Position = int.MaxValue;
        public string Body = "";
        public List<ImportFile> Files = [];
        public List<(string Id, string? Parent, string? Creator, string? Date)> Comments = [];
    }

    private void ReadConfluenceXml(string entities)
    {
        Confluence = true;
        var root = Dir(entities);
        var pages = new Dictionary<string, XmlPage>();
        var bodies = new Dictionary<string, string>();   // content id → storage XHTML
        var spaces = new Dictionary<string, ImportSpace>();
        var attachments = new List<(string Id, string Title, string Page, string Version)>();
        var comments = new List<(string Id, string Page, string? Parent, string? Creator, string? Date)>();
        var users = new Dictionary<string, string>();   // user key → username

        try
        {
        using (var reader = XmlReader.Create(files[entities].Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true }))
        {
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "object") { reader.Read(); continue; }
                var o = (XElement)XNode.ReadFrom(reader);
                var id = o.Element("id")?.Value;
                if (id is null) continue;
                // Old versions point at their current version; drafts and trashed content aren't "current".
                var current = Ref(o, "originalVersion") is null && (Prop(o, "contentStatus")?.Value ?? "current") == "current";
                switch ((string?)o.Attribute("class"))
                {
                    case "Space":
                        spaces[id] = new(Prop(o, "key")?.Value ?? id, Prop(o, "name")?.Value ?? id);
                        break;
                    case "Page" when current:
                        pages[id] = new XmlPage
                        {
                            Id = id, Title = Prop(o, "title")?.Value ?? "", Parent = Ref(o, "parent"), Space = Ref(o, "space"),
                            Position = int.TryParse(Prop(o, "position")?.Value, out var pos) ? pos : int.MaxValue,
                        };
                        break;
                    case "BodyContent" when Ref(o, "content") is { } content:
                        bodies[content] = Prop(o, "body")?.Value ?? "";
                        break;
                    case "Attachment" when current && (Ref(o, "containerContent") ?? Ref(o, "content")) is { } page:
                        attachments.Add((id, Prop(o, "title")?.Value ?? id, page, Prop(o, "version")?.Value ?? "1"));
                        break;
                    case "Comment" when current && (Ref(o, "containerContent") ?? Ref(o, "content")) is { } container:
                        comments.Add((id, container, Ref(o, "parent"), Ref(o, "creator") ?? Prop(o, "creatorName")?.Value, Prop(o, "creationDate")?.Value));
                        break;
                    case "ConfluenceUserImpl":
                        users[id] = Prop(o, "name")?.Value ?? id;
                        break;
                }
            }
        }
        }
        catch (XmlException e) { throw new AtharError("invalid_input", $"The Confluence export's entities.xml is damaged: {e.Message}"); }
        if (pages.Count == 0) throw new AtharError("invalid_input", "The Confluence export has no pages.");

        foreach (var (id, title, pageId, version) in attachments)
        {
            if (!pages.TryGetValue(pageId, out var page)) continue;
            // The newest stored version of the file (exports usually hold only the current one).
            var dir = Combine(root, $"attachments/{pageId}/{id}");
            var path = files.ContainsKey($"{dir}/{version}") ? $"{dir}/{version}"
                : files.Keys.Where(k => k.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k.Length).ThenByDescending(k => k).FirstOrDefault();
            if (path is not null) page.Files.Add(new(path, title));
        }
        foreach (var (id, body) in bodies) if (pages.TryGetValue(id, out var page)) page.Body = body;
        foreach (var c in comments)
            if (pages.TryGetValue(c.Page, out var page)) page.Comments.Add((c.Id, c.Parent, c.Creator is { } k ? users.GetValueOrDefault(k, k) : null, c.Date));
        foreach (var p in pages.Values) virtualPages.Add(Key(p.Id));
        var byTitle = pages.Values.GroupBy(p => (p.Space, p.Title)).ToDictionary(g => g.Key, g => g.First().Id);

        foreach (var s in pages.Values.Select(p => p.Space).Distinct())
            Spaces.Add(s is not null && spaces.TryGetValue(s, out var sp) ? sp : new(s ?? "space", s ?? "Confluence"));

        // Parents first, siblings by position then title; a page whose parent isn't current goes at the top.
        var kids = pages.Values.ToLookup(p => p.Parent is { } parent && pages.ContainsKey(parent) ? parent : null);
        void Emit(string? parent)
        {
            foreach (var p in kids[parent].OrderBy(p => p.Position).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase))
            {
                string? PageByTitle(string title) => byTitle.GetValueOrDefault((p.Space, title));
                var html = StorageToHtml(p.Body, p.Files, PageByTitle);
                // Comments (T163): each one's body converted like the page's; replies keep their parent.
                var notes = p.Comments.Select(c => new ImportComment(c.Id, c.Parent, c.Creator ?? "unknown",
                        DateTimeOffset.TryParse(c.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null,
                        HtmlToMarkdown(Html($"<div>{StorageToHtml(bodies.GetValueOrDefault(c.Id, ""), p.Files, PageByTitle)}</div>").DocumentNode)))
                    .Where(c => c.Markdown.Length > 0).ToList();
                var spaceKey = p.Space is not null && spaces.TryGetValue(p.Space, out var sp) ? sp.Key : p.Space ?? "space";
                Docs.Add(new(Key(p.Id), p.Title.Length > 0 ? p.Title : $"Page {p.Id}", parent is null ? null : Key(parent),
                    HtmlToMarkdown(Html($"<div>{html}</div>").DocumentNode), p.Files.Count > 0 ? p.Files : null, spaceKey, notes.Count > 0 ? notes : null));
                Emit(p.Id);
            }
        }
        Emit(null);

        static XElement? Prop(XElement o, string name) => o.Elements("property").FirstOrDefault(p => (string?)p.Attribute("name") == name);
        static string? Ref(XElement o, string name) => Prop(o, name)?.Element("id")?.Value;
    }

    private static string Key(string pageId) => $"pages/{pageId}";

    /// <summary>
    /// Confluence storage format (XHTML with <c>ac:</c> macros and <c>ri:</c> resources) to plain HTML: code, panels,
    /// expand and layout bodies, links to pages and files, images and task lists. Macros Athar has no match for keep
    /// their body, or go (table of contents, children, page tree).
    /// </summary>
    private string StorageToHtml(string body, List<ImportFile> pageFiles, Func<string, string?> pageByTitle)
    {
        // CDATA (code bodies, link text) as escaped text, so the HTML parser keeps it.
        var xhtml = Cdata().Replace(body, m => WebUtility.HtmlEncode(m.Groups[1].Value));
        var doc = Html($"<div>{xhtml}</div>");
        var files = pageFiles.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);
        static HtmlNode? Child(HtmlNode n, string name) => n.ChildNodes.FirstOrDefault(c => c.Name == name);
        static string Param(HtmlNode n, string name) =>
            n.ChildNodes.FirstOrDefault(c => c.Name == "ac:parameter" && c.GetAttributeValue("ac:name", "") == name)?.InnerText.Trim() ?? "";
        void Replace(HtmlNode n, string html, bool block = false) => n.ParentNode.ReplaceChild(HtmlNode.CreateNode(block ? $"<div>{html}</div>" : $"<span>{html}</span>"), n);

        // Innermost first, so an outer macro's body already holds the converted inner one.
        foreach (var n in doc.DocumentNode.Descendants().Where(n => n.Name is "ac:structured-macro" or "ac:macro").Reverse().ToList())
        {
            var rich = Child(n, "ac:rich-text-body")?.InnerHtml;
            var plain = Child(n, "ac:plain-text-body")?.InnerHtml;
            var name = n.GetAttributeValue("ac:name", "");
            var html = name switch
            {
                "code" or "noformat" => $"<pre class=\"language-{WebUtility.HtmlEncode(Param(n, "language"))}\">{plain}</pre>",
                "info" or "note" or "warning" or "tip" or "panel" =>
                    $"<div class=\"confluence-information-macro confluence-information-macro-{(name is "info" or "panel" ? "information" : name)}\"><div class=\"confluence-information-macro-body\">{rich}</div></div>",
                "jira" => WebUtility.HtmlEncode(Param(n, "key")),
                "status" => WebUtility.HtmlEncode(Param(n, "title")),
                "toc" or "children" or "pagetree" or "attachments" or "recently-updated" or "contentbylabel" or "livesearch" => "",
                _ => rich ?? plain ?? "",
            };
            Replace(n, html, block: name is not ("jira" or "status"));
        }

        foreach (var n in doc.DocumentNode.Descendants().Where(n => n.Name is "ac:link" or "ac:image" or "ac:emoticon" or "ac:task-list" or "ac:placeholder").Reverse().ToList())
        {
            var target = n.ChildNodes.FirstOrDefault(c => c.Name.StartsWith("ri:", StringComparison.Ordinal));
            var linkBody = Child(n, "ac:plain-text-link-body")?.InnerHtml ?? Child(n, "ac:link-body")?.InnerHtml;
            string? href = target?.Name switch
            {
                "ri:page" => pageByTitle(target.GetAttributeValue("ri:content-title", "")) is { } id ? "/" + Key(id) : null,
                "ri:attachment" => files.TryGetValue(target.GetAttributeValue("ri:filename", ""), out var path) ? "/" + path : null,
                "ri:url" => target.GetAttributeValue("ri:value", ""),
                _ => null,
            };
            var label = linkBody ?? WebUtility.HtmlEncode(target?.GetAttributeValue("ri:content-title", target.GetAttributeValue("ri:filename", "")) ?? "");
            var html = n.Name switch
            {
                "ac:link" => href is null ? label : $"<a href=\"{WebUtility.HtmlEncode(href)}\">{label}</a>",
                "ac:image" => href is null ? "" : $"<img src=\"{WebUtility.HtmlEncode(href)}\" alt=\"{WebUtility.HtmlEncode(target!.GetAttributeValue("ri:filename", ""))}\">",
                "ac:task-list" => "<ul>" + string.Concat(n.ChildNodes.Where(c => c.Name == "ac:task").Select(t =>
                    $"<li>{(Child(t, "ac:task-status")?.InnerText.Trim() == "complete" ? "[x]" : "[ ]")} {Child(t, "ac:task-body")?.InnerHtml}</li>")) + "</ul>",
                _ => "",
            };
            Replace(n, html, block: n.Name == "ac:task-list");
        }
        foreach (var n in doc.DocumentNode.Descendants().Where(n => n.Name.StartsWith("ri:", StringComparison.Ordinal) || n.Name.StartsWith("ac:", StringComparison.Ordinal)).Reverse().ToList())
            Replace(n, n.InnerHtml);
        return doc.DocumentNode.FirstChild.InnerHtml;
    }

    [GeneratedRegex(@"<!\[CDATA\[(.*?)\]\]>", RegexOptions.Singleline)] private static partial Regex Cdata();
}
