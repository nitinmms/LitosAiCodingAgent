using System.Globalization;
using System.Text;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Verification;

/// <summary>
/// The JSON lines <c>go test -json</c> prints (m3-architecture.md §8): a test's pass, fail or skip
/// is its result, and its output lines are a failure's message. A package that fails with no test
/// failing in it (it did not build, or its TestMain failed) is one failed case of its own, so a
/// broken package is never read as "no failures".
/// </summary>
public static class GoTestReportParser
{
    /// <summary>The most of a failure's output that is kept: the end, where the reason is.</summary>
    private const int MaxMessageChars = 4_000;

    public static IReadOnlyList<TestCaseResult> Parse(string text)
    {
        var results = new List<TestCaseResult>();
        var output = new Dictionary<(string Package, string Test), StringBuilder>();
        var failedPackages = new HashSet<string>(StringComparer.Ordinal);
        var packagesWithFailedTests = new HashSet<string>(StringComparer.Ordinal);
        var sawEvent = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            // go also prints plain lines (downloads, build errors before Go 1.24): not events.
            if (!line.StartsWith('{'))
                continue;

            GoTestEvent? e;
            try
            {
                e = JsonSerializer.Deserialize<GoTestEvent>(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (e?.Action is not { } action)
                continue;
            sawEvent = true;
            var package = e.Package ?? e.ImportPath ?? "";
            var key = (package, e.Test ?? "");

            if (action is "output" or "build-output")
            {
                if (!output.TryGetValue(key, out var builder))
                    output[key] = builder = new StringBuilder();
                builder.Append(e.Output);
                continue;
            }

            if (action is not ("pass" or "fail" or "skip") && action != "build-fail")
                continue;

            if (string.IsNullOrEmpty(e.Test))
            {
                if (action is "fail" or "build-fail")
                    failedPackages.Add(package);
                continue;
            }

            var outcome = action switch { "pass" => TestOutcome.Passed, "skip" => TestOutcome.Skipped, _ => TestOutcome.Failed };
            if (outcome == TestOutcome.Failed)
                packagesWithFailedTests.Add(package);
            results.Add(new TestCaseResult(
                $"{package} :: {e.Test}", outcome, outcome == TestOutcome.Failed ? MessageOf(output, key) : null, e.Elapsed));
        }

        if (!sawEvent && text.Trim().Length > 0)
            throw new ReportFormatException("The go test report has no JSON events: was it written with go test -json?");

        foreach (var package in failedPackages.Where(p => !packagesWithFailedTests.Contains(p)).Order(StringComparer.Ordinal))
            results.Add(new TestCaseResult($"{package} :: (package)", TestOutcome.Failed, MessageOf(output, (package, "")) ?? "The package failed."));

        return results;
    }

    private static string? MessageOf(Dictionary<(string, string), StringBuilder> output, (string, string) key)
    {
        if (!output.TryGetValue(key, out var builder))
            return null;
        var message = builder.ToString().Trim();
        return message.Length <= MaxMessageChars ? message : "…" + message[^MaxMessageChars..];
    }

    private sealed record GoTestEvent(string? Action, string? Package, string? ImportPath, string? Test, string? Output, double? Elapsed);
}

/// <summary>
/// The coverage profile <c>go test -coverprofile</c> writes: <c>mode: set</c>, then one block per
/// line, <c>file:startLine.col,endLine.col statements count</c>. Every line a block spans gets
/// its count, and a line in several blocks the highest. Files are named by import path; with the
/// module's path, from its go.mod, that prefix is taken off, leaving a path in the module.
/// </summary>
public static class GoCoverParser
{
    public static CoverageReport Parse(string text, string? modulePath = null)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new CoverageReport([], []);
        if (!lines[0].StartsWith("mode:", StringComparison.Ordinal))
            throw new ReportFormatException("The Go coverage profile does not start with a 'mode:' line.");

