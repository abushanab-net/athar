using System.Net;
using System.Text;
using System.Text.Json;

namespace Athar.Web.Sharing;

/// <summary>
/// Read-only HTML for share links (FR-SH-02), from the page's ProseMirror JSON. Allow-list only: known nodes and marks,
/// text encoded, links limited to http(s) and mailto, images to http(s) (NFR-03), and uploaded files (`/attachments/{id}`)
/// rewritten to the share's own route when their page is shared, dropped otherwise. Comment anchors, authors and history are
/// never shown.
/// </summary>
public static class ShareHtml
{
    /// <summary>
    /// The whole share page (Registry style, DESIGN.md): the Athar mark and a read-only seal above the content, in the
    /// reader's language and direction. Colors are fixed light values: a share page has no theme switch.
    /// </summary>
    public static string Document(string title, string body, string lang, bool rtl, string readOnly, string sharedFrom, bool diagrams = false) =>
        $$"""
        <!doctype html>
        <html lang="{{lang}}" dir="{{(rtl ? "rtl" : "ltr")}}">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex, nofollow">
        <title>{{Enc(title)}} · Athar</title>
        <link rel="icon" href="/favicon.svg" type="image/svg+xml">
        <link rel="stylesheet" href="/fonts.css">
        <style>
        :root{--ink:#14213d;--muted:#5b6475;--primary:#1b2a4a;--seal:#c2410c;--ground:#f6f7f9;--rule:#dfe3ea;--stroke:#c6ccd6;--fill:#eef1f5;--warn:#b45309}
        *{box-sizing:border-box}
        body{font-family:'Public Sans','Noto Sans Arabic',system-ui,'Segoe UI',sans-serif;font-size:15px;line-height:1.65;margin:0;color:var(--ink);background:var(--ground)}
        .bar{display:flex;align-items:center;gap:12px;max-width:52rem;margin:0 auto;padding:16px}
        .brand{display:inline-flex;align-items:center;gap:8px;color:var(--ink);text-decoration:none;font-weight:700}
        .from{color:var(--muted);font-size:.8125rem}
        .seal{margin-inline-start:auto;display:inline-flex;align-items:center;min-height:22px;padding:0 8px;border:1.5px solid var(--seal);border-radius:999px;color:var(--seal);font-size:.75rem;font-weight:600}
        .wrap{max-width:52rem;margin:0 auto;padding:0 16px 48px}
        nav{display:flex;flex-wrap:wrap;gap:4px 16px;margin-bottom:12px;font-size:.875rem}nav a{color:var(--primary)}
        main{background:#fff;border:1px solid var(--rule);border-radius:8px;padding:24px 28px}
        h1{font-size:1.5rem;line-height:1.25;margin:0 0 16px}h2{font-size:1.25rem}h3{font-size:1.0625rem}h4,h5,h6{font-size:1rem}
        p,li{max-width:72ch}
        a{color:var(--primary)}table{border-collapse:collapse;margin:12px 0}th,td{border:1px solid var(--rule);padding:6px 10px;text-align:start}th{background:var(--fill)}
        pre{background:var(--fill);border-radius:6px;padding:10px 12px;overflow-x:auto}code{font-family:ui-monospace,Consolas,monospace;font-size:.875em}img{max-width:100%}
        blockquote{margin:12px 0;padding:4px 16px;border-inline-start:2px solid var(--stroke);color:var(--muted)}
        .callout{border:1px solid color-mix(in srgb,var(--warn) 35%,transparent);background:color-mix(in srgb,var(--warn) 8%,transparent);border-radius:6px;padding:10px 14px;margin:12px 0}
        .tasks{list-style:none;padding-inline-start:4px}
        .notice h1{font-size:1.25rem}
        form{display:grid;gap:8px;max-width:22rem}label{font-size:.8125rem;font-weight:500}
        input{height:38px;padding:0 12px;border:1px solid var(--stroke);border-radius:6px;font:inherit}
        input:focus-visible,a:focus-visible,button:focus-visible{outline:2px solid var(--primary);outline-offset:2px}
        button{height:36px;padding:0 16px;border:0;border-radius:6px;background:var(--primary);color:#fff;font:inherit;font-weight:500;cursor:pointer;justify-self:start}
        [role=alert]{color:#b42318;font-weight:500}
        .muted{color:var(--muted)}
        .mermaid-diagram{margin:12px 0;overflow-x:auto}.mermaid-diagram svg{max-width:100%;height:auto}
        </style>
        </head>
        <body>
        <header class="bar"><span class="brand"><img src="/img/athar-mark.svg" alt="" width="28" height="28"><span>Athar</span></span><span class="from">{{Enc(sharedFrom)}}</span><span class="seal">{{Enc(readOnly)}}</span></header>
        <div class="wrap">{{body}}</div>
        {{(diagrams ? $"<script type=\"module\" src=\"{MermaidScript}\"></script>" : "")}}
        </body>
        </html>
        """;

    public static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>
    /// T120: how a ```mermaid code block starts. Page text is HTML-encoded (quotes too), so content can never produce it.
    /// </summary>
    public const string MermaidMarker = "<code class=\"language-mermaid\">";

    /// <summary>The one script a share page may load, and only when its content has a diagram.</summary>
    public const string MermaidScript = "/editor/athar-mermaid.js";

    public static bool HasMermaid(string body) => body.Contains(MermaidMarker, StringComparison.Ordinal);

