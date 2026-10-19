namespace Athar.Web.Components.Brain;

public enum DiffOp { Same, Removed, Added }
public sealed record DiffLine(DiffOp Op, string Text);

/// <summary>Line diff for the proposal queue (FR-MM-09): the longest common subsequence of lines.</summary>
// ponytail: O(n·m) LCS over lines, fine for memory-sized bodies; word-level highlighting if reviewers ask for it.
public static class LineDiff
{
    public static IReadOnlyList<DiffLine> Compute(string before, string after)
    {
        string[] a = Lines(before), b = Lines(after);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
            lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add(new(DiffOp.Same, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add(new(DiffOp.Removed, a[x++]));
            else result.Add(new(DiffOp.Added, b[y++]));
        }
        while (x < a.Length) result.Add(new(DiffOp.Removed, a[x++]));
        while (y < b.Length) result.Add(new(DiffOp.Added, b[y++]));
        return result;
    }

    // An empty body has no lines, so a new memory's diff is all additions.
    private static string[] Lines(string text) => text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');
}
