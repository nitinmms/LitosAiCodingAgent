using System.Text;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>What the host knows at handoff, from its own records.</summary>
public sealed record HandoffFacts(string Branch, string? CommitSha, int? PullRequestNumber, string? PullRequestUrl)
{
    public double? CoverageThresholdPercent { get; init; }

    public long TokensUsed { get; init; }

    public long? BudgetCap { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    /// <summary>Things that went wrong around the handoff itself, such as a draft PR that could
    /// not be opened.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The handoff as structured evidence, stored with the thread and shown as a card.</summary>
public sealed record HandoffEvidence(
    string Summary,
    string Branch,
    string? CommitSha,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string Build,
    string UnitTests,
    int TestsPassed,
    int TestsFailed,
    int TestsSkipped,
    int NewTests,
    IReadOnlyList<string> PreExistingFailures,
    string Coverage,
    double? ChangedLineCoveragePercent,
    double? CoverageThresholdPercent,
    IReadOnlyList<string> UnmeasuredFiles,
    string Review,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<CriterionCoverage> Criteria,
    IReadOnlyList<string> TestsAdded,
    IReadOnlyList<string> KnownLimitations,
    IReadOnlyList<string> ManualTestSteps,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<AnsweredDecision> Decisions,
    long TokensUsed,
    long? BudgetCap);

/// <summary>
/// Composes the handoff (ReadMe_LitosSoftwareFactory_V1.md §11) deterministically, with no
/// model call: the verification figures come from the host's own evidence, and only the
/// descriptive parts — the summary, the criterion-to-test mapping, the manual steps — come from
/// what the agent submitted. Nothing is labelled as passing that did not run.
/// </summary>
public static class HandoffComposer
{
    public static HandoffEvidence Evidence(RunState state, HandoffFacts facts)
    {
        var verification = state.LastVerification;
        var submission = state.LastSubmission;

        return new HandoffEvidence(
            Summary: submission?.Summary ?? "",
            Branch: facts.Branch,
            CommitSha: facts.CommitSha,
            PullRequestNumber: facts.PullRequestNumber,
            PullRequestUrl: facts.PullRequestUrl,
            Build: (verification?.Build ?? BuildStatus.NotRun).ToString(),
            UnitTests: (verification?.UnitTests ?? UnitTestStatus.NotRun).ToString(),
            TestsPassed: verification?.PassedCount ?? 0,
            TestsFailed: verification?.FailedCount ?? 0,
            TestsSkipped: verification?.SkippedCount ?? 0,
            NewTests: verification?.NewTestCount(state.Baseline) ?? 0,
            PreExistingFailures: [.. (verification?.PreExistingFailures(state.Baseline) ?? []).Select(t => t.Name)],
            Coverage: (verification?.Coverage ?? CoverageStatus.NotMeasured).ToString(),
            ChangedLineCoveragePercent: verification?.ChangedLines?.Percent,
            CoverageThresholdPercent: facts.CoverageThresholdPercent,
            UnmeasuredFiles: verification?.ChangedLines?.UnmeasuredFiles ?? [],
            Review: state.Review.ToString(),
            Findings: state.Findings,
            Criteria: submission?.Criteria ?? [],
            TestsAdded: submission?.TestsAdded ?? [],
            // The scan's assumptions are choices made without asking; the tester sees each one.
            KnownLimitations: [.. (submission?.KnownLimitations ?? []).Concat(state.Assumptions.Select(a => $"Assumed, not asked: {a}")).Concat(state.Disclosures).Concat(facts.Notes)],
            ManualTestSteps: submission?.ManualTestSteps ?? [],
            Commands: [.. (verification?.Commands ?? []).Select(c => $"{c.Command} — {(c.Succeeded ? "ok" : c.TimedOut ? "timed out" : $"exit {c.ExitCode}")} in {c.Duration.TotalSeconds:0.#}s")],
            ChangedFiles: facts.ChangedFiles,
            Decisions: state.Decisions,
            TokensUsed: facts.TokensUsed,
            BudgetCap: facts.BudgetCap);
    }

    /// <summary>The handoff message posted to the thread.</summary>
    public static string Text(HandoffEvidence evidence)
    {
        var text = new StringBuilder("Ready for human testing. ");

        text.Append($"Branch {evidence.Branch} pushed");
        if (evidence.CommitSha is { Length: > 0 } sha)
            text.Append($" (commit {sha[..Math.Min(7, sha.Length)]})");
        if (evidence.PullRequestNumber is { } pr)
            text.Append($", draft PR #{pr}");
        text.Append(". ");

        text.Append(evidence.Build switch
        {
            nameof(BuildStatus.Passed) => "Build passed. ",
            nameof(BuildStatus.NotApplicable) => "Build: not applicable. ",
            var other => $"Build: {Words(other)}. ",
        });

        if (evidence.UnitTests == nameof(UnitTestStatus.Passed))
        {
            text.Append($"{evidence.TestsPassed} unit {(evidence.TestsPassed == 1 ? "test" : "tests")} passed");
            if (evidence.NewTests > 0)
                text.Append($" ({evidence.NewTests} new)");
            text.Append(". ");
        }
        else
        {
            text.Append($"Unit tests: {Words(evidence.UnitTests)}");
            if (evidence.TestsPassed + evidence.TestsFailed > 0)
                text.Append($" ({evidence.TestsPassed} passed, {evidence.TestsFailed} failed)");
            text.Append(". ");
        }

        if (evidence.PreExistingFailures.Count > 0)
            text.Append($"{evidence.PreExistingFailures.Count} failing {(evidence.PreExistingFailures.Count == 1 ? "test was" : "tests were")} already failing before this task. ");

        if (evidence.ChangedLineCoveragePercent is { } percent)
        {
            text.Append($"Changed-line coverage {percent:0.#}%");
            if (evidence.CoverageThresholdPercent is { } threshold)
                text.Append($" (threshold {threshold:0.#}%)");
            text.Append(". ");
        }
        else
        {
            text.Append($"Changed-line coverage: {Words(evidence.Coverage)}. ");
        }

        if (evidence.UnmeasuredFiles.Count > 0)
            text.Append($"Not measured for coverage: {string.Join(", ", evidence.UnmeasuredFiles)}. ");

        var blocking = evidence.Findings.Count(f => f.Severity == FindingSeverity.Blocking);
        var minor = evidence.Findings.Count - blocking;
        text.Append(evidence.Review switch
        {
            nameof(ReviewStatus.Clean) => "Agent review: clean. ",
            nameof(ReviewStatus.FindingsFixed) when minor > 0 => $"Agent review: {Count(blocking, "finding")} fixed, {Count(minor, "minor finding")} open. ",
            nameof(ReviewStatus.FindingsFixed) => $"Agent review: {Count(blocking, "finding")} fixed. ",
            nameof(ReviewStatus.FindingsOpen) => $"Agent review: {Count(evidence.Findings.Count, "finding")} open. ",
            _ => "Agent review: not run. ",
        });

        if (evidence.KnownLimitations.Count > 0)
            text.Append("Known limitations: ").Append(string.Join(" ", evidence.KnownLimitations.Select(l => l.TrimEnd('.') + "."))).Append(' ');

        text.Append(evidence.ManualTestSteps.Count > 0
            ? "Please check: " + string.Join("; ", evidence.ManualTestSteps.Select(s => s.TrimEnd('.'))) + "."
            : "Application testing is yours.");

        return text.ToString().TrimEnd();
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>"BelowThreshold" → "below threshold".</summary>
    private static string Words(string pascal)
    {
        var words = new StringBuilder();
        foreach (var c in pascal)
        {
            if (char.IsUpper(c) && words.Length > 0)
                words.Append(' ');
            words.Append(char.ToLowerInvariant(c));
        }

        return words.ToString();
    }
}
