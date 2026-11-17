namespace Litos.Probe.Web.Core.Reports;

/// <param name="Report">The current report, its pre-existing failures marked and its outcome recomputed without them.</param>
/// <param name="NewFailures">What this change broke: blamed on the task.</param>
/// <param name="PreExisting">What also fails on the default branch: disclosed, not blamed.</param>
public sealed record ComparedReport(ProbeWebReport Report, IReadOnlyList<CheckResult> NewFailures, IReadOnlyList<CheckResult> PreExisting);

/// <summary>
/// The baseline rule (testability-architecture.md §6.2): a smoke check that also fails on main is
/// pre-existing. Scenarios are never compared, because a scenario for new behaviour always fails
/// on main and would otherwise read as "pre-existing" and pass (Probe feasibility §2.1).
/// </summary>
public static class SmokeComparison
{
    public static ComparedReport Compare(ProbeWebReport now, ProbeWebReport? baseline)
    {
        var failingOnMain = baseline?.Checks
            .Where(c => c.Failed && c.Kind != CheckKind.Scenario)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        var checks = now.Checks
            .Select(c => c.Failed && c.Kind != CheckKind.Scenario && failingOnMain.Contains(c.Id) ? c with { PreExisting = true } : c)
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
}
