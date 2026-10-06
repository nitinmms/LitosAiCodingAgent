using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>
/// What a task's agent reviews cost, and what they found that a person confirmed
/// (ReadM_SoftwareFactory_ReviewGuidance.md §13–§14). In M1 review took 10% to 44% of a task's
/// tokens, and only 6 of 18 threads had a blocking finding; without knowing which findings were
/// real, no threshold in ReviewPlanner can be tuned.
/// </summary>
/// <param name="ReviewTokens">Tokens charged to review calls (full and light).</param>
/// <param name="ReviewCalls">How many model calls those were.</param>
/// <param name="TotalTokens">Tokens charged to the whole task, for the review's share.</param>
/// <param name="Findings">Findings the reviews reported, across every run of the task.</param>
/// <param name="Real">Findings a person judged a real defect.</param>
/// <param name="NotWorthFixing">Findings judged true but not worth acting on.</param>
/// <param name="Wrong">Findings judged mistaken.</param>
public sealed record ReviewYield(
    long ReviewTokens, int ReviewCalls, long TotalTokens, int Findings, int Real, int NotWorthFixing, int Wrong)
{
    /// <summary>The usage phases that are review calls.</summary>
    public static readonly IReadOnlySet<string> ReviewPhases = new HashSet<string> { "Review", "LightReview" };

    /// <summary>Findings a person has judged.</summary>
    public int Judged => Real + NotWorthFixing + Wrong;

    /// <summary>Review's share of the task's tokens, in percent; null for a task that spent nothing.</summary>
    public double? ReviewSharePercent => TotalTokens > 0 ? Math.Round(100.0 * ReviewTokens / TotalTokens, 1) : null;

    /// <summary>Confirmed defects per 100,000 review tokens; null until a review has cost something.</summary>
    public double? RealPer100KReviewTokens => ReviewTokens > 0 ? Math.Round(Real * 100_000.0 / ReviewTokens, 2) : null;

    /// <summary>Wrong findings among those judged, in percent; null until one is judged.</summary>
    public double? FalsePositivePercent => Judged > 0 ? Math.Round(100.0 * Wrong / Judged, 1) : null;

    public static ReviewYield From(IEnumerable<UsageEntry> usage, IEnumerable<ReviewFindingRecord> findings)
    {
        var calls = usage.ToList();
        var review = calls.Where(u => u.Phase is { } phase && ReviewPhases.Contains(phase)).ToList();
        var found = findings.ToList();
        return new ReviewYield(
            review.Sum(u => u.Charged),
            review.Count,
            calls.Sum(u => u.Charged),
            found.Count,
            found.Count(f => f.Verdict == FindingVerdict.Real),
            found.Count(f => f.Verdict == FindingVerdict.NotWorthFixing),
            found.Count(f => f.Verdict == FindingVerdict.Wrong));
    }

    /// <summary>Several tasks' yields added together.</summary>
    public static ReviewYield Sum(IEnumerable<ReviewYield> yields)
    {
        var all = yields.ToList();
        return new ReviewYield(
            all.Sum(y => y.ReviewTokens), all.Sum(y => y.ReviewCalls), all.Sum(y => y.TotalTokens),
            all.Sum(y => y.Findings), all.Sum(y => y.Real), all.Sum(y => y.NotWorthFixing), all.Sum(y => y.Wrong));
    }
}
