using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Briefs;

/// <summary>
/// The fixed inputs of a run: what was asked, on which branch, under which rules. Composed by
/// the host at preflight from its own records; the briefs are rendered from this and the
/// run's state.
/// </summary>
public sealed record RunContext(string Project, string Branch, string BaseBranch, string Request)
{
    public string? SpecificationSummary { get; init; }

    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];

    /// <summary>Approved project lessons included in this run (empty until lessons arrive in M4).</summary>
    public IReadOnlyList<string> Lessons { get; init; } = [];

    /// <summary>The commands the factory will run, for the agent's information.</summary>
    public string VerificationSummary { get; init; } = "";

    public double? CoverageThresholdPercent { get; init; }

    // Rework runs.
    public string? TesterFeedback { get; init; }

    public string? PreviousHandoffSummary { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    // Review turns.
    public string? Diff { get; init; }

    /// <summary>How deep this review goes, chosen by ReviewPlanner; Full when not set.</summary>
    public ReviewDepth ReviewDepth { get; init; } = ReviewDepth.Full;

    /// <summary>
    /// For the review of a rework run: the commit that was last reviewed and handed off. The
    /// diff is then only what changed since, so the reviewer does not review the whole task
    /// again. Null for a first run, whose diff is against the base branch.
    /// </summary>
    public string? ReviewedThroughCommit { get; init; }

    public int ChangedLineCount { get; init; }
}

/// <summary>
/// Renders each turn's prompt from a versioned template. The templates are embedded resources,
/// and every run records <see cref="Revision"/>, so an evaluation result can be tied to the
/// exact prompts that produced it.
/// </summary>
public static partial class BriefComposer
{
    /// <summary>Bump whenever any template or any text composed here changes.</summary>
    public const string Revision = "m1.10";

    private const int CharsPerToken = 4;

