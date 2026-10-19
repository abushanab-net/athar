using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Athar.Domain.Sources;

/// <summary>
/// FR-KS-01/02 GitHub sync rules, pure: which repos may be attached (the allow-list), which files are synced as docs
/// (README, <c>docs/**</c>, ADRs and Markdown, at most 1 MB) and which are manifests, and the structure document (a tree
/// summary plus the dependencies the manifests declare).
/// </summary>
public static partial class GitHubRules
{
    public const long MaxDocBytes = 1024 * 1024;
    public const string StructurePath = ".athar/structure.md";
    public const string SyncKind = "github-sync";
    public static readonly TimeSpan SweepEvery = TimeSpan.FromHours(1);

    public sealed record SyncPayload(Guid SourceId);

    [GeneratedRegex(@"^(?:https?://github\.com/|git@github\.com:|github\.com/)?([A-Za-z0-9-]+)/([A-Za-z0-9._-]+?)(?:\.git)?/?$")]
    private static partial Regex Repo();

    /// <summary><c>owner/name</c>, lower-cased, from a slug or a GitHub URL; null if it isn't one.</summary>
    public static string? NormalizeRepo(string? input)
    {
        var m = Repo().Match((input ?? "").Trim());
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}".ToLowerInvariant() : null;
    }

    /// <summary>Allow-list entries are <c>owner/name</c> or <c>owner/*</c>. An empty list allows nothing.</summary>
    public static bool IsAllowed(string repo, IEnumerable<string> allowList) =>
        allowList.Select(a => a.Trim().ToLowerInvariant()).Any(a => a == repo || (a.EndsWith("/*") && repo.StartsWith(a[..^1], StringComparison.Ordinal)));

    private static readonly string[] DocExtensions = [".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc"];
    private static readonly string[] Markdown = [".md", ".markdown", ".mdx"];

    /// <summary>A doc to sync: a text document that is Markdown, a README, or under <c>docs/</c> (ADRs included), at most 1 MB.</summary>
    public static bool IsDoc(string path, long size)
    {
        if (size > MaxDocBytes || path.StartsWith(".athar/", StringComparison.Ordinal)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!DocExtensions.Contains(ext)) return false;
        var name = Path.GetFileName(path).ToLowerInvariant();
        return Markdown.Contains(ext) || name.StartsWith("readme", StringComparison.Ordinal)
            || path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || path.Contains("/docs/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsManifest(string path)
    {
        var name = Path.GetFileName(path);
        return name is "package.json" or "requirements.txt" or "go.mod" or "Directory.Packages.props"
            || name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".fsproj", StringComparison.Ordinal);
    }

    [GeneratedRegex("""<Package(?:Reference|Version)\s+Include="([^"]+)"(?:\s+Version="([^"]*)")?""")]
    private static partial Regex NuGet();

    /// <summary>The dependencies a manifest declares, as <c>name version</c> (version omitted when unknown).</summary>
    public static IReadOnlyList<string> Dependencies(string path, string content)
    {
        var name = Path.GetFileName(path);
        try
        {
            if (name == "package.json")
            {
                using var doc = JsonDocument.Parse(content);
                return [.. new[] { "dependencies", "devDependencies" }
                    .Where(k => doc.RootElement.TryGetProperty(k, out var o) && o.ValueKind == JsonValueKind.Object)
                    .SelectMany(k => doc.RootElement.GetProperty(k).EnumerateObject().Select(p => $"{p.Name} {p.Value.GetString()}"))];
            }
            if (name == "requirements.txt")
                return [.. content.Split('\n').Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0 && !l.StartsWith('-'))];
            if (name == "go.mod")
                return [.. content.Split('\n').Select(l => l.Trim()).Where(l => Regex.IsMatch(l, @"^[\w.\-/]+\s+v[\w.\-+]+"))
                    .Select(l => l.Replace("require ", "")).Where(l => !l.StartsWith("module", StringComparison.Ordinal))];
            return [.. NuGet().Matches(content).Select(m => m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? $"{m.Groups[1].Value} {m.Groups[2].Value}" : m.Groups[1].Value)];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The structure document: top-level folders with file counts, then each manifest's dependencies.</summary>
    public static string Structure(string repo, string sha, IReadOnlyCollection<string> files, IEnumerable<(string Path, IReadOnlyList<string> Dependencies)> manifests)
    {
        var md = new StringBuilder($"# Structure of {repo}\n\nCommit `{sha[..Math.Min(7, sha.Length)]}`, {files.Count} files.\n\n## Top level\n");
        foreach (var g in files.GroupBy(f => f.Contains('/') ? f[..(f.IndexOf('/') + 1)] : "(root)").OrderBy(g => g.Key, StringComparer.Ordinal))
            md.Append($"- {g.Key} ({g.Count()} files)\n");
        var any = false;
        foreach (var (path, deps) in manifests.OrderBy(m => m.Path, StringComparer.Ordinal))
        {
            if (!any) { md.Append("\n## Dependencies\n"); any = true; }
            md.Append($"\n### {path}\n");
            foreach (var d in deps) md.Append($"- {d}\n");
        }
        return md.ToString();
    }
}
