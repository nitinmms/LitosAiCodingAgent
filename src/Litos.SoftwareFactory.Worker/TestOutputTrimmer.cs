using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// Cuts the output of a test run to what an agent acts on: its failures and its summary. Every
/// line a tool returns stays in the conversation, and every later model call re-reads it: on R3,
/// test-run output was 27% of the tool output in context, most of it lists of passing tests, and
/// two runs added about 23,000 tokens that the remaining 25 calls each paid for again.
///
/// The cut is made once, as the output is produced, so nothing already in the conversation
/// changes and the provider's prompt cache is unaffected. The whole output is kept in a file
/// outside the working copy, and the agent is told where.
/// </summary>
public static partial class TestOutputTrimmer
{
    /// <summary>Output up to this many characters is left as it is.</summary>
    public const int KeepWholeUpTo = 4_000;

    /// <summary>Lines kept after each line that reports a failure: its message and the start of its stack.</summary>
    public const int FailureContextLines = 6;

    /// <summary>At most this many lines are kept for failures; the summary is kept as well.</summary>
    public const int MaxFailureLines = 150;

    /// <summary>Lines kept from the end, where every runner prints its summary.</summary>
    public const int SummaryLines = 15;

    /// <summary>Whether the command runs a test suite, in any of the common runners.</summary>
    public static bool IsTestRun(string? command) => command is not null && TestCommand().IsMatch(command);

    /// <summary>
    /// The output with only its failures and its summary, or null when it is short enough to keep
    /// whole. <paramref name="fullOutputPath"/> is where the caller saved the whole output.
    /// </summary>
    public static string? Trim(string output, string fullOutputPath)
    {
        if (output.Length <= KeepWholeUpTo)
            return null;

        var lines = output.Replace("\r\n", "\n").Split('\n');
        var keep = new SortedSet<int> { 0 };   // the shell's exit line
        var failureLines = 0;
        for (var i = 1; i < lines.Length && failureLines < MaxFailureLines; i++)
        {
            if (!FailureMarker().IsMatch(lines[i]) || SummaryCount().IsMatch(lines[i]))
                continue;
            for (var j = i; j <= Math.Min(lines.Length - 1, i + FailureContextLines) && failureLines < MaxFailureLines; j++)
            {
                if (keep.Add(j))
                    failureLines++;
            }
        }

        for (var i = Math.Max(1, lines.Length - SummaryLines); i < lines.Length; i++)
            keep.Add(i);

        var text = new StringBuilder();
        text.Append(lines[0]).Append('\n');
        text.Append(string.Create(CultureInfo.InvariantCulture,
            $"[The factory kept the failures and the summary of this test run: {keep.Count} of {lines.Length} lines. "
            + $"The whole output is in {fullOutputPath}; to see more, print part of it from kernel code, for example "
            + $"File.ReadAllLines(@\"{fullOutputPath}\").Skip(100).Take(60).]\n"));
        var previous = 0;
        foreach (var i in keep)
        {
            if (i == 0)
                continue;
            if (i > previous + 1)
                text.Append(string.Create(CultureInfo.InvariantCulture, $"  [... {i - previous - 1} lines ...]\n"));
            text.Append(lines[i]).Append('\n');
            previous = i;
        }

        return text.ToString().TrimEnd('\n');
    }

    // dotnet test, Vitest, Jest, Mocha, npm/pnpm/yarn/bun test, pytest, go test, cargo test, Maven, Gradle.
    [GeneratedRegex(@"\b(dotnet(\.exe)?\s+test|vitest|jest|mocha|(npm|pnpm|yarn|bun)(\s+run)?\s+test|pytest|py\.test|go\s+test|cargo\s+test|mvn\b[^|&;\r\n]*\btest|gradlew?\b[^|&;\r\n]*\btest)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestCommand();

    // A line that reports a failure, an error or an assertion, in the common runners' words.
    [GeneratedRegex(@"\b(FAIL|FAILED|Failed|failed|ERROR|Error|error|Exception|AssertionError|Assert\.\w+|Expected|Received|panicked)\b|[✗×✕]")]
    private static partial Regex FailureMarker();

    // A summary count such as "Failed: 0" or "0 failed", which reports no failure.
    [GeneratedRegex(@"\b(Failed|failed|Errors?|errors?)\s*:?\s*0\b|\b0\s+(failed|errors?)\b")]
    private static partial Regex SummaryCount();
}
