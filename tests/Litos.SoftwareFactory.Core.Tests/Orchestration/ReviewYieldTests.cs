using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>Review yield: what the reviews cost against what a person confirmed they found.</summary>
public class ReviewYieldTests
{
    private static UsageEntry Call(string? phase, long charged) =>
        new() { RequestKey = Guid.NewGuid().ToString(), Provider = "openrouter", Model = "m", Phase = phase, Charged = charged };

    private static ReviewFindingRecord Finding(FindingVerdict? verdict) =>
        new() { File = "src/a.ts", Text = "x", Severity = FindingSeverity.Minor, Verdict = verdict };

    [Fact]
    public void CountsOnlyReviewCalls_AsReviewCost_AndEveryCallInTheTotal()
    {
        var yield = ReviewYield.From(
            [Call("Implement", 500_000), Call("Review", 80_000), Call("LightReview", 20_000), Call("Scan", 25_000), Call(null, 5_000)],
            []);

        Assert.Equal((100_000L, 2, 630_000L), (yield.ReviewTokens, yield.ReviewCalls, yield.TotalTokens));
        Assert.Equal(15.9, yield.ReviewSharePercent);
    }

    [Fact]
    public void ConfirmedDefectsPer100KReviewTokens_AndTheFalsePositiveRate()
    {
        var yield = ReviewYield.From(
            [Call("Review", 200_000)],
            [Finding(FindingVerdict.Real), Finding(FindingVerdict.Real), Finding(FindingVerdict.Wrong), Finding(FindingVerdict.NotWorthFixing), Finding(null)]);

        Assert.Equal((5, 4, 2, 1, 1), (yield.Findings, yield.Judged, yield.Real, yield.Wrong, yield.NotWorthFixing));
        Assert.Equal(1.0, yield.RealPer100KReviewTokens);
        Assert.Equal(25.0, yield.FalsePositivePercent);
    }

    /// <summary>A task whose change needed no review, or whose findings nobody has judged yet,
    /// has no rate to report rather than a rate of zero.</summary>
    [Fact]
    public void WithNoReviewOrNothingJudged_TheRatesAreUnknown_NotZero()
    {
        var yield = ReviewYield.From([Call("Implement", 50_000)], [Finding(null)]);

        Assert.Null(yield.RealPer100KReviewTokens);
        Assert.Null(yield.FalsePositivePercent);
        Assert.Equal(0.0, yield.ReviewSharePercent);
        Assert.Null(ReviewYield.From([], []).ReviewSharePercent);
    }

    [Fact]
    public void Sum_AddsTasksTogether()
    {
        var total = ReviewYield.Sum([new ReviewYield(100_000, 10, 400_000, 3, 1, 1, 0), new ReviewYield(50_000, 4, 100_000, 2, 1, 0, 1)]);

        Assert.Equal(new ReviewYield(150_000, 14, 500_000, 5, 2, 1, 1), total);
        Assert.Equal(1.33, total.RealPer100KReviewTokens);
    }
}
