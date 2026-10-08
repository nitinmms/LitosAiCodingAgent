namespace Litos.SoftwareFactory.Core.Lifecycle;

/// <summary>
/// What kind of work a task is in. Deliberately separate from <see cref="LifecycleState"/>
/// (ReadMe_LitosSoftwareFactory_V1.md §5.1): a task can be Implement/Running or
/// Handoff/AwaitingHumanTesting, and a new stage can be added without touching the state machine.
/// </summary>
public enum Stage
{
    Discuss,
    Spec,
    Implement,
    Verify,
    Review,
    Handoff,
    Done,
}

/// <summary>Whether a task is moving and who it waits for (§7).</summary>
public enum LifecycleState
{
    Draft,
    Queued,
    Running,
    AwaitingDecision,
    PausedBudget,
    PausedUser,
    Blocked,
    AwaitingHumanTesting,
    Accepted,
    Interrupted,
    Cancelled,
}

/// <summary>The things that can happen to a task. Each is legal from only some states.</summary>
public enum LifecycleTrigger
{
    /// <summary>The user delegates with @factory.</summary>
    Delegate,

    /// <summary>The coordinator acquired the lock, a slot and a worker.</summary>
    Claim,

    /// <summary>The agent needs a human choice.</summary>
    RequestDecision,

    /// <summary>The user answered the open decision.</summary>
    AnswerDecision,

    /// <summary>The next model call does not fit the task's allowance or the user's quota.</summary>
    ExhaustBudget,

    /// <summary>The cap was raised and the task resumed.</summary>
    RaiseBudgetAndResume,

    Pause,

    Resume,

    /// <summary>An environment or verification blocker stopped the run.</summary>
    Block,

    ResolveBlocker,

    /// <summary>The handoff was committed and pushed.</summary>
    Handoff,

    /// <summary>The tester asked for changes in the same thread.</summary>
    RequestChanges,

    Accept,

    /// <summary>The host or the worker was lost mid-run.</summary>
    Interrupt,

    /// <summary>The user explicitly recovered an interrupted run.</summary>
    Recover,

    Cancel,

    /// <summary>The tester took back a change request before it produced a new handoff: the
    /// rework run is dropped and the task waits for testing on its last handoff again.</summary>
    WithdrawChanges,

    /// <summary>A spec run proposed a specification: the task is a draft again, at the Spec
    /// stage, until a person approves it and delegates (m2-architecture.md §5).</summary>
    ProposeSpec,
}

public sealed class InvalidLifecycleTransitionException(LifecycleState from, LifecycleTrigger trigger)
    : InvalidOperationException($"A task that is {from} cannot {trigger}.")
{
    public LifecycleState From { get; } = from;

    public LifecycleTrigger Trigger { get; } = trigger;
}

