using System.Text.RegularExpressions;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Git;

/// <summary>
/// Reads the files and added line ranges out of a unified diff — what changed-line coverage
/// intersects with a coverage report. Works on any context size, including --unified=0.
/// </summary>
public static partial class DiffParser
{
    // @@ -oldStart[,oldCount] +newStart[,newCount] @@
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    public static IReadOnlyList<FileChange> Parse(string patch)
    {
        var files = new List<FileChange>();
        string? path = null;
        var ranges = new List<LineRange>();
        var newLine = 0;
        int? runStart = null;
        var inHunk = false;

        void EndRun()
        {
            if (runStart is { } start)
                ranges.Add(new LineRange(start, newLine - 1));
            runStart = null;
        }

        void EndFile()
        {
            EndRun();
            if (path is not null)
                files.Add(new FileChange(path, [.. ranges]));
            path = null;
            ranges = [];
            inHunk = false;
        }

        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                EndFile();
                continue;
            }

            if (!inHunk && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                // "+++ /dev/null" is a deleted file: it has no lines in the new version.
                var target = line[4..];
                path = target == "/dev/null" ? null : NewPath(target);
                continue;
            }

            var hunk = HunkHeader().Match(line);
            if (hunk.Success)
            {
                EndRun();
                inHunk = true;
                newLine = int.Parse(hunk.Groups[1].Value);
                continue;
            }

            if (!inHunk || path is null)
                continue;

            if (line.StartsWith('+'))
            {
                runStart ??= newLine;
                newLine++;
            }
            else if (line.StartsWith('-') || line.StartsWith('\\'))
            {
                // A removed line, or "\ No newline at end of file": neither exists in the new version.
                EndRun();
            }
            else
            {
                EndRun();
                newLine++;
            }
        }

        EndFile();
        return files;
    }

    private static string NewPath(string target)
    {
        // Some diff formats append a tab and a timestamp; drop anything after a tab.
        var tab = target.IndexOf('\t');
        if (tab >= 0)
            target = target[..tab];

        target = Unquote(target);
        return target.StartsWith("b/", StringComparison.Ordinal) ? target[2..] : target;
    }

    /// <summary>Git quotes paths containing special characters; core.quotepath=false keeps
    /// non-ASCII as is, so only the surrounding quotes and simple escapes remain.</summary>
    private static string Unquote(string path) =>
        path.Length >= 2 && path[0] == '"' && path[^1] == '"'
            ? path[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\")
            : path;
}
