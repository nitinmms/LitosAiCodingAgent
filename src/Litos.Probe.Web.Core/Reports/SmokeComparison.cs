namespace Litos.Probe.Web.Core.Reports;

/// <param name="Report">The current report, its pre-existing failures marked and its outcome recomputed without them.</param>
/// <param name="NewFailures">What this change broke: blamed on the task.</param>
/// <param name="PreExisting">What also fails on the default branch: disclosed, not blamed.</param>
public sealed record ComparedReport(ProbeWebReport Report, IReadOnlyList<CheckResult> NewFailures, IReadOnlyList<CheckResult> PreExisting);

/// <summary>
/// The baseline rule (testability-architecture.md §6.2): a smoke check that also fails on main is
/// pre-existing. A scenario counts only when main ran the very same revision of it: a project's
/// approved scenario that already fails on main is not the task's (W3.6), while a scenario for new
/// behaviour, which main never ran, can never read as "pre-existing" and pass (Probe feasibility §2.1).
/// </summary>
public static class SmokeComparison
{
    public static ComparedReport Compare(ProbeWebReport now, ProbeWebReport? baseline)
    {
        var failingOnMain = baseline?.Checks
            .Where(c => c.Failed)
            .Select(Key)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        var checks = now.Checks
            .Select(c => c.Failed && failingOnMain.Contains(Key(c)) ? c with { PreExisting = true } : c)
            .ToList();

        var report = now with { Checks = checks };
        // A report that never ran its checks (a bad contract) keeps its own outcome.
        if (now.Outcome != ProbeWebOutcome.InvalidContract)
            report = report with { Outcome = ReportOutcome.Compute(checks) };

        return new ComparedReport(
            report,
            [.. checks.Where(c => c.Failed && !c.PreExisting)],
            [.. checks.Where(c => c.PreExisting)]);
    }

    /// <summary>A check's id; a scenario's with its revision, and a scenario without one never matches.</summary>
    private static string Key(CheckResult c) => c.Kind != CheckKind.Scenario
        ? c.Id
        : c.Scenario is { } s ? $"{c.Id}@{s.Revision}" : $"{c.Id}@unmatched:{Guid.NewGuid():N}";
}
