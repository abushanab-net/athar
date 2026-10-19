using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Athar.Domain.Model;

namespace Athar.Domain.Memory;

public sealed record ChunkText(int Idx, string HeadingPath, string Body);

/// <summary>
/// The chunking API (M-C2): Markdown → searchable chunks for any owner (pages, memories, source documents, attachments,
/// skills). Chunks follow headings and fill up to about <see cref="Target"/> characters (≈ the D8 benchmark size);
/// fenced code stays whole unless it alone exceeds <see cref="Max"/>.
/// </summary>
public static partial class Chunker
{
    public const int Target = 800, Max = 1600;

    /// <summary>Splits <paramref name="markdown"/> under <paramref name="title"/>. Every chunk's heading path starts with the title.</summary>
    public static IReadOnlyList<ChunkText> Split(string title, string markdown)
    {
        var chunks = new List<ChunkText>();
        var headings = new List<(int Level, string Text)>();
        var buf = new StringBuilder();
        string Path() => string.Join(" › ", [title, .. headings.Select(h => h.Text)]);
        var path = Path();
        void Flush()
        {
            var body = buf.ToString().Trim();
            if (body.Length > 0) chunks.Add(new(chunks.Count, path, body));
            buf.Clear();
        }

        foreach (var block in Blocks(markdown))
        {
            if (Heading().Match(block) is { Success: true } h)
            {
                Flush();
                var level = h.Groups[1].Length;
                headings.RemoveAll(x => x.Level >= level);
                headings.Add((level, h.Groups[2].Value.Trim()));
                path = Path();
                continue;
            }
            if (buf.Length > 0 && buf.Length + block.Length > Target) Flush();
            if (block.Length > Max)
            {
                foreach (var piece in HardSplit(block)) { buf.Append(piece); Flush(); }
                continue;
            }
            buf.Append(block).Append("\n\n");
        }
        Flush();
        // A title-only owner still gets one chunk, so it's findable by its title.
        if (chunks.Count == 0) chunks.Add(new(0, title, ""));
        return chunks;
    }

    /// <summary>
    /// Chunk rows for an owner revision. The search copy is the heading path plus the body, normalized (FR-SR-05); the body
    /// keeps the original text for snippets. <paramref name="keys"/> sets the denormalized scope keys and classification.
    /// </summary>
    public static List<Chunk> Build(ChunkOwner kind, Guid ownerId, long revision, string title, string markdown, Action<Chunk> keys) =>
        [.. Split(title, markdown).Select(t =>
        {
            var c = new Chunk
            {
                OwnerKind = kind, OwnerId = ownerId, OwnerRevision = revision, Idx = t.Idx, HeadingPath = t.HeadingPath, Body = t.Body,
                SearchText = TextNormalizer.Normalize(t.HeadingPath + "\n" + t.Body),
                ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(t.HeadingPath + "\n" + t.Body))),
            };
            keys(c);
            return c;
        })];

    /// <summary>The text a chunk is embedded from: its heading path and original body.</summary>
    public static string EmbeddingText(Chunk c) => c.HeadingPath + "\n" + c.Body;

    // Blank-line separated blocks; a fenced code block is one block.
    private static IEnumerable<string> Blocks(string markdown)
    {
        var block = new StringBuilder();
        var fenced = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var fence = line.TrimStart().StartsWith("```", StringComparison.Ordinal);
            var outside = !fenced && !fence;
            if (outside && (line.Trim().Length == 0 || Heading().IsMatch(line)))
            {
                if (block.Length > 0) yield return block.ToString().TrimEnd();
                block.Clear();
                if (line.Trim().Length > 0) yield return line; // a heading is its own block
                continue;
            }
            block.Append(line).Append('\n');
            if (fence) fenced = !fenced;
        }
        if (block.Length > 0) yield return block.ToString().TrimEnd();
    }

    // An oversized block: cut at the last sentence or line end before Max, else at Max.
    private static IEnumerable<string> HardSplit(string block)
    {
        while (block.Length > Max)
        {
            var cut = block.LastIndexOfAny(['\n', '.', '؟', '?', '!', '؛'], Max - 1, Max - Target) + 1;
            if (cut <= 0) cut = Max;
            yield return block[..cut];
            block = block[cut..].TrimStart();
        }
        if (block.Length > 0) yield return block;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex Heading();
}