/// <summary>
/// The §7 state machine as an explicit table. Status is never derived from the agent's chat
/// memory: every change of state is one of these transitions, and anything not in the table
/// throws.
/// </summary>
public static class TaskLifecycle
{
    private static readonly Dictionary<(LifecycleState From, LifecycleTrigger Trigger), LifecycleState> Transitions = new()
    {
        [(LifecycleState.Draft, LifecycleTrigger.Delegate)] = LifecycleState.Queued,

        [(LifecycleState.Queued, LifecycleTrigger.Claim)] = LifecycleState.Running,

        [(LifecycleState.Running, LifecycleTrigger.RequestDecision)] = LifecycleState.AwaitingDecision,
        [(LifecycleState.Running, LifecycleTrigger.ExhaustBudget)] = LifecycleState.PausedBudget,
        [(LifecycleState.Running, LifecycleTrigger.Pause)] = LifecycleState.PausedUser,
        [(LifecycleState.Running, LifecycleTrigger.Block)] = LifecycleState.Blocked,
        [(LifecycleState.Running, LifecycleTrigger.Handoff)] = LifecycleState.AwaitingHumanTesting,
        [(LifecycleState.Running, LifecycleTrigger.Interrupt)] = LifecycleState.Interrupted,
        [(LifecycleState.Running, LifecycleTrigger.ProposeSpec)] = LifecycleState.Draft,

        [(LifecycleState.AwaitingDecision, LifecycleTrigger.AnswerDecision)] = LifecycleState.Queued,
        [(LifecycleState.PausedBudget, LifecycleTrigger.RaiseBudgetAndResume)] = LifecycleState.Queued,
        [(LifecycleState.PausedUser, LifecycleTrigger.Resume)] = LifecycleState.Queued,
        [(LifecycleState.Blocked, LifecycleTrigger.ResolveBlocker)] = LifecycleState.Queued,
        [(LifecycleState.Interrupted, LifecycleTrigger.Recover)] = LifecycleState.Queued,

        [(LifecycleState.AwaitingHumanTesting, LifecycleTrigger.RequestChanges)] = LifecycleState.Queued,
        [(LifecycleState.AwaitingHumanTesting, LifecycleTrigger.Accept)] = LifecycleState.Accepted,

        // "Pause and cancel also apply to waiting states." A queued task has not started, so
        // pausing it simply holds it out of the queue. The other waiting states are already
        // stopped and waiting on a person, so pausing them would only hide what they wait for;
        // they can be cancelled but not paused.
        [(LifecycleState.Queued, LifecycleTrigger.Pause)] = LifecycleState.PausedUser,

        [(LifecycleState.Queued, LifecycleTrigger.Cancel)] = LifecycleState.Cancelled,
        [(LifecycleState.Running, LifecycleTrigger.Cancel)] = LifecycleState.Cancelled,
        [(LifecycleState.AwaitingDecision, LifecycleTrigger.Cancel)] = LifecycleState.Cancelled,
        [(LifecycleState.PausedBudget, LifecycleTrigger.Cancel)] = LifecycleState.Cancelled,
        [(LifecycleState.PausedUser, LifecycleTrigger.Cancel)] = LifecycleState.Cancelled,
        [(LifecycleState.Blocked, LifecycleTrigger.Cancel)] = LifecycleState.Cancelled,
        [(LifecycleState.Interrupted, LifecycleTrigger.Cancel)] = LifecycleState.Cancelled,
        [(LifecycleState.AwaitingHumanTesting, LifecycleTrigger.Cancel)] = LifecycleState.Cancelled,

        // A change request can be withdrawn from any state in which its rework run is not
        // executing; a running one is paused first. The table cannot see what kind of run the
        // task has — the store checks that it is a rework run with a handoff to go back to.
        [(LifecycleState.Queued, LifecycleTrigger.WithdrawChanges)] = LifecycleState.AwaitingHumanTesting,
        [(LifecycleState.AwaitingDecision, LifecycleTrigger.WithdrawChanges)] = LifecycleState.AwaitingHumanTesting,
        [(LifecycleState.PausedBudget, LifecycleTrigger.WithdrawChanges)] = LifecycleState.AwaitingHumanTesting,
        [(LifecycleState.PausedUser, LifecycleTrigger.WithdrawChanges)] = LifecycleState.AwaitingHumanTesting,
        [(LifecycleState.Blocked, LifecycleTrigger.WithdrawChanges)] = LifecycleState.AwaitingHumanTesting,
        [(LifecycleState.Interrupted, LifecycleTrigger.WithdrawChanges)] = LifecycleState.AwaitingHumanTesting,
    };

    public static bool CanApply(LifecycleState from, LifecycleTrigger trigger) => Transitions.ContainsKey((from, trigger));

    public static bool TryApply(LifecycleState from, LifecycleTrigger trigger, out LifecycleState to) =>
        Transitions.TryGetValue((from, trigger), out to);

    /// <exception cref="InvalidLifecycleTransitionException">The trigger is not legal from this state.</exception>
    public static LifecycleState Apply(LifecycleState from, LifecycleTrigger trigger) =>
        Transitions.TryGetValue((from, trigger), out var to) ? to : throw new InvalidLifecycleTransitionException(from, trigger);

    /// <summary>Every legal transition, for tests and for documentation generated from the table.</summary>
    public static IReadOnlyCollection<(LifecycleState From, LifecycleTrigger Trigger, LifecycleState To)> All =>
        [.. Transitions.Select(t => (t.Key.From, t.Key.Trigger, t.Value))];

    /// <summary>Accepted and Cancelled: nothing can happen to the task any more.</summary>
    public static bool IsTerminal(LifecycleState state) => state is LifecycleState.Accepted or LifecycleState.Cancelled;
}
