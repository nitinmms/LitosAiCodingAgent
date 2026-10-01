using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Verification;

/// <summary>A report file exists but is not a report of the declared format.</summary>
public sealed class ReportFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One source file in a coverage report: line number → how many times it ran.</summary>
/// <param name="InRepository">True once the path has been resolved to a repository-relative
/// path; false while it is still as the report wrote it.</param>
public sealed record CoverageFile(string Path, IReadOnlyDictionary<int, int> LineHits, bool InRepository = false);

/// <summary>SourceRoots are the directories a report's relative file paths are relative to.</summary>
public sealed record CoverageReport(IReadOnlyList<string> SourceRoots, IReadOnlyList<CoverageFile> Files);

internal static class ReportXml
{
    /// <summary>Loads report XML without resolving anything external. Cobertura reports carry a
    /// DOCTYPE pointing at a DTD on the web; it is ignored, never fetched.</summary>
    public static XDocument Load(string xml, string format)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿')), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ReportFormatException($"The {format} report is not well-formed XML: {ex.Message}", ex);
        }
    }

    public static IEnumerable<XElement> Named(this IEnumerable<XElement> elements, string localName) =>
        elements.Where(e => e.Name.LocalName == localName);

    public static string? Attr(this XElement element, string name) => element.Attribute(name)?.Value;
}

/// <summary>JUnit XML, which most test runners in most languages can emit.</summary>
public static class JUnitReportParser
{
    public static IReadOnlyList<TestCaseResult> Parse(string xml)
    {
        var document = ReportXml.Load(xml, "JUnit");
        if (document.Root?.Name.LocalName is not ("testsuites" or "testsuite"))
            throw new ReportFormatException("The JUnit report's root element is neither <testsuites> nor <testsuite>.");

        return [.. document.Descendants().Named("testcase").Select(ToResult)];
    }

