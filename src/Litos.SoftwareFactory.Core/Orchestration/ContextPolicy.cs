using Litos.Agent.Session;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>
/// The context rules of ReadMe_LitosSoftwareFactory_V1.md §8.6 that the host applies between
/// turns.
/// </summary>
public static class ContextPolicy
{
    /// <summary>
    /// Whether to compact the thread's session before starting this turn. Only rework and repair
    /// turns qualify: they arrive after a long implement turn and carry a brief of their own.
    /// Review always runs in a fresh session, so it never needs it.
    ///
    /// "Large" is the engine's own compaction trigger for the model's window
    /// (<see cref="CompactionSettings.ForContextWindow"/>), not a rule of the factory's: the same
    /// figure the agent loop compacts at mid-turn, and the one every other Litos face uses. It is
    /// deliberately high — 250,000 tokens for a million-token window — because compacting a
    /// smaller context costs more in re-reading what the summary dropped than it saves, and
    /// because a cut keeps the most recent part of the conversation verbatim, so a small session
    /// has nothing old enough to cut. The factory's sessions in the first real runs were 25,000
    /// to 45,000 tokens: this rule rightly leaves those alone.
    /// </summary>
    /// <param name="sessionInputTokens">The session's last reported input, or null when this
    /// run has not made a call in it yet.</param>
    public static bool ShouldCompactBefore(StartTurnStep step, int? sessionInputTokens, int contextLength)
    {
        if (step.Session != SessionScope.Thread || step.Kind is not (TurnKind.Rework or TurnKind.Repair))
            return false;
        if (sessionInputTokens is not { } tokens || contextLength <= 0)
            return false;

        return tokens > CompactionTrigger(contextLength);
    }

    /// <summary>The context size at which a session of this model is compacted.</summary>
    public static int CompactionTrigger(int contextLength) =>
        new CompactionSettings().ForContextWindow(contextLength).TriggerAtTokens;
}
