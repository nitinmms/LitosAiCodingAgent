using Litos.SoftwareFactory.Core.Lifecycle;

namespace Litos.SoftwareFactory.Core.Tests.Lifecycle;

/// <summary>Whose move a task is, from its state (§7.1).</summary>
public class TurnLabelsTests
{
    [Theory]
    [InlineData(LifecycleState.Draft, TurnLabel.NotStarted)]
    [InlineData(LifecycleState.Queued, TurnLabel.AwaitingAgent)]
    [InlineData(LifecycleState.Running, TurnLabel.AgentWorking)]
    [InlineData(LifecycleState.AwaitingDecision, TurnLabel.AwaitingYou)]
    [InlineData(LifecycleState.AwaitingHumanTesting, TurnLabel.AwaitingYou)]
    [InlineData(LifecycleState.PausedBudget, TurnLabel.AwaitingYou)]
    [InlineData(LifecycleState.Blocked, TurnLabel.AwaitingYou)]
    [InlineData(LifecycleState.Interrupted, TurnLabel.AwaitingYou)]
    [InlineData(LifecycleState.PausedUser, TurnLabel.Paused)]
    [InlineData(LifecycleState.Accepted, TurnLabel.Done)]
    [InlineData(LifecycleState.Cancelled, TurnLabel.Done)]
    public void EachState_HasTheBlueprintsLabel(LifecycleState state, TurnLabel expected) =>
        Assert.Equal(expected, TurnLabels.For(state));

    /// <summary>A state added later must be given a label, not fall into one silently.</summary>
    [Fact]
    public void EveryStateHasALabel()
    {
        foreach (var state in Enum.GetValues<LifecycleState>())
            TurnLabels.For(state);
    }

    [Fact]
    public void TaskTypes_AreTheFourOfTheBlueprint_AndNothingElseIsKnown()
    {
        Assert.Equal(["bug", "feature", "refactor", "chore"], TaskTypes.All);
        Assert.True(TaskTypes.IsKnown("bug"));
        Assert.False(TaskTypes.IsKnown("Bug"));
        Assert.False(TaskTypes.IsKnown("epic"));
        Assert.False(TaskTypes.IsKnown(null));
    }
}
