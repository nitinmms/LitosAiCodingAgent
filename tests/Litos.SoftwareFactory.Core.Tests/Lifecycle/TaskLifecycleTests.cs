using Litos.SoftwareFactory.Core.Lifecycle;

namespace Litos.SoftwareFactory.Core.Tests.Lifecycle;

public class TaskLifecycleTests
{
    /// <summary>The §7 state diagram, written out independently of the implementation's table.</summary>
    public static readonly (LifecycleState From, LifecycleTrigger Trigger, LifecycleState To)[] Legal =
    [
        (LifecycleState.Draft, LifecycleTrigger.Delegate, LifecycleState.Queued),
        (LifecycleState.Queued, LifecycleTrigger.Claim, LifecycleState.Running),
        (LifecycleState.Running, LifecycleTrigger.RequestDecision, LifecycleState.AwaitingDecision),
        (LifecycleState.AwaitingDecision, LifecycleTrigger.AnswerDecision, LifecycleState.Queued),
        (LifecycleState.Running, LifecycleTrigger.ExhaustBudget, LifecycleState.PausedBudget),
        (LifecycleState.PausedBudget, LifecycleTrigger.RaiseBudgetAndResume, LifecycleState.Queued),
        (LifecycleState.Running, LifecycleTrigger.Pause, LifecycleState.PausedUser),
        (LifecycleState.PausedUser, LifecycleTrigger.Resume, LifecycleState.Queued),
        (LifecycleState.Running, LifecycleTrigger.Block, LifecycleState.Blocked),
        (LifecycleState.Blocked, LifecycleTrigger.ResolveBlocker, LifecycleState.Queued),
        (LifecycleState.Running, LifecycleTrigger.Handoff, LifecycleState.AwaitingHumanTesting),
        (LifecycleState.AwaitingHumanTesting, LifecycleTrigger.RequestChanges, LifecycleState.Queued),
        (LifecycleState.AwaitingHumanTesting, LifecycleTrigger.Accept, LifecycleState.Accepted),
        (LifecycleState.Running, LifecycleTrigger.Interrupt, LifecycleState.Interrupted),
        // A spec run's proposal puts the task back in Draft, at the Spec stage (m2-architecture.md §5).
        (LifecycleState.Running, LifecycleTrigger.ProposeSpec, LifecycleState.Draft),
        (LifecycleState.Interrupted, LifecycleTrigger.Recover, LifecycleState.Queued),
        // A draft can be closed without ever being delegated (m2-architecture.md §7).
        (LifecycleState.Draft, LifecycleTrigger.Cancel, LifecycleState.Cancelled),
        (LifecycleState.Queued, LifecycleTrigger.Cancel, LifecycleState.Cancelled),
        (LifecycleState.Running, LifecycleTrigger.Cancel, LifecycleState.Cancelled),

        // "Pause and cancel also apply to waiting states."
        (LifecycleState.Queued, LifecycleTrigger.Pause, LifecycleState.PausedUser),
        (LifecycleState.AwaitingDecision, LifecycleTrigger.Cancel, LifecycleState.Cancelled),
        (LifecycleState.PausedBudget, LifecycleTrigger.Cancel, LifecycleState.Cancelled),
        (LifecycleState.PausedUser, LifecycleTrigger.Cancel, LifecycleState.Cancelled),
        (LifecycleState.Blocked, LifecycleTrigger.Cancel, LifecycleState.Cancelled),
        (LifecycleState.Interrupted, LifecycleTrigger.Cancel, LifecycleState.Cancelled),
        (LifecycleState.AwaitingHumanTesting, LifecycleTrigger.Cancel, LifecycleState.Cancelled),

        // A change request can be withdrawn whenever its rework run is not executing.
        (LifecycleState.Queued, LifecycleTrigger.WithdrawChanges, LifecycleState.AwaitingHumanTesting),
        (LifecycleState.AwaitingDecision, LifecycleTrigger.WithdrawChanges, LifecycleState.AwaitingHumanTesting),
        (LifecycleState.PausedBudget, LifecycleTrigger.WithdrawChanges, LifecycleState.AwaitingHumanTesting),
        (LifecycleState.PausedUser, LifecycleTrigger.WithdrawChanges, LifecycleState.AwaitingHumanTesting),
        (LifecycleState.Blocked, LifecycleTrigger.WithdrawChanges, LifecycleState.AwaitingHumanTesting),
        (LifecycleState.Interrupted, LifecycleTrigger.WithdrawChanges, LifecycleState.AwaitingHumanTesting),
    ];

    /// <summary>A running change request is paused first; a draft, a handoff that is already
    /// waiting, and a closed task have nothing to withdraw.</summary>
    [Theory]
    [InlineData(LifecycleState.Running)]
    [InlineData(LifecycleState.Draft)]
    [InlineData(LifecycleState.AwaitingHumanTesting)]
    [InlineData(LifecycleState.Accepted)]
    [InlineData(LifecycleState.Cancelled)]
    public void WithdrawChanges_IsNotLegalFrom(LifecycleState state) =>
        Assert.False(TaskLifecycle.CanApply(state, LifecycleTrigger.WithdrawChanges));

    public static TheoryData<LifecycleState, LifecycleTrigger, LifecycleState> LegalTransitions()
    {
        var data = new TheoryData<LifecycleState, LifecycleTrigger, LifecycleState>();
        foreach (var (from, trigger, to) in Legal)
            data.Add(from, trigger, to);
        return data;
    }