    public static string Compose(StartTurnStep step, RunContext context, RunState state, RunLimits limits) => step.Brief switch
    {
        BriefKind.Run => Run(context, state),
        BriefKind.Rework => Rework(context, state),
        BriefKind.Repair => Repair(context, state, limits),
        BriefKind.Review => Review(context, state, limits),
        BriefKind.Nudge => Nudge(state),
        BriefKind.DecisionAnswer => DecisionAnswer(state),
        BriefKind.ProceedOnRecommendation => Render("proceed", new() { ["maxDecisions"] = limits.MaxDecisions.ToString() }),
        BriefKind.Resume => Resume(state),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.Brief, "Unknown brief kind."),
    };

    /// <summary>The instruction for compaction before a large turn (§8.6): what must survive.</summary>
    public static string CompactionInstruction() => Render("compaction", []);

    private static string Run(RunContext context, RunState state) => Render("run", new()
    {
        ["project"] = context.Project,
        ["branch"] = context.Branch,
        ["baseBranch"] = context.BaseBranch,
        ["request"] = context.Request.Trim(),
        ["specification"] = Specification(context),
        ["decisions"] = Decisions(state),
        ["lessons"] = Section("Project lessons (guidance from earlier accepted work)", Bullets(context.Lessons)),
        ["verification"] = VerificationSummary(context),
        ["baseline"] = BaselineFailures(state),
    });

    private static string Rework(RunContext context, RunState state) => Render("rework", new()
    {
        ["project"] = context.Project,
        ["branch"] = context.Branch,
        ["baseBranch"] = context.BaseBranch,
        ["feedback"] = OrNone(context.TesterFeedback),
        ["previousHandoff"] = OrNone(context.PreviousHandoffSummary),
        ["contract"] = Contract,
        ["changedFiles"] = context.ChangedFiles.Count == 0 ? "(none)" : Bullets(context.ChangedFiles.Select(f => $"`{f}`")),
        ["decisions"] = Decisions(state),
    });

    private static string Repair(RunContext context, RunState state, RunLimits limits)
    {
        var verification = state.LastVerification;
        var failing = verification is null || state.ReviewRepairPending ? [] : FailuresToRepair(verification, state.Baseline);
        var buildFailed = verification?.Build == BuildStatus.Failed && !state.ReviewRepairPending;
        var noReport = verification is { UnitTests: UnitTestStatus.Failed, FailedCount: 0 } && !state.ReviewRepairPending;

        var failures = new StringBuilder();
        if (buildFailed)
            failures.AppendLine("The build failed. Run the build command yourself to see the errors.").AppendLine();
        if (noReport)
        {
            // The command failed, yet its report names no failing test: it crashed, timed out, or
            // wrote no report at all. The verifier's own findings are all there is to go on.
            failures.AppendLine("A unit-test command failed, but no failing test could be read from its report. Make the test command run to completion.").AppendLine();
            foreach (var problem in verification!.Problems)
                failures.AppendLine($"- {problem}");
            if (verification.Problems.Count > 0)
                failures.AppendLine();
        }
        if (failing.Count > 0)
        {
            failures.AppendLine($"## Failing tests ({failing.Count})").AppendLine();
            foreach (var (name, excerpt) in BoundExcerpts(failing, limits.RepairExcerptTokens * CharsPerToken))
            {
                failures.AppendLine($"### `{name}`").AppendLine();
                if (excerpt.Length > 0)
                    failures.AppendLine("```text").AppendLine(excerpt).AppendLine("```").AppendLine();
            }
        }

        var blocking = state.ReviewRepairPending
            ? state.Findings.Where(f => f.Severity == FindingSeverity.Blocking).ToList()
            : [];

        return Render("repair", new()
        {
            ["contract"] = Contract,
            ["cycle"] = state.RepairCyclesUsed.ToString(),
            ["maxCycles"] = limits.MaxRepairCycles.ToString(),
            ["failingTests"] = failures.ToString().TrimEnd(),
            ["coverage"] = state.ReviewRepairPending ? "" : Coverage(context, verification),
            ["findings"] = Section("Blocking review findings", Bullets(blocking.Select(FormatFinding))),
            ["note"] = state.RepairCyclesUsed >= limits.MaxRepairCycles
                ? "This is the last repair cycle. If verification still fails afterwards, the run stops as blocked."
                : "",
        });
    }

    private static string Review(RunContext context, RunState state, RunLimits limits)
    {
        // Above the inline limit the reviewer reads the files itself, which keeps the review
        // turn's context small however large the change is.
        var rework = context.ReviewedThroughCommit is { Length: > 0 } commit ? commit[..Math.Min(7, commit.Length)] : null;
        var comparedWith = rework is null ? $"`{context.BaseBranch}`" : $"commit `{rework}`";
        var change = context.Diff is { Length: > 0 } diff && context.ChangedLineCount <= limits.InlineReviewMaxChangedLines
            ? "```diff\n" + diff.TrimEnd() + "\n```"
            : $"The change is {context.ChangedLineCount} lines, too large to include here. Read these files and compare them with {comparedWith}:\n\n"
              + (context.ChangedFiles.Count == 0 ? "(none)" : Bullets(context.ChangedFiles.Select(f => $"`{f}`")));

        // A rework is reviewed on its own: what was handed off before has been reviewed already.
        var scope = rework is null
            ? ""
            : "## Scope: a rework\n\n"
              + $"This task was reviewed and handed off at commit `{rework}`. The tester then asked for changes, and the change shown below is only what was done since, in answer to this:\n\n"
              + "> " + Quote(context.TesterFeedback ?? "") + "\n\n"
              + "Review that rework, and whether it does what the tester asked. Raise something in the earlier work only if the rework breaks it.";

        return Render(context.ReviewDepth == ReviewDepth.Light ? "review-light" : "review", new()
        {
            ["project"] = context.Project,
            ["branch"] = context.Branch,
            ["baseBranch"] = context.BaseBranch,
            ["request"] = context.Request.Trim(),
            ["specification"] = Specification(context),
            ["verification"] = VerificationResult(state),
            ["claims"] = Claims(state.LastSubmission),
            ["change"] = change,
            ["scope"] = scope,
        });
    }

    /// <summary>
    /// What the implementing agent said it did (ReadMe_CodeVerifyOptimisations.md §2), for the
    /// reviewer to check rather than rediscover. Labelled as claims: the verification result is
    /// the host's measurement, and this is only the agent's account.
    /// </summary>
    private static string Claims(WorkSubmission? submission)
    {
        if (submission is null)
            return "";

        var text = new StringBuilder("## The implementing agent's account (claims to check, not facts)\n\n");
        if (!string.IsNullOrWhiteSpace(submission.Summary))
            text.AppendLine(submission.Summary.Trim()).AppendLine();

        if (submission.Criteria.Count > 0)
        {
            text.AppendLine("Acceptance criteria and the tests it says cover them:").AppendLine();
            foreach (var c in submission.Criteria)
            {
                var covered = c.Tests.Count > 0 ? string.Join(", ", c.Tests.Select(t => $"`{t}`")) : c.ManualOnly ? "manual testing only" : "**no test named**";
                text.AppendLine($"- {c.Criterion.Trim()} — {covered}");
            }

            text.AppendLine();
        }

        if (submission.KnownLimitations.Count > 0)
            text.AppendLine("Known limitations it reports:").AppendLine().AppendLine(Bullets(submission.KnownLimitations.Select(l => l.Trim()))).AppendLine();

        text.Append("Check these against the change. A criterion it calls covered may not be, and a limitation it reports may break the request.");
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The execution contract, restated in every brief that can follow a compaction: the summary
    /// that replaces the conversation keeps the request and the decisions, not the rules.
    /// </summary>
    private const string Contract =
        """
        The execution contract still applies:

        - Stay within what is asked. Write or update unit tests for every behaviour you change.
        - Do not commit, push, merge, switch branches or rewrite history. The factory commits and pushes at handoff.
        - Do not delete or weaken existing tests, and do not change the verification configuration to make your work pass.
        - Do not start the application, deploy anything, or run migrations or other commands against a database.
        - Work only inside the working directory: other directories on this machine, including other tasks' working copies, are not yours to read or change.
        - Never stop processes by name (`taskkill /IM`, `Stop-Process -Name`, `pkill`, `killall`): that stops the factory itself and other programs on this machine. Stop only a process you started, by its id.
        - If a test run hangs or times out, one of the tests is hanging, most likely on code you changed; the test output names it. Find and fix the cause. The test runner works in this environment, so do not build a way around it.
        - Work economically: every call re-sends this whole conversation and everything printed in it. Find code with `search_code`, then read only the lines you need (`read_file` takes `offset` and `limit`) rather than whole files; print only the summary and the failures of a build or test run; do several things in one script, and make related edits together.
        - Every call to a completion tool is acted on at once: never call one to test it or to find out its parameters. From kernel code, pass their arguments as name and value pairs (`submit_work("summary", "...", "testsAdded", new[] { "..." })`); named C# arguments do not compile.
        - `submit_work` takes `summary`; `criteria`, a list of objects, each `new { criterion = "...", tests = new[] { "TestName" } }` or `new { criterion = "...", manualOnly = true }`, never plain strings; and `testsAdded`, `knownLimitations` and `manualTestSteps`, each a list of strings.
        """;

    private static string Nudge(RunState state)
    {
        var review = state.WorkTurn == TurnKind.Review;
        return Render("nudge", new()
        {
            ["expectedTools"] = review ? "`submit_review`" : "`submit_work` or `request_decision`",
            ["completionTool"] = review ? "`submit_review`" : "`submit_work`",
            ["decisionHint"] = review ? "" : "If you are blocked on a choice only a person can make, call `request_decision`. ",
        });
    }

    private static string DecisionAnswer(RunState state)
    {
        var latest = state.Decisions.LastOrDefault()
            ?? throw new InvalidOperationException("A decision-answer brief needs an answered decision.");
        return Render("decision-answer", new() { ["question"] = Quote(latest.Question), ["answer"] = Quote(latest.Answer) });
    }

    private static string Resume(RunState state) => Render("resume", new()
    {
        ["reason"] = state.LastStop is { } stop ? $"It had stopped because: {stop.Message}" : "",
        ["completionTool"] = state.WorkTurn == TurnKind.Review ? "`submit_review`" : "`submit_work`",
    });

    // ---- Sections ----

    private static string Specification(RunContext context)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context.SpecificationSummary))
            text.AppendLine("## Approved specification").AppendLine().AppendLine(context.SpecificationSummary.Trim()).AppendLine();
        if (context.AcceptanceCriteria.Count > 0)
        {
            text.AppendLine("## Acceptance criteria").AppendLine();
            for (var i = 0; i < context.AcceptanceCriteria.Count; i++)
                text.AppendLine($"{i + 1}. {context.AcceptanceCriteria[i]}");
        }

        return text.ToString().TrimEnd();
    }

    private static string Decisions(RunState state) => Section(
        "Decisions already made",
        Bullets(state.Decisions.Select(d => $"**{d.Question.Trim()}** — {d.Answer.Trim()}")));

    private static string VerificationSummary(RunContext context)
    {
        var summary = string.IsNullOrWhiteSpace(context.VerificationSummary) ? "(not configured)" : context.VerificationSummary.Trim();
        return context.CoverageThresholdPercent is { } threshold
            ? $"{summary}\n\nThe lines you change must be at least {threshold:0.#}% covered by unit tests."
            : summary;
    }

    private static string BaselineFailures(RunState state)
    {
        var failing = state.Baseline?.FailingTestNames ?? new HashSet<string>();
        return Section(
            "Tests already failing before this task (not yours to fix unless the request says so)",
            Bullets(failing.Order(StringComparer.Ordinal).Select(n => $"`{n}`")));
    }

    private static string Coverage(RunContext context, VerificationOutcome? verification)
    {
        if (verification is not { Coverage: CoverageStatus.BelowThreshold, ChangedLines: { } changed })
            return "";

        var text = new StringBuilder("## Changed-line coverage\n\n");
        text.Append($"{changed.Percent:0.#}% of the lines you changed are covered by unit tests");
        if (context.CoverageThresholdPercent is { } threshold)
            text.Append($"; the project requires {threshold:0.#}%");
        text.AppendLine(". Add tests that exercise these lines:").AppendLine();
        foreach (var file in changed.Uncovered)
            text.AppendLine($"- `{file.File}`: lines {FormatLines(file.Lines)}");
        return text.ToString().TrimEnd();
    }

    private static string VerificationResult(RunState state)
    {
        if (state.LastVerification is not { } v)
            return "(not run)";

        var lines = new List<string>
        {
            $"- Build: {v.Build}",
            $"- Unit tests: {v.UnitTests} ({v.PassedCount} passed, {v.FailedCount} failed, {v.SkippedCount} skipped)",
            v.ChangedLines is { } changed
                ? $"- Changed-line coverage: {v.Coverage} ({changed.Percent:0.#}%)"
                : $"- Changed-line coverage: {v.Coverage}",
        };
        return string.Join('\n', lines);
    }

    private static List<TestCaseResult> FailuresToRepair(VerificationOutcome verification, VerificationOutcome? baseline) =>
        [.. verification.NewFailures(baseline).OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Bounds the failure excerpts to a total size, sharing it evenly so one enormous stack trace
    /// cannot crowd out the rest. Every failing test keeps its name even when its excerpt is cut
    /// to nothing.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Excerpt)> BoundExcerpts(IReadOnlyList<TestCaseResult> failing, int totalChars)
    {
        if (failing.Count == 0)
            return [];

        var share = Math.Max(0, totalChars / failing.Count);
        return [.. failing.Select(t =>
        {
            var message = (t.Message ?? "").Trim();
            return (t.Name, message.Length <= share ? message : message[..share].TrimEnd() + (share > 0 ? "\n[truncated]" : ""));
        })];
    }

    internal static string FormatLines(IReadOnlyList<int> lines)
    {
        var ranges = new List<string>();
        var ordered = lines.Distinct().Order().ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var start = ordered[i];
            while (i + 1 < ordered.Count && ordered[i + 1] == ordered[i] + 1)
                i++;
            ranges.Add(start == ordered[i] ? $"{start}" : $"{start}-{ordered[i]}");
        }

        return string.Join(", ", ranges);
    }

    private static string FormatFinding(ReviewFinding finding) =>
        finding.Line is { } line ? $"`{finding.File}:{line}` — {finding.Text.Trim()}" : $"`{finding.File}` — {finding.Text.Trim()}";

    private static string Section(string heading, string body) => body.Length == 0 ? "" : $"## {heading}\n\n{body}";

    private static string Bullets(IEnumerable<string> items) => string.Join('\n', items.Select(i => $"- {i}"));

    private static string OrNone(string? text) => string.IsNullOrWhiteSpace(text) ? "(none)" : text.Trim();

    /// <summary>Keeps a multi-line question or answer inside the template's block quote.</summary>
    private static string Quote(string text) => text.Trim().ReplaceLineEndings("\n").Replace("\n", "\n> ");

    // ---- Templates ----

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    internal static string LoadTemplate(string name)
    {
        var assembly = typeof(BriefComposer).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith($".Briefs.Templates.{name}.md", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Brief template '{name}' is not embedded in {assembly.GetName().Name}.");
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Fills a template. A placeholder with no value is an error, and so is a value with no
    /// placeholder: either means a template and this code have drifted apart. Substitution is a
    /// single pass, so braces inside a value (a request quoting code, a diff) are never expanded.
    /// </summary>
    internal static string Render(string name, Dictionary<string, string> values)
    {
        var template = LoadTemplate(name);
        var used = new HashSet<string>();
        var rendered = Placeholder().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
                throw new InvalidOperationException($"Brief template '{name}' uses {{{{{key}}}}}, which was not supplied.");
            used.Add(key);
            return value;
        });

        var unused = values.Keys.Except(used).ToList();
        if (unused.Count > 0)
            throw new InvalidOperationException($"Brief template '{name}' has no placeholder for: {string.Join(", ", unused)}.");

        // An empty optional section leaves a run of blank lines behind; collapse those.
        return BlankRuns().Replace(rendered, "\n\n").Trim() + "\n";
    }
}
