using System.Text.RegularExpressions;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Orchestration;

public enum ReviewDepth
{
    /// <summary>One look at the evidence and the diff: no reading round the repository, no running code.</summary>
    Light,

    /// <summary>The reviewer may read files and run code, within its allowance.</summary>
    Full,
}

/// <param name="Reasons">Why this depth: for a full review, every risk signal found; for a light
/// one, a one-line description of the change.</param>
public sealed record ReviewPlan(ReviewDepth Depth, IReadOnlyList<string> Reasons)
{
    /// <summary>The line the thread shows when the review starts.</summary>
    public string Describe() => Depth == ReviewDepth.Light
        ? $"Review: light. {string.Join(" ", Reasons)}"
        : $"Review: full, because {string.Join("; ", Reasons)}.";
}

/// <summary>What the host knows about a change when its review is about to start.</summary>
/// <param name="Files">The changed files with their added lines (from the diff).</param>
/// <param name="Patch">The unified diff, with zero lines of context.</param>
public sealed record ReviewInputs(
    IReadOnlyList<FileChange> Files,
    string Patch,
    VerificationOutcome? Verification,
    WorkSubmission? Submission);

/// <summary>
/// Chooses how deep a review must go, from the host's own evidence, without a model call
/// (docs/software-factory/m1-architecture.md; ReadMe_CodeVerifyOptimisations.md §9). Review was
/// about a third of every task's tokens in the first real runs, and a small, low-risk change paid
/// the same open-ended review as a change to the file format or the locking. A light review is
/// one look at the evidence; a full review may read and run code.
///
/// The rule leans to full: any one risk signal makes the review full. Signals are deliberately
/// crude — names and keywords, not analysis — because a false "full" costs tokens while a false
/// "light" can let a defect through.
/// </summary>
public static partial class ReviewPlanner
{
    public static ReviewPlan Plan(ReviewInputs inputs, RunLimits limits)
    {
        var reasons = new List<string>();
        var production = inputs.Files.Where(f => !IsTest(f.Path)).ToList();
        var productionLines = production.Sum(f => f.AddedLineCount);
        var removedPublic = RemovedPublicMembers(inputs.Patch);

        if (inputs.Files.Count == 0)
            reasons.Add("the diff could not be read");
        if (productionLines > limits.LightReviewMaxChangedLines)
            reasons.Add($"it changes {productionLines} lines outside tests (light review is for up to {limits.LightReviewMaxChangedLines})");
        if (inputs.Files.Count > limits.LightReviewMaxFiles)
            reasons.Add($"it touches {inputs.Files.Count} files (light review is for up to {limits.LightReviewMaxFiles})");

        foreach (var signal in PathSignals(production.Select(f => f.Path)))
            reasons.Add(signal);
        foreach (var signal in CodeSignals(AddedProductionLines(inputs.Patch)))
            reasons.Add(signal);
        if (removedPublic > 0)
            reasons.Add($"it removes or changes {removedPublic} public declaration{(removedPublic == 1 ? "" : "s")}");

        if (VerificationConcern(inputs.Verification) is { } concern)
            reasons.Add(concern);

        switch (inputs.Submission)
        {
            case null:
                reasons.Add("there is no submission to check the change against");
                break;
            case { Criteria.Count: 0 }:
                reasons.Add("the agent mapped no acceptance criterion to a test");
                break;
            case var submission when submission.Criteria.Count(c => c.Tests.Count == 0 && !c.ManualOnly) is var untested and > 0:
                reasons.Add($"{untested} acceptance criteri{(untested == 1 ? "on has" : "a have")} no test");
                break;
        }

        if (reasons.Count > 0)
            return new ReviewPlan(ReviewDepth.Full, reasons.Distinct().ToList());

        var fileCount = inputs.Files.Count;
        return new ReviewPlan(ReviewDepth.Light,
            [$"A small change ({productionLines} line{(productionLines == 1 ? "" : "s")} outside tests in {fileCount} file{(fileCount == 1 ? "" : "s")}) with a clean verification and no risk signals."]);
    }

    /// <summary>Test code: a defect there fails a test, it does not ship.</summary>
    internal static bool IsTest(string path)
    {
        var p = path.Replace('\\', '/');
        return TestPath().IsMatch(p);
    }

    [GeneratedRegex(@"(^|/)(tests?|__tests__|spec|specs|test-?fixtures?)/|(\.|_|-)(test|tests|spec)\.[a-z0-9]+$|Tests?\.cs$", RegexOptions.IgnoreCase)]
    private static partial Regex TestPath();

    private static readonly (Regex Pattern, string Signal)[] PathRules =
    [
        (new(@"(auth|login|permission|role|password|secret|token|credential|crypt|security)", RegexOptions.IgnoreCase), "it touches authentication or security code"),
        (new(@"(migration|schema|\.sql$)", RegexOptions.IgnoreCase), "it touches a schema or migration"),
        (new(@"(storage|format|serializ|persist|protocol|wire)", RegexOptions.IgnoreCase), "it touches storage or a data format"),
        (new(@"(^|/)(\.github|\.gitlab|ci|build|deploy|infra)/|(\.csproj|\.props|\.targets|\.sln|\.slnx|package\.json|package-lock\.json|global\.json|tsconfig[^/]*\.json|vite\.config\.[a-z]+|dockerfile)$", RegexOptions.IgnoreCase), "it changes build, dependency or deployment configuration"),
    ];

