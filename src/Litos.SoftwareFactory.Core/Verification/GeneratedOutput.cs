namespace Litos.SoftwareFactory.Core.Verification;

/// <summary>
/// Build, test and coverage output that must never be on a task's branch (README_Litos.Probe.Web.md
/// finding 6: about 53k lines of <c>.trx</c> and coverage files were committed, and the review
/// called it minor). The factory checks every verification's changed files against these, so it
/// does not depend on a review that may be light or skipped for budget.
/// </summary>
public static class GeneratedOutput
{
    /// <summary>Folders that only tools write into, matched as a whole path segment.</summary>
    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "TestResults", "test-results", "coverage", ".nyc_output", "playwright-report",
        "__pycache__", ".pytest_cache", "htmlcov",
    };

    /// <summary>Report files, wherever they are.</summary>
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        "coverage.cobertura.xml", "cobertura-coverage.xml", "lcov.info", "coverage-final.json", ".coverage",
    };

    private static readonly string[] Extensions = [".trx", ".coverage"];

    /// <summary>The paths among <paramref name="changed"/> that are generated output, in order.</summary>
    public static IReadOnlyList<string> Find(IEnumerable<string> changed) => [.. changed.Where(IsGenerated)];

    public static bool IsGenerated(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;
        var name = segments[^1];
        return segments[..^1].Any(Folders.Contains)
            || Files.Contains(name)
            || Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }
}
