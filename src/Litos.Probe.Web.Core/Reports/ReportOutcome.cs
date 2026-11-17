namespace Litos.Probe.Web.Core.Reports;

/// <summary>A report's overall outcome, from its checks alone.</summary>
public static class ReportOutcome
{
    /// <summary>
    /// An environment failure outranks a defect, because once the app can't start or reset nothing
    /// after it means anything. A flaky check passed in the end. With no check run or passed,
    /// there is no verdict.
    /// </summary>
    public static ProbeWebOutcome Compute(IEnumerable<CheckResult> checks)
    {
        var list = checks.ToList();
        var failed = list.Where(c => c.Failed && !c.PreExisting).ToList();

        if (failed.Any(c => c.Classification == Classification.EnvironmentFailure))
            return ProbeWebOutcome.EnvironmentFailure;
        if (failed.Count > 0)
            return ProbeWebOutcome.Failed;
        return list.Any(c => c.Status == CheckStatus.Passed) ? ProbeWebOutcome.Passed : ProbeWebOutcome.Inconclusive;
    }
}
