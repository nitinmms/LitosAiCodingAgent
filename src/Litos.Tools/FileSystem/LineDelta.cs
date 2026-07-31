namespace Litos.Tools.FileSystem;

/// <summary>
/// Naive line-count delta for the "+N -M" summaries tools report alongside a successful
/// write/edit — matches UnifiedDiff's own "not a real diff" philosophy (no LCS matching),
/// just counting lines on each side of the change.
/// </summary>
internal static class LineDelta
{
    /// <summary>Whole-file replace: every old line counts as removed, every new line as added.</summary>
    public static (int Added, int Removed) Count(string? oldText, string newText) =>
        (CountLines(newText), oldText is null ? 0 : CountLines(oldText));

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.Split('\n').Length;
}
