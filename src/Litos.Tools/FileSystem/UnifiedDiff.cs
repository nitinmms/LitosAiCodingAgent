using System.Text;

namespace Litos.Tools.FileSystem;

internal static class UnifiedDiff
{
    /// <summary>
    /// Minimal line-level diff for approval previews — not meant to be a general-purpose
    /// diff algorithm (no LCS/Myers), just a readable +/- rendering of whole-file changes.
    /// </summary>
    public static string Render(string? oldText, string newText, string path)
    {
        var oldLines = (oldText ?? string.Empty).Split('\n');
        var newLines = newText.Split('\n');

        var sb = new StringBuilder();
        sb.AppendLine($"--- {path}");
        sb.AppendLine($"+++ {path}");

        foreach (var line in oldLines)
            sb.AppendLine($"-{line}");
        foreach (var line in newLines)
            sb.AppendLine($"+{line}");

        return sb.ToString();
    }
}
