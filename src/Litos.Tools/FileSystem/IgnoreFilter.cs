using System.Text.RegularExpressions;

namespace Litos.Tools.FileSystem;

/// <summary>
/// Decides which directories/files GrepTool should skip: a small fixed set of
/// always-noisy directories, plus a best-effort parse of the nearest .gitignore.
/// Not a full git-semantics implementation (no negation precedence across nested
/// files, no .git/info/exclude) — a pattern too exotic to parse confidently is
/// skipped rather than guessed, since a missed exclusion just searches a few
/// extra files while a wrongly-applied one could hide a real match.
/// </summary>
public sealed class IgnoreFilter
{
    private static readonly string[] DefaultIgnoredDirs = [".git", "bin", "obj", "node_modules", ".vs"];

    private readonly List<Regex> _fileRules = [];
    private readonly List<Regex> _dirRules = [];

    private IgnoreFilter()
    {
    }

    public static IgnoreFilter ForDirectory(string rootPath)
    {
        var filter = new IgnoreFilter();
        var gitignorePath = FindNearestGitignore(rootPath);
        if (gitignorePath is not null)
            filter.LoadGitignore(gitignorePath);
        return filter;
    }

    public bool IsDirectoryIgnored(string directoryName)
    {
        if (DefaultIgnoredDirs.Contains(directoryName, StringComparer.OrdinalIgnoreCase))
            return true;
        return _dirRules.Any(rule => rule.IsMatch(directoryName));
    }

    public bool IsFileIgnored(string fileName)
    {
        return _fileRules.Any(rule => rule.IsMatch(fileName));
    }

    private static string? FindNearestGitignore(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".gitignore");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private void LoadGitignore(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
                continue; // blank/comment/negation — negation needs precedence tracking we don't do; skip rather than misapply

            var isDirOnly = line.EndsWith('/');
            if (isDirOnly)
                line = line[..^1];

            if (line.Contains('/'))
                continue; // path-rooted pattern (e.g. "src/generated") — outside this filter's simple name-based matching

            var regex = TryBuildGlobRegex(line);
            if (regex is null)
                continue;

            (isDirOnly ? _dirRules : _fileRules).Add(regex);
        }
    }

    private static Regex? TryBuildGlobRegex(string glob)
    {
        if (glob.Length == 0)
            return null;

        try
        {
            var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        catch (RegexParseException)
        {
            return null;
        }
    }
}
