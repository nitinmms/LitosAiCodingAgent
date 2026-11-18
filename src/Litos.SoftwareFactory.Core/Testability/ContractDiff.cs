namespace Litos.SoftwareFactory.Core.Testability;

public enum DiffKind { Same, Added, Removed }

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>
/// The line diff an adoption shows (testability-architecture.md §5: "shows a diff against the
/// current profile"), so a person sees what the contract changes before it is used. A contract is
/// a few dozen lines, so a plain longest-common-subsequence is enough.
/// </summary>
public static class ContractDiff
{
    public static IReadOnlyList<DiffLine> Lines(string before, string after)
    {
        var a = Split(before);
        var b = Split(after);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        }

        var lines = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                lines.Add(new DiffLine(DiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                lines.Add(new DiffLine(DiffKind.Removed, a[x++]));
            }
            else
            {
                lines.Add(new DiffLine(DiffKind.Added, b[y++]));
            }
        }

        while (x < a.Length)
            lines.Add(new DiffLine(DiffKind.Removed, a[x++]));
        while (y < b.Length)
            lines.Add(new DiffLine(DiffKind.Added, b[y++]));
        return lines;
    }

    public static bool Changes(IReadOnlyList<DiffLine> lines) => lines.Any(l => l.Kind != DiffKind.Same);

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
}
