using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>
/// The context rules of ReadMe_LitosSoftwareFactory_V1.md §8.6 that the host applies between
/// turns. Every turn's cost includes its whole context, so the context is managed deliberately.
/// </summary>
public static class ContextPolicy
{
    /// <summary>The fraction of the context window above which a large turn is preceded by compaction.</summary>
    public const double CompactBeforeLargeTurnFraction = 0.60;

    /// <summary>
    /// Whether to compact the thread's session before starting this turn. Only rework and repair
    /// turns qualify: they arrive after a long implement turn and carry a brief of their own.
    /// Review always runs in a fresh session, so it never needs it.
    /// </summary>
    /// <param name="usedFraction">The session's context usage as a fraction of its window, or
    /// null when it is not known yet (a session with no reported usage).</param>
    public static bool ShouldCompactBefore(TurnKind kind, double? usedFraction) =>
        kind is TurnKind.Rework or TurnKind.Repair && usedFraction > CompactBeforeLargeTurnFraction;
}