    /// <summary>Every (state, trigger) pair that is not in the diagram.</summary>
    public static TheoryData<LifecycleState, LifecycleTrigger> IllegalTransitions()
    {
        var legal = Legal.Select(t => (t.From, t.Trigger)).ToHashSet();
        var data = new TheoryData<LifecycleState, LifecycleTrigger>();
        foreach (var from in Enum.GetValues<LifecycleState>())
            foreach (var trigger in Enum.GetValues<LifecycleTrigger>())
                if (!legal.Contains((from, trigger)))
                    data.Add(from, trigger);
        return data;
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void Apply_LegalTransition_ReturnsTheNextState(LifecycleState from, LifecycleTrigger trigger, LifecycleState to)
    {
        Assert.Equal(to, TaskLifecycle.Apply(from, trigger));
        Assert.True(TaskLifecycle.CanApply(from, trigger));
        Assert.True(TaskLifecycle.TryApply(from, trigger, out var actual));
        Assert.Equal(to, actual);
    }

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void Apply_IllegalTransition_Throws(LifecycleState from, LifecycleTrigger trigger)
    {
        var ex = Assert.Throws<InvalidLifecycleTransitionException>(() => TaskLifecycle.Apply(from, trigger));

        Assert.Equal(from, ex.From);
        Assert.Equal(trigger, ex.Trigger);
        Assert.False(TaskLifecycle.CanApply(from, trigger));
        Assert.False(TaskLifecycle.TryApply(from, trigger, out _));
    }

    [Fact]
    public void All_IsExactlyTheDiagram()
    {
        Assert.Equal(Legal.Order(), TaskLifecycle.All.Order());
    }

    [Fact]
    public void IllegalTransitions_CoverEveryPairNotInTheDiagram()
    {
        var total = Enum.GetValues<LifecycleState>().Length * Enum.GetValues<LifecycleTrigger>().Length;

        Assert.Equal(total - Legal.Length, IllegalTransitions().Count);
    }

    [Theory]
    [InlineData(LifecycleState.Accepted)]
    [InlineData(LifecycleState.Cancelled)]
    public void TerminalStates_AllowNothing(LifecycleState state)
    {
        Assert.True(TaskLifecycle.IsTerminal(state));
        Assert.All(Enum.GetValues<LifecycleTrigger>(), trigger => Assert.False(TaskLifecycle.CanApply(state, trigger)));
    }

    [Fact]
    public void OnlyAcceptedAndCancelledAreTerminal()
    {
        var terminal = Enum.GetValues<LifecycleState>().Where(TaskLifecycle.IsTerminal);

        Assert.Equal([LifecycleState.Accepted, LifecycleState.Cancelled], terminal);
    }

    [Fact]
    public void EveryNonTerminalState_HasAWayForward()
    {
        foreach (var state in Enum.GetValues<LifecycleState>().Where(s => !TaskLifecycle.IsTerminal(s)))
            Assert.Contains(Enum.GetValues<LifecycleTrigger>(), trigger => TaskLifecycle.CanApply(state, trigger));
    }

    /// <summary>A normal chat message is never acceptance, and acceptance never merges: the only
    /// way into Accepted is the structured Accept from AwaitingHumanTesting.</summary>
    [Fact]
    public void Accepted_IsReachableOnlyFromAwaitingHumanTesting()
    {
        var into = TaskLifecycle.All.Where(t => t.To == LifecycleState.Accepted);

        Assert.Equal([(LifecycleState.AwaitingHumanTesting, LifecycleTrigger.Accept, LifecycleState.Accepted)], into);
    }

    /// <summary>Budget exhaustion cannot be bypassed by delegating again: the only way to do
    /// more work from PausedBudget is raising the cap. The other ways out stop the work:
    /// cancelling, or withdrawing the change request that ran out.</summary>
    [Fact]
    public void PausedBudget_ContinuesOnlyByRaisingTheCap()
    {
        var outOf = TaskLifecycle.All.Where(t => t.From == LifecycleState.PausedBudget).ToList();

        Assert.Equal(
            new[] { LifecycleTrigger.RaiseBudgetAndResume, LifecycleTrigger.Cancel, LifecycleTrigger.WithdrawChanges }.Order(),
            outOf.Select(t => t.Trigger).Order());
        Assert.Equal(LifecycleTrigger.RaiseBudgetAndResume, Assert.Single(outOf, t => t.To == LifecycleState.Queued).Trigger);
    }

    /// <summary>A draft leaves only by being delegated, by a spec run (which delegates it too), or
    /// by being cancelled; it cannot be paused or accepted.</summary>
    [Fact]
    public void Draft_IsDelegatedOrCancelled_AndNothingElse()
    {
        var outOf = TaskLifecycle.All.Where(t => t.From == LifecycleState.Draft).Select(t => t.Trigger).Order();

        Assert.Equal(new[] { LifecycleTrigger.Delegate, LifecycleTrigger.Cancel }.Order(), outOf);
    }

    /// <summary>Running is entered only by a claim, so no path skips the lock, slot and worker.</summary>
    [Fact]
    public void Running_IsReachableOnlyByClaimFromQueued()
    {
        var into = TaskLifecycle.All.Where(t => t.To == LifecycleState.Running);

        Assert.Equal([(LifecycleState.Queued, LifecycleTrigger.Claim, LifecycleState.Running)], into);
    }

    [Fact]
    public void Exception_NamesTheStateAndTheTrigger()
    {
        var ex = Assert.Throws<InvalidLifecycleTransitionException>(
            () => TaskLifecycle.Apply(LifecycleState.Draft, LifecycleTrigger.Accept));

        Assert.Contains("Draft", ex.Message);
        Assert.Contains("Accept", ex.Message);
    }

    [Fact]
    public void Stage_HasTheSevenStagesInOrder()
    {
        Assert.Equal(
            [Stage.Discuss, Stage.Spec, Stage.Implement, Stage.Verify, Stage.Review, Stage.Handoff, Stage.Done],
            Enum.GetValues<Stage>());
    }
}
