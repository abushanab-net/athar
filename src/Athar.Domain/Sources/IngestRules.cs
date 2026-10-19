using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Athar.Domain.Sources;

/// <summary>
/// FR-KS-06 (T142) ingestion rules, pure: which documents are read (text only, at most 10 MB), which addresses a URL
/// snapshot may reach (public unicast only: no loopback, private, link-local, CGNAT, multicast or reserved ranges), and
/// how an HTML page becomes text.
/// </summary>
public static partial class IngestRules
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const int MaxTextChars = 2 * 1024 * 1024;
    public const int MaxRedirects = 3;
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(30);
    public const string IngestKind = "ingest-source";
    /// <summary>T143: <c>athar ingest</c> sends text documents only, at most 1 MB each (like GitHub docs).</summary>
    public static readonly IReadOnlySet<string> FolderTypes = new HashSet<string> { ".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc", ".html", ".htm" };
    public const int MaxFolderFileBytes = 1024 * 1024;

    public sealed record IngestPayload(Guid SourceId);

    /// <summary>Extension → media type, the only document types read (G-4).</summary>
    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown", [".markdown"] = "text/markdown", [".mdx"] = "text/markdown", [".rst"] = "text/plain", [".adoc"] = "text/plain", [".txt"] = "text/plain", [".html"] = "text/html", [".htm"] = "text/html",
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    /// <summary>The media type for a file name, or null when it isn't a supported document.</summary>
    public static string? MediaTypeOf(string fileName) => Types.GetValueOrDefault(Path.GetExtension(fileName));

    /// <summary>A URL a snapshot may start from: absolute http(s), no credentials in it.</summary>
    public static Uri? ParseUrl(string? input) =>
        Uri.TryCreate((input ?? "").Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && string.IsNullOrEmpty(u.UserInfo) ? u : null;

    /// <summary>
    /// Whether a fetch may connect to <paramref name="ip"/>: public unicast only. Every resolved address is checked, and the
    /// connection goes to the checked address, so DNS rebinding can't swap in a private one afterwards.
    /// </summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal || ip.IsIPv6Teredo
                || ip.GetAddressBytes() is [0x20, 0x01, 0x0d, 0xb8, ..]); // 2001:db8::/32 documentation
        var b = ip.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 or >= 224               // this network, private, loopback, multicast and reserved
            || b[0] == 100 && b[1] is >= 64 and <= 127           // CGNAT 100.64/10
            || b[0] == 169 && b[1] == 254                        // link-local, cloud metadata
            || b[0] == 172 && b[1] is >= 16 and <= 31            // private
            || b[0] == 192 && b[1] == 168                        // private
            || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2        // IETF protocol assignments, TEST-NET-1
            || b[0] == 198 && b[1] is 18 or 19                   // benchmarking
            || b[0] == 198 && b[1] == 51 && b[2] == 100          // TEST-NET-2
            || b[0] == 203 && b[1] == 0 && b[2] == 113);         // TEST-NET-3
    }

    [GeneratedRegex(@"<(script|style|noscript|template|svg|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();
    [GeneratedRegex(@"<\s*(br|/p|/div|/h[1-6]|/li|/tr|/section|/article|/header|/footer|/blockquote|/pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();
    [GeneratedRegex(@"<\s*h([1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();
    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Item();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();
    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex Blank();

    /// <summary>
    /// The readable text of an HTML page as light Markdown (headings and list items kept), without scripts, styles or markup.
    /// ponytail: regex, not a DOM; it reads ordinary pages well enough for search, and a parser can replace it if needed.
    /// </summary>
    public static (string? Title, string Text) HtmlToText(string html)
    {
        var title = TitleTag().Match(html) is { Success: true } t ? Clean(WebUtility.HtmlDecode(Tag().Replace(t.Groups[1].Value, ""))) : null;
        var s = Hidden().Replace(html, " ");
        s = Heading().Replace(s, m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " ");
        s = Item().Replace(s, "\n- ");
        s = Breaks().Replace(s, "\n");
        s = WebUtility.HtmlDecode(Tag().Replace(s, " "));
        var lines = s.Split('\n').Select(l => Spaces().Replace(l, " ").Trim());
        return (string.IsNullOrWhiteSpace(title) ? null : title, Blank().Replace(string.Join('\n', lines), "\n\n").Trim());

        static string Clean(string v) => Spaces().Replace(v, " ").Trim();
    }
}
