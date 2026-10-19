namespace Athar.Domain.Wiki;

/// <summary>
/// How a workspace or space shows in menus: a colored badge with an icon, or the name's initials. Colors and icons come
/// from fixed sets, so every badge carries white text legibly in both themes and the values stay valid.
/// </summary>
public static class Appearance
{
    /// <summary>Color keys and their mid tones (white text on each).</summary>
    public static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>
    {
        ["teal"] = "#0f766e", ["blue"] = "#3b6fc4", ["indigo"] = "#4f46e5", ["purple"] = "#7c4ddf", ["pink"] = "#c8325a",
        ["red"] = "#b91c1c", ["orange"] = "#c2410c", ["amber"] = "#b45309", ["green"] = "#15803d", ["slate"] = "#475569",
    };

    /// <summary>Icon keys; the web app maps each to a Material icon.</summary>
    public static readonly IReadOnlyList<string> Icons =
        ["code", "rocket", "book", "people", "build", "cloud", "security", "chart", "idea", "work", "school", "support", "science", "palette", "home", "star"];

    public static bool Valid(string? color, string? icon) =>
        (color is null || Colors.ContainsKey(color)) && (icon is null || Icons.Contains(icon));

    /// <summary>The color to show: the chosen one, else a stable one from the id.</summary>
    public static string Hex(string? color, Guid id) =>
        color is not null && Colors.TryGetValue(color, out var hex) ? hex : Colors.Values.ElementAt((int)((uint)id.GetHashCode() % (uint)Colors.Count));

    /// <summary>Up to two initials ("Core Dev" → "CD", "DevOps" → "DE"), for a badge without an icon.</summary>
    public static string Initials(string name)
    {
        // Letters and digits only: "People (confidential)" → "PC", not "P(".
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)])).Where(w => w.Length > 0).ToList();
        var initials = words.Count >= 2 ? $"{words[0][0]}{words[1][0]}" : words.Count == 1 ? words[0][..Math.Min(2, words[0].Length)] : "?";
        return initials.ToUpperInvariant();
    }
}
