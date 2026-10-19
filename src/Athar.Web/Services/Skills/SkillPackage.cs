using System.IO.Compression;
using System.Text;
using Athar.Domain.Memory;

namespace Athar.Web.Services;

public sealed record SkillUpload(string Name, string Description, string BodyMd, IReadOnlyList<SkillFile> Files);

/// <summary>
/// T140 (FR-SK-02): a skill as a folder (<c>name/SKILL.md</c> plus its files), zipped for download, and the reverse for
/// publish by upload: a bare <c>SKILL.md</c> or a zip of one skill folder. Text files only; the package limits are
/// <see cref="SkillService"/>'s.
/// </summary>
public static class SkillPackage
{
    public const long MaxUploadBytes = 1024 * 1024;

    public static string SkillMd(string name, string description, string bodyMd) =>
        $"---\nname: {name}\ndescription: {description}\n---\n\n{bodyMd}";

    public static byte[] Zip(SkillDetail s)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, $"{s.Name}/SKILL.md", SkillMd(s.Name, s.Description, s.BodyMd));
            foreach (var f in s.Files) Add(zip, $"{s.Name}/{f.Path}", f.Content);
        }
        return ms.ToArray();

        static void Add(ZipArchive zip, string path, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
    }

    /// <summary>Reads an uploaded <c>SKILL.md</c> or <c>.zip</c>; throws <c>invalid_input</c> with a reason people can act on.</summary>
    public static SkillUpload Read(string fileName, byte[] bytes)
    {
        if (bytes.LongLength > MaxUploadBytes) throw new AtharError("invalid_input", "The file is larger than 1 MB.");
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Parse(Text(bytes, fileName), []);

        var texts = new List<SkillFile>();
        long total = 0;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            if (entries.Count > SkillService.MaxFiles + 1) throw new AtharError("invalid_input", $"A skill has at most {SkillService.MaxFiles} files besides SKILL.md.");
            foreach (var e in entries)
            {
                var path = e.FullName.Replace('\\', '/');
                if (path.StartsWith('/') || path.Split('/').Contains("..")) throw new AtharError("invalid_input", "Zip paths must be relative, with no '..'.");
                total += e.Length; // the declared size, checked before inflating anything
                if (total > SkillService.MaxPackageChars * 4L) throw new AtharError("invalid_input", "A skill package is limited to 256 KB.");
                using var s = e.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                texts.Add(new(path, Text(m.ToArray(), path)));
            }
        }
        catch (InvalidDataException) { throw new AtharError("invalid_input", "That isn't a readable zip file."); }

        var md = texts.Where(f => f.Path.Split('/')[^1] == "SKILL.md").OrderBy(f => f.Path.Count(c => c == '/')).FirstOrDefault()
            ?? throw new AtharError("invalid_input", "The zip has no SKILL.md.");
        var root = md.Path[..^"SKILL.md".Length]; // "" or "my-skill/"
        if (texts.Any(f => !f.Path.StartsWith(root))) throw new AtharError("invalid_input", "Put SKILL.md and its files in one folder.");
        return Parse(md.Content, [.. texts.Where(f => f != md).Select(f => f with { Path = f.Path[root.Length..] })]);
    }

    private static SkillUpload Parse(string skillMd, IReadOnlyList<SkillFile> files)
    {
        var text = skillMd.Replace("\r\n", "\n").TrimStart('﻿');
        if (!text.StartsWith("---\n")) throw new AtharError("invalid_input", "SKILL.md must start with front matter (name and description).");
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) throw new AtharError("invalid_input", "SKILL.md front matter isn't closed with ---.");
        // ponytail: flat "key: value" lines only; multi-line YAML values aren't read.
        var fields = text[4..end].Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim().Trim('"', '\''), StringComparer.OrdinalIgnoreCase);
        var body = text[(end + 4)..].TrimStart('\n');
        return new(fields.GetValueOrDefault("name") ?? "", fields.GetValueOrDefault("description") ?? "", body, files);
    }

    private static string Text(byte[] bytes, string path)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { throw new AtharError("invalid_input", $"{path} isn't a UTF-8 text file. Skills hold text files only."); }
    }
}
