using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// The browser step ran under the project's adopted contract, so a changed litos.test.yaml on
    /// the branch was not obeyed. False for a Make-testable task, which runs its own draft, and
    /// for a project with nothing adopted (finding 17).
    /// </summary>
    public bool UsedAdoptedContract { get; init; }

    /// <summary>Things that went wrong around the handoff itself, such as a draft PR that could
    /// not be opened.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The approved specification revision the run built, if any (m2-architecture.md §5).</summary>
    public int? SpecificationRevision { get; init; }

    /// <summary>That revision's acceptance criteria, each of which the work must report on.</summary>
    public IReadOnlyList<string> ApprovedCriteria { get; init; } = [];

    /// <summary>The approved spec's criteria that no scenario checks, and why (W3.8 finding 2).</summary>
    public IReadOnlyList<NotInBrowser> NotInBrowser { get; init; } = [];

    /// <summary>The repository's page on GitHub, which the tester gets the branch from.</summary>
    public string? RepositoryUrl { get; init; }

    public string? CloneUrl { get; init; }

    /// <summary>The branch the work is compared against.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>How to run the application, from the task's last handoff: a rework that does not
    /// say again keeps it.</summary>
    public RunInstructions? PreviousHowToRun { get; init; }
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
    long? BudgetCap,
    int? SpecificationRevision = null,
    string? RepositoryUrl = null,
    string? CloneUrl = null,
    string? DefaultBranch = null,
    RunInstructions? HowToRun = null,
    Testability.UiTestEvidence? UiTesting = null,
    IReadOnlyList<NotInBrowser>? NotInBrowser = null);

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
            // The scan's assumptions are choices made without asking; the tester sees each one, and
            // each approved criterion the work did not report on.
            KnownLimitations: [.. (submission?.KnownLimitations ?? []).Select(l => Contradicted(l, verification))
                .Concat(state.Assumptions.Select(a => $"Assumed, not asked: {a}"))
                .Concat(Unreported(facts.ApprovedCriteria, submission?.Criteria ?? []).Select(c => $"Approved criterion not reported on: {c}"))
                .Concat(state.Disclosures)
                .Concat(ContractDrift(state, facts.ChangedFiles, facts.UsedAdoptedContract) is { } drift ? [drift] : [])
                .Concat(facts.Notes)],
            ManualTestSteps: submission?.ManualTestSteps ?? [],
            Commands: [.. (verification?.Commands ?? []).Select(c => $"{c.Command} — {(c.Succeeded ? "ok" : c.TimedOut ? "timed out" : $"exit {c.ExitCode}")} in {c.Duration.TotalSeconds:0.#}s")],
            ChangedFiles: facts.ChangedFiles,
            Decisions: state.Decisions,
            TokensUsed: facts.TokensUsed,
            BudgetCap: facts.BudgetCap,
            SpecificationRevision: facts.SpecificationRevision,
            RepositoryUrl: facts.RepositoryUrl,
            CloneUrl: facts.CloneUrl,
            DefaultBranch: facts.DefaultBranch,
            HowToRun: submission?.HowToRun ?? facts.PreviousHowToRun,
            UiTesting: state.UiTestEnabled ? UiTestingAt(state, facts.CommitSha) : Testability.UiTestEvidence.NotSetUp,
            NotInBrowser: state.UiTestEnabled ? facts.NotInBrowser : []);
    }

    /// <summary>
    /// The browser evidence, labelled with the commit it describes (finding 8). The browser step
    /// runs on the uncommitted working copy, so Probe.Web reads the commit before it. While the
    /// result is current nothing has changed since, and the handoff commit is exactly what ran.
    /// </summary>
    private static Testability.UiTestEvidence? UiTestingAt(RunState state, string? handoffCommit) =>
        state is { UiTestCurrent: true, UiTest: { } evidence } && handoffCommit is { Length: > 0 }
            ? evidence with { Commit = handoffCommit }
            : state.UiTest;

    /// <summary>
    /// An agent's limitation that says tests fail, naming tests the factory's own verification ran
    /// green (finding 14), is kept but answered with what the factory measured. A name counts when
    /// it is a test's class or method, as a whole word of eight characters or more.
    /// </summary>
    /// <remarks>
    /// W3.8 finding 9: a name whose tests another named match already covers is left out, so a
    /// class named both in full and by its bare name ("TestDataTests" and "Testing/TestData.cs"), or
    /// a class and its methods, are listed once. A limitation that says coverage was not measured is
    /// answered with the changed-line figure the factory did measure. The review brief shows these
    /// notes too, so a reviewer does not take a stale claim for a defect (finding 8).
    /// </remarks>
    internal static string Contradicted(string limitation, VerificationOutcome? verification)
    {
        if (verification is null)
            return limitation;

        var notes = new List<string>();
        if (verification.Tests.Count > 0 && FailWords.IsMatch(limitation))
        {
            var named = verification.Tests
                .SelectMany(t => NameParts(t.Name).Select(part => (Part: part, Test: t)))
                .Where(p => p.Part.Length >= 8 && Regex.IsMatch(limitation, $@"\b{Regex.Escape(p.Part)}\b"))
                .GroupBy(p => p.Part, p => p.Test)
                .Select(g => (Name: g.Key, Tests: g.Distinct().ToList()))
                .ToList();
            if (named.Count > 0 && named.All(g => g.Tests.All(t => t.Outcome == TestOutcome.Passed)))
            {
                var kept = new List<(string Name, List<TestCaseResult> Tests)>();
                foreach (var g in named.OrderByDescending(g => g.Tests.Count))
                {
                    if (!kept.Any(k => g.Tests.All(k.Tests.Contains)))
                        kept.Add(g);
                }

                var ran = string.Join(" and ", named.Where(kept.Contains).Select(g => $"{g.Name} ({g.Tests.Count} test{(g.Tests.Count == 1 ? "" : "s")})"));
                notes.Add($"its verification ran {ran}, and all passed");
            }
        }

        if (verification.ChangedLines is { } coverage && CoverageWords.IsMatch(limitation) && UnmeasuredWords.IsMatch(limitation))
            notes.Add($"it measured changed-line coverage at {coverage.Percent:0.#}%");

        return notes.Count == 0 ? limitation : $"{limitation.TrimEnd()} (Not what the factory measured: {string.Join("; ", notes)}.)";
    }

    private static readonly Regex FailWords = new(@"\bfail", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CoverageWords = new(@"\bcoverage\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UnmeasuredWords = new(
        @"\b(no|not|never|without|missing|unverified|unmeasured|unknown|could not)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A test's class and method names: "Shop.Tests.CartTests.Adds_an_item(x: 1)" gives CartTests and
    /// Adds_an_item. The class counts without its Tests suffix too, as agents write it: the live
    /// check's limitation named "TestData/TestingEnvironment tests" for TestDataTests and
    /// TestingEnvironmentTests.
    /// </summary>
    private static IEnumerable<string> NameParts(string name)
    {
        var plain = name.Split('(', 2)[0];
        var parts = plain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return parts;
        var type = parts[^2];
        var bare = type.EndsWith("Tests", StringComparison.Ordinal) ? type[..^5] : type.EndsWith("Test", StringComparison.Ordinal) ? type[..^4] : type;
        return bare == type ? [type, parts[^1]] : [type, bare, parts[^1]];
    }

    /// <summary>
    /// The drift note (testability-architecture.md §5): a branch that changes litos.test.yaml is
    /// never obeyed, because only the adopted copy in the profile is used. The tester is told so.
    /// </summary>
    public static string? ContractDrift(RunState state, IReadOnlyList<string> changedFiles, bool usedAdoptedContract)
    {
        if (!changedFiles.Any(f => string.Equals(f.Replace('\\', '/'), "litos.test.yaml", StringComparison.OrdinalIgnoreCase)))
            return null;
        return state.UiTestEnabled && usedAdoptedContract
            ? $"litos.test.yaml changed on this branch. The factory still used the adopted contract{(state.UiTest?.ContractRevision is { } r ? $" (profile revision {r})" : "")}; adopt the new one after merging if you agree."
            : "This branch adds or changes litos.test.yaml. After merging, adopt it on the project's Testability panel so tasks are tested in a browser.";
    }

    /// <summary>
    /// The approved criteria no reported criterion matches. The run is told to report on each in
    /// its own words; case, spacing and a closing full stop are not held against it.
    /// </summary>
    public static IReadOnlyList<string> Unreported(IReadOnlyList<string> approved, IReadOnlyList<CriterionCoverage> reported)
    {
        var said = reported.Select(c => Normal(c.Criterion)).ToHashSet();
        return [.. approved.Where(a => !said.Contains(Normal(a)))];

        static string Normal(string text) =>
            string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.').ToLowerInvariant();
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
            nameof(ReviewStatus.DidNotFinish) => "Agent review: did not finish. ",
            nameof(ReviewStatus.SkippedForBudget) => "Agent review: not run, because no budget was left once verification had passed. ",
            nameof(ReviewStatus.NotNeeded) => "Agent review: not needed for a change this small with no risk signals; the factory's own verification passed. ",
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
