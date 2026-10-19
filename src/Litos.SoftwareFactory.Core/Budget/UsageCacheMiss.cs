using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Budget;

/// <summary>
/// Which settled calls the provider's prompt cache failed, and what that cost. A call is a miss
/// when the gateway expected the cache to serve its input — the same session, inside the cache
/// window, with the cache already proven (SessionBaseline.ExpectedCachedTokens) — and the cache
/// served less than half of it. A first call, a provider that does not cache, or a call after a
/// pause was never expected to hit, so it is not counted. On the M4 check R3 had calls seconds
/// apart on one upstream come back uncached; this is what tells such a miss from the factory's
/// own cost.
/// </summary>
public static class UsageCacheMiss
{
    public static bool IsMiss(UsageEntry entry) =>
        entry.Status == UsageStatus.Settled
        && entry.ExpectedCachedInput > 0
        && entry.ActualCacheRead * 2 < entry.ExpectedCachedInput;

    /// <summary>
    /// About how many tokens more the call was charged than had the cache served what was
    /// expected: the input the cache should have served, at full weight less the cached weight.
    /// Zero when the call was not a miss.
    /// </summary>
    public static long ExtraTokens(UsageEntry entry, double cachedInputWeight)
    {
        if (!IsMiss(entry))
            return 0;

        var shouldHaveBeenCached = Math.Min(entry.ExpectedCachedInput, entry.ActualInput);
        var missed = Math.Max(0, shouldHaveBeenCached - entry.ActualCacheRead);
        return (long)Math.Ceiling(missed * (decimal)(1 - cachedInputWeight));
    }
}
