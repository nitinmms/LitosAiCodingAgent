namespace Litos.SoftwareFactory.Core.Lifecycle;

/// <summary>Whose move a task is: the one label every board card carries (§7.1).</summary>
public enum TurnLabel
{
    /// <summary>A draft nobody has delegated yet.</summary>
    NotStarted,

    /// <summary>Stopped until a person acts: a decision, testing, a budget, a blocker, a recovery.</summary>
    AwaitingYou,

    /// <summary>Queued, waiting for its repository or a free slot.</summary>
    AwaitingAgent,

    AgentWorking,

    /// <summary>Paused by a person, and resumed by one.</summary>
    Paused,

    /// <summary>Accepted or cancelled: nothing more happens to it.</summary>
    Done,
}

/// <summary>
/// The §7.1 table from lifecycle state to turn label, kept here so the host and every client
/// agree. Draft is not in that table: it is "not started" (m2-architecture.md §6), unless a
/// specification has been proposed, which waits for a person to approve it or delegate (§5).
/// </summary>
public static class TurnLabels
{
    public static TurnLabel For(LifecycleState state, Stage stage) => state switch
    {
        LifecycleState.Draft => stage == Stage.Spec ? TurnLabel.AwaitingYou : TurnLabel.NotStarted,
        LifecycleState.Queued => TurnLabel.AwaitingAgent,
        LifecycleState.Running => TurnLabel.AgentWorking,
        LifecycleState.PausedUser => TurnLabel.Paused,
        LifecycleState.Accepted or LifecycleState.Cancelled => TurnLabel.Done,
        LifecycleState.AwaitingDecision or LifecycleState.AwaitingHumanTesting or LifecycleState.PausedBudget
            or LifecycleState.Blocked or LifecycleState.Interrupted => TurnLabel.AwaitingYou,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A lifecycle state with no turn label."),
    };
}

/// <summary>The labels a task can be filed under (§7.1). Chosen by a person, never derived.</summary>
public static class TaskTypes
{
    public static readonly IReadOnlyList<string> All = ["bug", "feature", "refactor", "chore"];

    public static bool IsKnown(string? type) => type is not null && All.Contains(type);
}