        var prefix = string.IsNullOrEmpty(modulePath) ? null : modulePath.TrimEnd('/') + "/";
        var files = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            // The file name may hold ':' (a Windows path); the block is after the last one.
            var colon = line.LastIndexOf(':');
            var parts = colon < 0 ? [] : line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                continue;
            var range = parts[0].Split(',');
            if (range.Length != 2 || !TryLine(range[0], out var start) || !TryLine(range[1], out var end)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                continue;
            }

            var file = line[..colon];
            if (prefix is not null && file.StartsWith(prefix, StringComparison.Ordinal))
                file = file[prefix.Length..];
            if (!files.TryGetValue(file, out var hits))
                files[file] = hits = [];

            var clamped = (int)Math.Min(count, int.MaxValue);
            for (var number = start; number <= end; number++)
                hits[number] = hits.TryGetValue(number, out var existing) ? Math.Max(existing, clamped) : clamped;
        }

        return new CoverageReport([], [.. files.Select(f => new CoverageFile(f.Key, f.Value))]);
    }

    private static bool TryLine(string position, out int line) =>
        int.TryParse(position.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out line);

    /// <summary>The module path a go.mod declares; null without one.</summary>
    public static string? ModulePathIn(string directory)
    {
        var goMod = Path.Combine(directory, "go.mod");
        if (!File.Exists(goMod))
            return null;
        foreach (var line in File.ReadLines(goMod))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("module ", StringComparison.Ordinal))
                return trimmed["module ".Length..].Trim().Trim('"');
        }

        return null;
    }
}

/// <summary>
/// JaCoCo's XML report: each <c>&lt;sourcefile&gt;</c> under its <c>&lt;package&gt;</c>, each line
/// with its covered (ci) and missed (mi) instructions. A line with any covered instruction ran.
/// Its files are named by package path (com/acme/Order.java), relative to the module's source
/// folders, which are found from where the report is: Maven writes it to
/// <c>&lt;module&gt;/target/site/jacoco/jacoco.xml</c> and Gradle to
/// <c>&lt;module&gt;/build/reports/jacoco/test/jacocoTestReport.xml</c>.
/// </summary>
public static class JacocoParser
{
    private static readonly string[] SourceFolders = ["src/main/java", "src/main/kotlin", "src/main/scala", "src/main/groovy"];

    public static CoverageReport Parse(string xml, string? reportPath = null)
    {
        var document = ReportXml.Load(xml, "JaCoCo");
        if (document.Root?.Name.LocalName != "report")
            throw new ReportFormatException("The JaCoCo report's root element is not <report>.");

        var files = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (var package in document.Descendants().Named("package"))
        {
            var packagePath = package.Attr("name") ?? "";
            foreach (var source in package.Elements().Named("sourcefile"))
            {
                if (source.Attr("name") is not { Length: > 0 } name)
                    continue;
                var path = packagePath.Length == 0 ? name : $"{packagePath}/{name}";
                if (!files.TryGetValue(path, out var hits))
                    files[path] = hits = [];

                foreach (var line in source.Elements().Named("line"))
                {
                    if (!int.TryParse(line.Attr("nr"), out var number) || !int.TryParse(line.Attr("ci"), out var covered))
                        continue;
                    hits[number] = Math.Max(hits.GetValueOrDefault(number), covered);
                }
            }
        }

        return new CoverageReport(ModuleSourceRoots(reportPath), [.. files.Select(f => new CoverageFile(f.Key, f.Value))]);
    }

    /// <summary>The module's source folders, when the report is where a build tool writes it.</summary>
    private static IReadOnlyList<string> ModuleSourceRoots(string? reportPath)
    {
        if (reportPath is null)
            return [];
        var parts = Path.GetFullPath(reportPath).Replace('\\', '/').Split('/');
        var target = Array.FindLastIndex(parts, p => p is "target" or "build");
        if (target < 1)
            return [];
        var module = string.Join('/', parts[..target]);
        return [.. SourceFolders.Select(folder => $"{module}/{folder}")];
    }
}
