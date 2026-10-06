namespace Litos.SoftwareFactory.Core.Verification;

// Verification is tracked separately from lifecycle (ReadMe_LitosSoftwareFactory_V1.md §7). A
// result may be handed off with disclosed limitations, but nothing is ever labelled as passing
// something that did not run — hence a distinct "did not run" member on every enum.

public enum BuildStatus
{
    NotRun,
    Passed,
    Failed,

    /// <summary>The toolchain could not be started (missing executable).</summary>
    Unavailable,

    /// <summary>The profile declares no build; the handoff says "Build: not applicable".</summary>
    NotApplicable,
}

public enum UnitTestStatus
{
    NotRun,
    Passed,
    Failed,
    NoTests,
    Unavailable,
}

public enum CoverageStatus
{
    NotMeasured,
    Met,
    BelowThreshold,
    Unavailable,
}

public enum ReviewStatus
{
    NotRun,
    Clean,
    FindingsFixed,
    FindingsOpen,

    /// <summary>The review planner judged the change too small and safe to need one.</summary>
    NotNeeded,
}

public enum HumanTestingStatus
{
    NotStarted,
    Passed,
    Failed,
}

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>One test from a structured report. Name is the fully qualified name the report gives.</summary>
public sealed record TestCaseResult(string Name, TestOutcome Outcome, string? Message = null, double? DurationSeconds = null);

/// <summary>One command the verifier ran, as evidence for the handoff.</summary>
public sealed record CommandRun(
    string Step, string Label, string Command, int? ExitCode, TimeSpan Duration, bool TimedOut, string? LogPath = null)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>A run of lines in one file. Start and End are 1-based and inclusive.</summary>
public sealed record LineRange(int Start, int End)
{
    public int Count => End - Start + 1;
}

public sealed record UncoveredLines(string File, IReadOnlyList<int> Lines);

/// <summary>
/// Coverage of the lines the task changed: Covered of Measurable changed lines were executed by
/// the unit tests. Lines the coverage report says nothing about (comments, blank lines, files
/// outside its scope) are not measurable and count neither way.
/// </summary>
public sealed record ChangedLineCoverage(int Covered, int Measurable, IReadOnlyList<UncoveredLines> Uncovered)
{
    /// <summary>100 when nothing measurable changed — there is nothing the tests failed to reach.</summary>
    public double Percent => Measurable == 0 ? 100 : 100.0 * Covered / Measurable;

    /// <summary>
    /// Changed files the coverage report does not mention at all. Their lines count neither way,
    /// which is right for tests, configuration and documentation — but a new source file no test
    /// loaded looks the same, so they are listed for the handoff rather than hidden behind the
    /// percentage.
    /// </summary>
    public IReadOnlyList<string> UnmeasuredFiles { get; init; } = [];
}

/// <summary>
/// What one verification run established. Every figure comes from a structured report or an
/// exit code, never from console text or from the agent.
/// </summary>
public sealed record VerificationOutcome(
    BuildStatus Build,
    UnitTestStatus UnitTests,
    CoverageStatus Coverage,
    IReadOnlyList<TestCaseResult> Tests,
    ChangedLineCoverage? ChangedLines,
    IReadOnlyList<CommandRun> Commands,
    IReadOnlyList<string> Problems)
{
    public int PassedCount => Tests.Count(t => t.Outcome == TestOutcome.Passed);

    public int FailedCount => Tests.Count(t => t.Outcome == TestOutcome.Failed);

    public int SkippedCount => Tests.Count(t => t.Outcome == TestOutcome.Skipped);

    public IReadOnlySet<string> FailingTestNames => Tests.Where(t => t.Outcome == TestOutcome.Failed).Select(t => t.Name).ToHashSet();

    /// <summary>
    /// Failing tests that did not already fail on the base commit. A test that also fails on the
    /// baseline is pre-existing and is reported as such, not blamed on the task (§8.5).
    /// </summary>
    public IReadOnlyList<TestCaseResult> NewFailures(VerificationOutcome? baseline)
    {
        var preExisting = baseline?.FailingTestNames ?? new HashSet<string>();
        return [.. Tests.Where(t => t.Outcome == TestOutcome.Failed && !preExisting.Contains(t.Name))];
    }

    public IReadOnlyList<TestCaseResult> PreExistingFailures(VerificationOutcome? baseline)
    {
        var preExisting = baseline?.FailingTestNames ?? new HashSet<string>();
        return [.. Tests.Where(t => t.Outcome == TestOutcome.Failed && preExisting.Contains(t.Name))];
    }

    /// <summary>Tests in this run that the baseline did not have — "N new" in the handoff.</summary>
    public int NewTestCount(VerificationOutcome? baseline)
    {
        if (baseline is null)
            return 0;
        var known = baseline.Tests.Select(t => t.Name).ToHashSet();
        return Tests.Count(t => !known.Contains(t.Name));
    }
}
