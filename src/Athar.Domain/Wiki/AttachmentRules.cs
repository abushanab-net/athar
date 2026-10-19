using System.IO.Compression;
using System.Text;

namespace Athar.Domain.Wiki;

/// <summary>
/// FR-PG-06 attachments. The type comes from the bytes, never from the client: PNG, JPEG, GIF and WebP (shown inline) and
/// PDF and DOCX (download only). Everything else, SVG and HTML included, is rejected, and so is a declared type that
/// disagrees with the bytes.
/// </summary>
public static class AttachmentRules
{
    public const long MaxBytes = 25L * 1024 * 1024;
    /// <summary>Larger PDF and DOCX files are stored but not text-extracted (T164: imports carry files of any size).</summary>
    public const long MaxExtractBytes = 100L * 1024 * 1024;
    public const long MaxDocxExpandedBytes = 100L * 1024 * 1024;
    public const int MaxDocxEntries = 2_000;
    public const int MaxTextChars = 2 * 1024 * 1024;
    public static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The worker job that extracts a PDF's or DOCX's text (queued with the upload).</summary>
    public const string ExtractKind = "extract-attachment";
    public sealed record ExtractPayload(Guid AttachmentId);

    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Pdf = "application/pdf";

    /// <summary>Raster images are served inline; documents only as downloads.</summary>
    public static bool Inline(string mime) => mime.StartsWith("image/", StringComparison.Ordinal);

    /// <summary>Whether extraction applies (no OCR for images).</summary>
    public static bool HasText(string mime) => mime is Pdf or Docx;
    public static bool Vision(string mime) => mime is "image/png" or "image/jpeg" or "image/webp";

    /// <summary>The allowed type the bytes are, or null. DOCX also needs <see cref="CheckDocx"/>.</summary>
    public static string? Sniff(ReadOnlySpan<byte> head) => head switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7' or (byte)'9', (byte)'a', ..] => "image/gif",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', ..] => Pdf,
        [(byte)'P', (byte)'K', 0x03, 0x04, ..] => Docx, // a zip; CheckDocx decides
        _ => null,
    };

    /// <summary>
    /// A declared type is fine when absent, generic, or the same as the sniffed one (<c>image/jpg</c> counts as JPEG).
    /// </summary>
    public static bool DeclaredMatches(string? declared, string sniffed)
    {
        if (string.IsNullOrWhiteSpace(declared)) return true;
        var d = declared.Split(';')[0].Trim().ToLowerInvariant();
        return d is "application/octet-stream" || d == sniffed || (d, sniffed) is ("image/jpg", "image/jpeg");
    }

    /// <summary>
    /// A Word document within the zip-bomb limits, from its central directory (nothing is expanded). Null when fine, else why not.
    /// </summary>
    public static string? CheckDocx(Stream zip)
    {
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxDocxEntries) return $"The document has more than {MaxDocxEntries} parts.";
            if (archive.Entries.Sum(e => e.Length) > MaxDocxExpandedBytes) return "The document expands to more than 100 MB.";
            if (archive.GetEntry("word/document.xml") is null || archive.GetEntry("[Content_Types].xml") is null)
                return "Only Word documents (.docx) are accepted among zip files.";
            return null;
        }
        catch (InvalidDataException) { return "The document is damaged."; }
    }

    /// <summary>A safe download name: no path, no control or quote characters, at most 200 characters.</summary>
    public static string FileName(string? name)
    {
        var raw = Path.GetFileName(name ?? "").Trim();
        var clean = new StringBuilder();
        foreach (var ch in raw) clean.Append(char.IsControl(ch) || ch is '"' or '\\' or '/' ? '_' : ch);
        var s = clean.ToString();
        return s.Length == 0 ? "file" : s.Length > 200 ? s[..200] : s;
    }
}
