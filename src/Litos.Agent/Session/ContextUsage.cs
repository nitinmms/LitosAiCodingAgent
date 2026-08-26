namespace Litos.Agent.Session;

public enum ContextUsageLevel { Normal, Warning, Critical }

/// <summary>
/// Snapshot for a status-bar context meter: how much of the model's window is used, and how
/// urgent that is. Fraction is clamped to [0, 1] — see Compute's remarks on why the estimate it's
/// derived from can otherwise run past 100% of a real conversation's actual size. IsStale flags
/// that same failure mode for display, rather than presenting a runaway number with quiet confidence.
/// </summary>
public sealed record ContextUsageSnapshot(int UsedTokens, int ContextLength, double Fraction, ContextUsageLevel Level, bool IsStale);

/// <summary>
/// Turns Transcript's real+estimated usage (CompactionPlanner.EstimatedTokensUsed) into a
/// percentage and urgency level for display, using the same reserve-threshold concept
/// CompactionPlanner already applies when deciding whether to fire compaction.
/// </summary>
public static class ContextUsage
{
    /// <summary>Fraction (of ContextWindowTokens - ReserveTokens) above which usage is shown as Warning rather than Normal.</summary>
    public const double WarningFractionOfReserveThreshold = 0.6;

    /// <summary>
    /// MessagesSinceLastUsage beyond which the char/4 trailing estimate (CompactionPlanner.
    /// EstimatedTokensUsed) is treated as unreliable rather than displayed with confidence. A
    /// single legitimate turn — even a large tool-heavy one — appends at most a few dozen
    /// messages (one per round: an assistant message plus its tool results) before the next real
    /// usage report resets the count; this many messages piling up with no fresh report in
    /// between only happens when something keeps re-appending without ever completing a round —
    /// e.g. repeated sends/steering against a turn stuck mid-tool-call (see McpServerConnection's
    /// CallToolAsync timeout for the specific bug this was first observed alongside). Past this
    /// point the estimate can run well past the model's real context window even though the
    /// actual conversation is nowhere near full.
    /// </summary>
    public const int StaleAfterMessagesSinceLastUsage = 200;

    private static readonly CompactionSettings DefaultCompactionSettings = new();

    /// <summary>
    /// reserveTokens defaults to CompactionSettings.ReserveTokens (the same reserve compaction
    /// itself uses to decide when to fire) rather than restating its own constant, so the
    /// meter's Warning/Critical banding can't silently drift from when compaction actually kicks in.
    /// </summary>
    public static ContextUsageSnapshot? Compute(Transcript transcript, int contextLength, int? reserveTokens = null)
    {
        var usedTokens = CompactionPlanner.EstimatedTokensUsed(transcript);
        if (usedTokens is null)
            return null;

        var rawFraction = contextLength <= 0 ? 0 : (double)usedTokens.Value / contextLength;
        var fraction = Math.Clamp(rawFraction, 0, 1);
        var reserveThreshold = contextLength - (reserveTokens ?? DefaultCompactionSettings.ReserveTokens);
        var level = usedTokens >= reserveThreshold
            ? ContextUsageLevel.Critical
            : usedTokens >= reserveThreshold * WarningFractionOfReserveThreshold
                ? ContextUsageLevel.Warning
                : ContextUsageLevel.Normal;
        var isStale = transcript.MessagesSinceLastUsage > StaleAfterMessagesSinceLastUsage;

        return new ContextUsageSnapshot(usedTokens.Value, contextLength, fraction, level, isStale);
    }
}
