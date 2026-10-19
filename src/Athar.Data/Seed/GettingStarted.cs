using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Athar.Domain.Memory;
using Athar.Domain.Model;

namespace Athar.Data.Seed;

/// <summary>
/// The "Getting started with agents" page (FR-AG-10, T144) in English and Arabic. The text lives in docs/handbook and is
/// embedded here, so the seeds and the admin action add the same page. Seeds write it directly; the admin action goes
/// through the page operations like any other page.
/// </summary>
public static partial class GettingStarted
{
    public static readonly IReadOnlyList<string> Languages = ["en", "ar"];

    // Fixed IDs in Lane C's seed range (0xc30-0xc7f): the qa pages, and the minimal profile's Help workspace, space and pages.
    public static readonly IReadOnlyList<Guid> QaPages = [Id(0xc30), Id(0xc31)];
    public static readonly Guid HelpWorkspace = Id(0xc50);
    public static readonly Guid HelpSpace = Id(0xc51);
    public static readonly IReadOnlyList<Guid> MinimalPages = [Id(0xc52), Id(0xc53)];

    internal static Guid Id(int n) => new($"00000000-0000-7000-8000-{n:x12}");

    /// <summary>The page title (the Markdown's first heading).</summary>
    public static string Title(string lang) => Split(lang).Title;

    /// <summary>The page body in Markdown, without the title heading.</summary>
    public static string Body(string lang) => Split(lang).Body;

    private static (string Title, string Body) Split(string lang)
    {
        using var stream = typeof(GettingStarted).Assembly.GetManifestResourceStream($"handbook.getting-started-agents.{lang}.md")
            ?? throw new ArgumentException($"No getting-started page for '{lang}'.", nameof(lang));
        var text = new StreamReader(stream).ReadToEnd().Replace("\r\n", "\n").Trim();
        var newline = text.IndexOf('\n');
        return (text[2..newline].Trim(), text[(newline + 1)..].Trim());
    }

    /// <summary>Both pages, published, at the root of <paramref name="space"/>, with their FTS chunks (vectors come from the worker's sweep).</summary>
    internal static void AddPages(AtharDbContext db, Guid org, Guid space, IReadOnlyList<Guid> pageIds, Func<Guid> nextId, Guid? author = null)
    {
        for (var i = 0; i < Languages.Count; i++)
        {
            var (id, title, body) = (pageIds[i], Title(Languages[i]), Body(Languages[i]));
            db.Pages.Add(new Page
            {
                Id = id, OrgId = org, SpaceId = space, Position = 100 + i, Title = title, ContentJson = ProseMirror(body), ContentMd = body,
                Revision = 1, CreatedBy = author,
            });
            db.PageRevisions.Add(new PageRevision { Id = nextId(), OrgId = org, PageId = id, Revision = 1, Contributors = author is { } a ? [a] : [] });
            db.Chunks.AddRange(Chunker.Build(ChunkOwner.Page, id, 1, title, body, c =>
            {
                c.Id = nextId();
                c.OrgId = org;
                c.SpaceId = space;
            }));
        }
    }

    // ponytail: enough Markdown for this page (headings, bullets, numbered steps as paragraphs, bold and code marks). The
    // admin action goes through collab's full converter instead.
    private static string ProseMirror(string markdown)
    {
        var blocks = new List<object>();
        List<object>? list = null;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("- "))
            {
                if (list is null) blocks.Add(new { type = "bulletList", content = list = [] });
                list.Add(new { type = "listItem", content = new[] { Paragraph(line[2..]) } });
                continue;
            }
            list = null;
            if (line.Length == 0) continue;
            var level = line.TakeWhile(c => c == '#').Count();
            blocks.Add(level is > 0 and <= 3 && line.Length > level && line[level] == ' '
                ? new { type = "heading", attrs = new { level }, content = Inline(line[(level + 1)..]) }
                : Paragraph(line));
        }
        return JsonSerializer.Serialize(new { type = "doc", content = blocks });
    }

    private static object Paragraph(string text) => new { type = "paragraph", content = Inline(text) };

    private static object[] Inline(string text) => [.. InlineMarks().Split(text).Where(p => p.Length > 0).Select(p =>
        p.Length > 4 && p.StartsWith("**") && p.EndsWith("**") ? new { type = "text", text = p[2..^2], marks = new object[] { new { type = "bold" } } }
        : p.Length > 2 && p[0] == '`' && p[^1] == '`' ? new { type = "text", text = p[1..^1], marks = new object[] { new { type = "code" } } }
        : (object)new { type = "text", text = p })];

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")]
    private static partial Regex InlineMarks();
}
