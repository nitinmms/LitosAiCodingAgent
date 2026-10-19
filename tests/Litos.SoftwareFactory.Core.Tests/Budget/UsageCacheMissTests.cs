using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Tests.Budget;

public sealed class UsageCacheMissTests
{
    private static UsageEntry Entry(long expected, long read, long input = 40_000, UsageStatus status = UsageStatus.Settled) => new()
    {
        RequestKey = "k",
        Provider = "openrouter",
        Model = "m",
        ExpectedCachedInput = expected,
        ActualCacheRead = read,
        ActualInput = input,
        Status = status,
    };

    /// <summary>R3's implementation call at 06:18:09 on the M4 check: 34,438 expected from the
    /// cache, nothing served, 35,771 input.</summary>
    [Fact]
    public void ACallTheCacheWasExpectedToServe_ThatCameBackUncached_IsAMiss_CostingTheMissedInputLessItsCachedWeight()
    {
        var entry = Entry(expected: 34_438, read: 0, input: 35_771);

        Assert.True(UsageCacheMiss.IsMiss(entry));
        Assert.Equal(30_995, UsageCacheMiss.ExtraTokens(entry, 0.10)); // 34,438 × 0.9, rounded up
    }

    public static TheoryData<string, UsageEntry, bool> Cases => new()
    {
        { "a hit", Entry(30_000, 29_500), false },
        { "half served is not a miss", Entry(30_000, 15_000), false },
        { "under half served is", Entry(30_000, 14_999), true },
        { "never expected (first call, a pause, a provider that does not cache)", Entry(0, 0), false },
        { "not settled", Entry(30_000, 0, status: UsageStatus.Unknown), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsMiss_OnlyWhenTheCacheServedUnderHalfOfWhatWasExpected(string name, UsageEntry entry, bool expected)
    {
        Assert.True(expected == UsageCacheMiss.IsMiss(entry), name);
        if (!expected)
            Assert.Equal(0, UsageCacheMiss.ExtraTokens(entry, 0.10));
    }

    /// <summary>The expectation is the last call's whole input; a call compacted smaller since
    /// cannot have missed more than it sent.</summary>
    [Fact]
    public void ExtraTokens_NeverCountsMoreThanTheCallsInput()
    {
        Assert.Equal(9_000, UsageCacheMiss.ExtraTokens(Entry(expected: 30_000, read: 0, input: 10_000), 0.10));
    }

    [Fact]
    public void ExtraTokens_AtFullCachedWeight_IsZero_BecauseAMissCostNothingMore()
    {
        Assert.Equal(0, UsageCacheMiss.ExtraTokens(Entry(30_000, 0), 1.0));
    }
}
