namespace Litos.Agent.Session;

public enum ContextUsageLevel { Normal, Warning, Critical }

/// <summary>Snapshot for a status-bar context meter: how much of the model's window is used, and how urgent that is.</summary>
public sealed record ContextUsageSnapshot(int UsedTokens, int ContextLength, double Fraction, ContextUsageLevel Level);

/// <summary>
/// Turns Transcript's real+estimated usage (CompactionPlanner.EstimatedTokensUsed) into a
/// percentage and urgency level for display, using the same reserve-threshold concept
/// CompactionPlanner already applies when deciding whether to fire compaction.
/// </summary>
public static class ContextUsage
{
    /// <summary>Fraction (of ContextWindowTokens - ReserveTokens) above which usage is shown as Warning rather than Normal.</summary>
    public const double WarningFractionOfReserveThreshold = 0.6;

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

        var fraction = contextLength <= 0 ? 0 : (double)usedTokens.Value / contextLength;
        var reserveThreshold = contextLength - (reserveTokens ?? DefaultCompactionSettings.ReserveTokens);
        var level = usedTokens >= reserveThreshold
            ? ContextUsageLevel.Critical
            : usedTokens >= reserveThreshold * WarningFractionOfReserveThreshold
                ? ContextUsageLevel.Warning
                : ContextUsageLevel.Normal;

        return new ContextUsageSnapshot(usedTokens.Value, contextLength, fraction, level);
    }
}
