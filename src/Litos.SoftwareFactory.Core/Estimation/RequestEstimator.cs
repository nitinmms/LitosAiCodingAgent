using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;

namespace Litos.SoftwareFactory.Core.Estimation;

/// <summary>
/// The last settled request of a session: how many messages it carried and what the provider
/// said its whole input cost. The next request in the same session is that input again plus
/// whatever was appended since.
/// </summary>
public sealed record SessionBaseline(int MessageCount, int TotalInputTokens)
{
    /// <summary>How much of that request the provider served from its prompt cache. Above zero
    /// it shows the cache works for this session.</summary>
    public int CacheReadTokens { get; init; }

    /// <summary>When the request settled; null when not recorded.</summary>
    public DateTimeOffset? SettledAt { get; init; }

    public static SessionBaseline From(ChatRequest request, UsageInfo usage, DateTimeOffset? settledAt = null) =>
        new(request.Messages.Count, usage.TotalInputTokens) { CacheReadTokens = usage.CacheReadInputTokens, SettledAt = settledAt };

    /// <summary>
    /// How much of the next request's input the provider can be expected to serve from its
    /// cache: all of this request's input, once the session has shown that the cache is being
    /// hit and while it is recent enough to still be held. Otherwise nothing — a first call, a
    /// provider that does not cache, or a session resumed after a pause is reserved in full.
    /// </summary>
    public long ExpectedCachedTokens(DateTimeOffset now, TimeSpan cacheWindow) =>
        CacheReadTokens > 0 && SettledAt is { } at && now >= at && now - at <= cacheWindow ? TotalInputTokens : 0;
}

public enum EstimateBasis
{
    /// <summary>The session's last settled request plus the characters appended since.</summary>
    BaselinePlusDelta,

    /// <summary>No usable baseline: system prompt, tool schemas and messages, all by characters.</summary>
    FromScratch,
}

/// <summary>Raw is before calibration; Tokens is what the gateway reserves against.</summary>
public sealed record RequestEstimate(long RawTokens, long Tokens, EstimateBasis Basis, double CalibrationRatio);

/// <summary>
/// The pre-send input estimate per-call reservation needs (ReadMe_LitosSoftwareFactory_V1.md
/// §9.5). Characters are counted with the same per-block rules compaction uses
/// (CompactionPlanner.EstimateChars), so the two never disagree about what a message weighs.
/// </summary>
public static class RequestEstimator
{
    private const int CharsPerToken = 4;

    /// <param name="baseline">The session's last settled request, or null for its first call.</param>
    /// <param name="calibrationRatio">Actual ÷ estimated for this provider and model, never
    /// below 1 — see <see cref="CalibrationWindow"/>.</param>
    public static RequestEstimate Estimate(ChatRequest request, SessionBaseline? baseline, double calibrationRatio = 1.0)
    {
        var ratio = Math.Max(1.0, calibrationRatio);

        // A request with fewer messages than the baseline saw has been compacted (or belongs to a
        // different conversation): the baseline says nothing about it.
        if (baseline is not null && request.Messages.Count >= baseline.MessageCount)
        {
            var appended = request.Messages.Skip(baseline.MessageCount).Sum(MessageChars);
            var raw = baseline.TotalInputTokens + Tokens(appended);
            return new RequestEstimate(raw, Calibrate(raw, ratio), EstimateBasis.BaselinePlusDelta, ratio);
        }

        var chars = (request.SystemPrompt?.Length ?? 0)
            + request.Tools.Sum(ToolSchemaChars)
            + request.Messages.Sum(MessageChars);
        var fromScratch = Tokens(chars);
        return new RequestEstimate(fromScratch, Calibrate(fromScratch, ratio), EstimateBasis.FromScratch, ratio);
    }

    /// <summary>
    /// The size of a reply the host relayed, by the same characters ÷ 4 rule — what a call is
    /// charged for its output when the provider reports none. It cannot see reasoning the
    /// provider kept to itself, so it is a floor, not a full count.
    /// </summary>
    public static long EstimateOutputTokens(ChatMessage reply) => Tokens(MessageChars(reply));

    private static long MessageChars(ChatMessage message) => CompactionPlanner.EstimateChars(message);

    private static long ToolSchemaChars(Litos.Agent.Tools.ToolSchema tool) =>
        tool.Name.Length + tool.Description.Length + tool.ParameterSchema.GetRawText().Length;

    /// <summary>Rounded up: a partial token is a token.</summary>
    private static long Tokens(long chars) => (chars + CharsPerToken - 1) / CharsPerToken;

    private static long Calibrate(long raw, double ratio) => (long)Math.Ceiling(raw * ratio);
}

/// <summary>
/// Learns one provider and model's calibration ratio from recent settled calls: the mean of
/// actual ÷ raw estimate over a sliding window, with a floor of 1.0 so calibration can only ever
/// make the estimate more cautious.
/// </summary>
public sealed class CalibrationWindow(int capacity = 20)
{
    private readonly Queue<double> _ratios = new();
    private readonly Lock _lock = new();

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int Count
    {
        get { lock (_lock) return _ratios.Count; }
    }

    /// <summary>Records one settled call. Calls with nothing to compare (a zero estimate, or a
    /// provider that reported no input) are ignored rather than skewing the ratio.</summary>
    public void Record(long rawEstimateTokens, long actualInputTokens)
    {
        if (rawEstimateTokens <= 0 || actualInputTokens <= 0)
            return;

        lock (_lock)
        {
            _ratios.Enqueue((double)actualInputTokens / rawEstimateTokens);
            while (_ratios.Count > Capacity)
                _ratios.Dequeue();
        }
    }

    /// <summary>1.0 until something has been recorded.</summary>
    public double Ratio
    {
        get
        {
            lock (_lock)
                return _ratios.Count == 0 ? 1.0 : Math.Max(1.0, _ratios.Average());
        }
    }
}

/// <summary>One call's estimate against what the provider reported — the calibration log entry
/// every call writes, and the unit the M1 evaluation measures.</summary>
public sealed record EstimateSample(long EstimatedTokens, long ActualTokens)
{
    /// <summary>How far the estimate fell short, as a fraction of the estimate: 0.08 means the
    /// call cost 8% more than estimated. Zero or negative when the estimate was enough.</summary>
    public double UnderEstimate => EstimatedTokens <= 0 ? 0 : (double)(ActualTokens - EstimatedTokens) / EstimatedTokens;
}

public static class EstimatorAccuracy
{
    /// <summary>
    /// The 95th-percentile under-estimate (nearest-rank), floored at zero. The M1 exit gate
    /// requires this to stay within the reservation margin: when it does, at least 95% of calls
    /// were fully covered by what was reserved for their input.
    /// </summary>
    public static double UnderEstimateP95(IReadOnlyCollection<EstimateSample> samples)
    {
        if (samples.Count == 0)
            return 0;

        var ordered = samples.Select(s => s.UnderEstimate).Order().ToList();
        var rank = (int)Math.Ceiling(0.95 * ordered.Count);
        return Math.Max(0, ordered[rank - 1]);
    }

    public static bool IsWithinMargin(IReadOnlyCollection<EstimateSample> samples, double margin) =>
        UnderEstimateP95(samples) <= margin;
}