    /// <summary>
    /// The content of a page. <paramref name="href"/> links page mentions that are inside the share, and maps an uploaded
    /// file's <c>/attachments/{id}</c> to its share route (null drops it).
    /// </summary>
    public static string Content(string? json, Func<string, string?> href)
    {
        if (string.IsNullOrEmpty(json)) return "";
        var sb = new StringBuilder();
        Node(JsonSerializer.Deserialize<JsonElement>(json), sb, href);
        return sb.ToString();
    }

    private static void Node(JsonElement n, StringBuilder sb, Func<string, string?> href)
    {
        var type = n.TryGetProperty("type", out var t) ? t.GetString() : null;
        string Attr(string name) => n.TryGetProperty("attrs", out var a) && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
        var dir = Attr("dir") is "rtl" or "ltr" ? Attr("dir") : "auto";
        void Wrap(string tag, string attrs = "")
        {
            sb.Append('<').Append(tag).Append(attrs).Append('>');
            Children(n, sb, href);
            sb.Append("</").Append(tag).Append('>');
        }
        switch (type)
        {
            case "doc": Children(n, sb, href); break;
            case "paragraph": Wrap("p", $" dir=\"{dir}\""); break;
            case "heading":
                var level = int.TryParse(Attr("level"), out var l) ? Math.Clamp(l, 1, 6) + 1 : 2; // the page title is the h1
                Wrap($"h{Math.Min(level, 6)}", $" dir=\"{dir}\"");
                break;
            case "bulletList": Wrap("ul"); break;
            case "orderedList": Wrap("ol", int.TryParse(Attr("start"), out var start) && start != 1 ? $" start=\"{start}\"" : ""); break;
            case "listItem": Wrap("li", $" dir=\"{dir}\""); break;
            case "taskList": Wrap("ul", " class=\"tasks\""); break;
            case "taskItem":
                sb.Append($"<li dir=\"{dir}\">").Append(Attr("checked") == "True" || Attr("checked") == "true" ? "☑ " : "☐ ");
                Children(n, sb, href);
                sb.Append("</li>");
                break;
            case "blockquote": Wrap("blockquote", $" dir=\"{dir}\""); break;
            case "callout": Wrap("div", " class=\"callout\""); break;
            case "codeBlock":
                sb.Append(Attr("language") == "mermaid" ? $"<pre class=\"mermaid-source\">{MermaidMarker}" : "<pre><code>");
                foreach (var c in Kids(n)) sb.Append(Enc(c.TryGetProperty("text", out var tx) ? tx.GetString() : ""));
                sb.Append("</code></pre>");
                break;
            case "horizontalRule": sb.Append("<hr>"); break;
            case "hardBreak": sb.Append("<br>"); break;
            case "image":
                if ((Attachment(Attr("src"), href) ?? SafeUrl(Attr("src"), images: true)) is { } src) sb.Append($"<img src=\"{Enc(src)}\" alt=\"{Enc(Attr("alt"))}\">");
                break;
            case "table": Wrap("table"); break;
            case "tableRow": Wrap("tr"); break;
            case "tableHeader": Wrap("th", Spans(Attr)); break;
            case "tableCell": Wrap("td", Spans(Attr)); break;
            case "mention": sb.Append("@").Append(Enc(Attr("label"))); break;
            case "pageMention":
                var label = Enc(Attr("label"));
                sb.Append(href(Attr("id")) is { } to ? $"<a href=\"{Enc(to)}\">{label}</a>" : label);
                break;
            case "text": Text(n, sb, href); break;
            default: Children(n, sb, href); break; // unknown wrapper: keep its content, drop the node
        }
    }

    private static string Spans(Func<string, string> attr) =>
        string.Concat(new[] { "colspan", "rowspan" }.Where(a => int.TryParse(attr(a), out var v) && v > 1).Select(a => $" {a}=\"{int.Parse(attr(a))}\""));

    private static IEnumerable<JsonElement> Kids(JsonElement n) =>
        n.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray() : [];

    private static void Children(JsonElement n, StringBuilder sb, Func<string, string?> href)
    {
        foreach (var c in Kids(n)) Node(c, sb, href);
    }

    private static void Text(JsonElement n, StringBuilder sb, Func<string, string?> href)
    {
        var text = Enc(n.TryGetProperty("text", out var t) ? t.GetString() : "");
        var close = new Stack<string>();
        if (n.TryGetProperty("marks", out var marks))
            foreach (var m in marks.EnumerateArray())
            {
                var (open, end) = m.GetProperty("type").GetString() switch
                {
                    "bold" => ("<strong>", "</strong>"),
                    "italic" => ("<em>", "</em>"),
                    "strike" => ("<s>", "</s>"),
                    "underline" => ("<u>", "</u>"),
                    "code" => ("<code>", "</code>"),
                    "link" when m.TryGetProperty("attrs", out var a) && a.TryGetProperty("href", out var h) && (Attachment(h.GetString(), href) ?? SafeUrl(h.GetString(), images: false)) is { } url
                        => ($"<a href=\"{Enc(url)}\" rel=\"noopener noreferrer nofollow\">", "</a>"),
                    _ => ("", ""), // commentAnchor and anything unknown: not shown
                };
                sb.Append(open);
                close.Push(end);
            }
        sb.Append(text);
        while (close.Count > 0) sb.Append(close.Pop());
    }

    /// <summary>An uploaded file (<c>/attachments/{id}</c>, as the editor inserts it) through the share, or null.</summary>
    private static string? Attachment(string? url, Func<string, string?> href) =>
        url is not null && url.StartsWith("/attachments/") && Guid.TryParse(url["/attachments/".Length..], out var id) ? href($"/attachments/{id}") : null;

    private static string? SafeUrl(string? url, bool images) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "https" or "http" || !images && u.Scheme == "mailto") ? u.ToString() : null;
}
