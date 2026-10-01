using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Verification;

/// <summary>
/// Changed-line coverage: the coverage report intersected with the lines the task changed
/// (ReadMe_LitosSoftwareFactory_V1.md §10.2).
/// </summary>
public static class ChangedLineCoverageCalculator
{
    /// <summary>
    /// Rewrites a report's file paths as repository-relative paths with forward slashes, which is
    /// how a diff names files. A report may give absolute paths, paths relative to one of its
    /// source roots, or paths relative to the directory the test command ran in.
    /// </summary>
    public static IReadOnlyList<CoverageFile> Normalize(CoverageReport report, string workingCopy, string stepDirectory)
    {
        var repository = Path.GetFullPath(workingCopy);
        return [.. report.Files.Select(file =>
        {
            var (path, placed) = Resolve(file.Path, report.SourceRoots, repository, stepDirectory);
            return file with { Path = path, InRepository = placed };
        })];
    }

    private static (string Path, bool InRepository) Resolve(string path, IReadOnlyList<string> sourceRoots, string repository, string stepDirectory)
    {
        var native = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        List<string> candidates = [];
        if (Path.IsPathRooted(native))
        {
            candidates.Add(native);
        }
        else
        {
            foreach (var root in sourceRoots)
            {
                var nativeRoot = root.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                candidates.Add(Path.IsPathRooted(nativeRoot) ? Path.Combine(nativeRoot, native) : Path.Combine(stepDirectory, nativeRoot, native));
            }

            candidates.Add(Path.Combine(stepDirectory, native));
        }

        var inside = candidates.Select(Path.GetFullPath).Where(c => IsUnder(c, repository)).ToList();
        // Only a candidate that is really there counts as placed. One that merely could be inside
        // the repository is a guess: its path is used, but it stays open to suffix matching.
        var existing = inside.FirstOrDefault(File.Exists);
        var chosen = existing ?? inside.FirstOrDefault();

        // Outside the working copy (a report produced elsewhere): keep the path as written and
        // let suffix matching find it.
        return ((chosen is null ? path : Path.GetRelativePath(repository, chosen)).Replace('\\', '/'), existing is not null);
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <param name="coverage">Coverage files, ideally already normalized; several entries for
    /// one file (two test projects covering the same source) are merged.</param>
    public static ChangedLineCoverage Calculate(IReadOnlyList<FileChange> changes, IReadOnlyList<CoverageFile> coverage)
    {
        var byPath = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        var unplaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in coverage)
        {
            var key = file.Path.Replace('\\', '/');
            if (!byPath.TryGetValue(key, out var hits))
                byPath[key] = hits = [];
            if (!file.InRepository)
                unplaced.Add(key);
            foreach (var (line, count) in file.LineHits)
                hits[line] = hits.TryGetValue(line, out var existing) ? Math.Max(existing, count) : count;
        }

        var covered = 0;
        var measurable = 0;
        var uncovered = new List<UncoveredLines>();
        var unmeasured = new List<string>();

        foreach (var change in changes)
        {
            // A file the report says nothing about has no measurable lines. For a test file, a
            // config file or documentation that is right; for a new source file no test ever
            // loaded it is not, and the two cannot be told apart from here — so the file is named,
            // and the percentage is never left to imply it was measured.
            if (Find(byPath, unplaced, change.Path.Replace('\\', '/')) is not { } hits)
            {
                if (change.AddedLineCount > 0)
                    unmeasured.Add(change.Path);
                continue;
            }

            var missed = new List<int>();
            foreach (var range in change.AddedLines)
            {
                for (var line = range.Start; line <= range.End; line++)
                {
                    // Lines absent from the report are not executable: comments, blanks, braces.
                    if (!hits.TryGetValue(line, out var count))
                        continue;

                    measurable++;
                    if (count > 0)
                        covered++;
                    else
                        missed.Add(line);
                }
            }

            if (missed.Count > 0)
                uncovered.Add(new UncoveredLines(change.Path, missed));
        }

        return new ChangedLineCoverage(covered, measurable, uncovered) { UnmeasuredFiles = unmeasured };
    }

    private static Dictionary<int, int>? Find(
        Dictionary<string, Dictionary<int, int>> byPath, HashSet<string> unplaced, string changedPath)
    {
        if (byPath.TryGetValue(changedPath, out var exact))
            return exact;

        // Fall back to a path-segment suffix match, in either direction, but only when exactly
        // one report file matches: two files with the same name must not be confused. Only
        // entries that could not be placed in the repository take part — one that was placed has
        // an exact repository path, and matching it by suffix would lend its hit counts to a
        // different file that merely shares its name (a root Program.cs and src/Program.cs).
        var matches = byPath
            .Where(entry => unplaced.Contains(entry.Key))
            .Where(entry =>
                entry.Key.EndsWith("/" + changedPath, StringComparison.OrdinalIgnoreCase)
                || changedPath.EndsWith("/" + entry.Key, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0].Value : null;
    }
}