    private static IEnumerable<string> PathSignals(IEnumerable<string> paths)
    {
        var list = paths.Select(p => p.Replace('\\', '/')).ToList();
        foreach (var (pattern, signal) in PathRules)
        {
            if (list.Any(pattern.IsMatch))
                yield return signal;
        }
    }

    private static readonly (Regex Pattern, string Signal)[] CodeRules =
    [
        (new(@"\block\s*\(|\bMonitor\.|\bInterlocked\.|\bSemaphoreSlim\b|\bReaderWriterLock|\bMutex\b|\bvolatile\b|\bConcurrent[A-Z]\w*|\bParallel\.|\bnew Thread\b|\bTask\.Run\b|\bAtomics\.", RegexOptions.None), "it changes locking or concurrency"),
        (new(@"\basync\b|\bawait\b|\bConfigureAwait\b|\bPromise\.(all|race|any)\b", RegexOptions.None), "it adds asynchronous code"),
        (new(@"\bFileStream\b|\bFile\.(Write|Append|Delete|Move|Replace|Open)|\bRandomAccess\.|\bBinaryPrimitives\.|\bfs\.(write|append|unlink|rename|rm)|\blocalStorage\b|\bindexedDB\b", RegexOptions.None), "it changes how data is written or stored"),
        (new(@"\b(Process\.Start|exec\(|spawn\(|eval\(|innerHTML|dangerouslySetInnerHTML)\b", RegexOptions.None), "it runs processes or injects code or markup"),
    ];

    private static IEnumerable<string> CodeSignals(IReadOnlyList<string> addedLines)
    {
        foreach (var (pattern, signal) in CodeRules)
        {
            if (addedLines.Any(pattern.IsMatch))
                yield return signal;
        }
    }

    /// <summary>The added lines of every file that is not a test, from a unified diff.</summary>
    internal static IReadOnlyList<string> AddedProductionLines(string patch) =>
        [.. Lines(patch, '+').Where(l => !IsTest(l.File)).Select(l => l.Text)];

    /// <summary>
    /// Public declarations on removed lines outside tests: a removed or changed signature breaks
    /// callers in ways the change's own tests do not see. Counted per line; a changed signature
    /// is a removal and an addition, and counts once.
    /// </summary>
    internal static int RemovedPublicMembers(string patch) =>
        Lines(patch, '-').Count(l => !IsTest(l.File) && PublicDeclaration().IsMatch(l.Text));

    [GeneratedRegex(@"^\s*(public|protected|export)\s")]
    private static partial Regex PublicDeclaration();

    private static IEnumerable<(string File, string Text)> Lines(string patch, char sign)
    {
        var file = "";
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim();
                file = path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                // On a deleted file the new name is /dev/null; the old one is what was removed.
                var path = line[4..].Trim();
                if (path != "/dev/null")
                    file = path.StartsWith("a/", StringComparison.Ordinal) ? path[2..] : path;
                continue;
            }

            if (line.Length > 0 && line[0] == sign)
                yield return (file, line[1..]);
        }
    }

    private static string? VerificationConcern(VerificationOutcome? verification) => verification switch
    {
        null => "the change has not been verified",
        { Build: not (BuildStatus.Passed or BuildStatus.NotApplicable) } => $"the build is {verification.Build}",
        { UnitTests: not UnitTestStatus.Passed } => $"the unit tests are {verification.UnitTests}",
        { Coverage: CoverageStatus.BelowThreshold or CoverageStatus.Unavailable } => $"changed-line coverage is {verification.Coverage}",
        _ => null,
    };
}

/// <summary>What a review turn may spend before it is asked to finish, and the hard stop after.</summary>
/// <param name="WrapUpAfterCalls">Model calls after which the reviewer is asked to submit what it has.</param>
/// <param name="WrapUpAfterTokens">Tokens after which it is asked the same; null for no token allowance.</param>
/// <param name="MaxToolCalls">Tool calls after which the turn is stopped.</param>
public sealed record TurnAllowance(int WrapUpAfterCalls, long? WrapUpAfterTokens, int MaxToolCalls)
{
    /// <summary>
    /// A review's allowance. A light review is one look, so it is asked to finish almost at once.
    /// A full review may cost up to a share of what the implementation cost, with a floor, so a
    /// cheap implementation still gets a real review; the first real runs had reviews costing as
    /// much as the implementation, and one costing nearly as much as the whole task's cap.
    /// </summary>
    public static TurnAllowance ForReview(ReviewDepth depth, long implementationTokens, RunLimits limits) => depth == ReviewDepth.Light
        ? new TurnAllowance(limits.LightReviewWrapUpCalls, null, limits.LightReviewMaxToolCalls)
        : new TurnAllowance(
            limits.FullReviewWrapUpCalls,
            Math.Max(limits.ReviewAllowanceFloorTokens, (long)Math.Ceiling(implementationTokens * limits.ReviewAllowanceShare)),
            limits.FullReviewMaxToolCalls);

    /// <summary>A reminder to submit, after a review ran out: it gets very little more.</summary>
    public static TurnAllowance ForReviewNudge(RunLimits limits) =>
        new(limits.LightReviewWrapUpCalls, null, limits.LightReviewMaxToolCalls);
}