    private static TestCaseResult ToResult(XElement testCase)
    {
        var className = testCase.Attr("classname");
        var name = testCase.Attr("name") ?? "(unnamed)";
        var fullName = string.IsNullOrEmpty(className) ? name : $"{className} :: {name}";

        double? duration = double.TryParse(testCase.Attr("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

        // <error> is an unexpected exception and <failure> a failed assertion; both are a failed test.
        var problem = testCase.Elements().FirstOrDefault(e => e.Name.LocalName is "failure" or "error");
        if (problem is not null)
            return new TestCaseResult(fullName, TestOutcome.Failed, FailureMessage(problem), duration);

        return testCase.Elements().Named("skipped").Any()
            ? new TestCaseResult(fullName, TestOutcome.Skipped, null, duration)
            : new TestCaseResult(fullName, TestOutcome.Passed, null, duration);
    }

    private static string FailureMessage(XElement problem)
    {
        var message = problem.Attr("message")?.Trim() ?? "";
        var body = problem.Value.Trim();
        if (body.Length == 0 || body == message)
            return message;
        return message.Length == 0 || body.Contains(message, StringComparison.Ordinal) ? body : $"{message}\n{body}";
    }
}

/// <summary>Visual Studio TRX, as written by `dotnet test --logger trx`.</summary>
public static class TrxReportParser
{
    public static IReadOnlyList<TestCaseResult> Parse(string xml)
    {
        var document = ReportXml.Load(xml, "TRX");
        if (document.Root?.Name.LocalName != "TestRun")
            throw new ReportFormatException("The TRX report's root element is not <TestRun>.");

        return [.. document.Descendants().Named("UnitTestResult").Select(ToResult)];
    }

    private static TestCaseResult ToResult(XElement result)
    {
        var name = result.Attr("testName") ?? "(unnamed)";
        double? duration = TimeSpan.TryParse(result.Attr("duration"), CultureInfo.InvariantCulture, out var span)
            ? span.TotalSeconds
            : null;

        var outcome = result.Attr("outcome") switch
        {
            "Passed" or "PassedButRunAborted" => TestOutcome.Passed,
            "NotExecuted" or "NotRunnable" or "Pending" or "Disconnected" => TestOutcome.Skipped,
            // Failed, Error, Timeout, Aborted, Inconclusive and anything unrecognised: a test
            // whose result is not known to be a pass is never counted as one.
            _ => TestOutcome.Failed,
        };

        string? message = null;
        if (outcome == TestOutcome.Failed)
        {
            var errorInfo = result.Descendants().Named("ErrorInfo").FirstOrDefault();
            var text = errorInfo?.Elements().Named("Message").FirstOrDefault()?.Value.Trim();
            var stack = errorInfo?.Elements().Named("StackTrace").FirstOrDefault()?.Value.Trim();
            message = string.Join("\n", new[] { text, stack }.Where(s => !string.IsNullOrEmpty(s)));
            if (message.Length == 0)
                message = $"Outcome: {result.Attr("outcome") ?? "unknown"}";
        }

        return new TestCaseResult(name, outcome, message, duration);
    }
}

/// <summary>Cobertura XML, as written by coverlet, Vitest/Istanbul, coverage.py and others.</summary>
public static class CoberturaParser
{
    public static CoverageReport Parse(string xml)
    {
        var document = ReportXml.Load(xml, "Cobertura");
        if (document.Root?.Name.LocalName != "coverage")
            throw new ReportFormatException("The Cobertura report's root element is not <coverage>.");

        var roots = document.Root.Elements().Named("sources").Elements().Named("source")
            .Select(s => s.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // One file can appear as several <class> elements (nested and generic types, lambdas):
        // merge them per file.
        var files = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in document.Descendants().Named("class"))
        {
            if (type.Attr("filename") is not { Length: > 0 } fileName)
                continue;

            if (!files.TryGetValue(fileName, out var hits))
                files[fileName] = hits = [];

            // The class's own <lines> is the report's line list and what its totals count.
            // Method-level lines repeat it in coverlet's reports, but in Istanbul's they are
            // function *declaration* lines that the class list leaves out, so they are read only
            // when a class has no list of its own.
            var classLines = type.Elements().Named("lines").Elements().Named("line").ToList();
            foreach (var line in classLines.Count > 0 ? classLines : type.Descendants().Named("line"))
            {
                if (!int.TryParse(line.Attr("number"), out var number) || !long.TryParse(line.Attr("hits"), out var count))
                    continue;
                var clamped = (int)Math.Min(count, int.MaxValue);
                hits[number] = hits.TryGetValue(number, out var existing) ? Math.Max(existing, clamped) : clamped;
            }
        }

        return new CoverageReport(roots, [.. files.Select(f => new CoverageFile(f.Key, f.Value))]);
    }
}

/// <summary>LCOV tracefiles, as written by Istanbul/Vitest, c8, lcov and others.</summary>
public static class LcovParser
{
    public static CoverageReport Parse(string text)
    {
        var files = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int>? current = null;
        var sawRecord = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                sawRecord = true;
                var path = line[3..].Trim();
                if (!files.TryGetValue(path, out current))
                    files[path] = current = [];
            }
            else if (line.StartsWith("DA:", StringComparison.Ordinal) && current is not null)
            {
                // DA:<line>,<hits>[,<checksum>]
                var parts = line[3..].Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var number) && long.TryParse(parts[1], out var count))
                {
                    var clamped = (int)Math.Min(count, int.MaxValue);
                    current[number] = current.TryGetValue(number, out var existing) ? Math.Max(existing, clamped) : clamped;
                }
            }
            else if (line == "end_of_record")
            {
                current = null;
            }
        }

        if (!sawRecord && text.Trim().Length > 0)
            throw new ReportFormatException("The LCOV report has no SF: records.");

        return new CoverageReport([], [.. files.Select(f => new CoverageFile(f.Key, f.Value))]);
    }
}
