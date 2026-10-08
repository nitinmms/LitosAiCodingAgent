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
        Assert.Equal(expected, TurnLabels.For(state, Stage.Discuss));

    /// <summary>A state added later must be given a label, not fall into one silently.</summary>
    [Fact]
    public void EveryStateHasALabel_AtEveryStage()
    {
        foreach (var state in Enum.GetValues<LifecycleState>())
        {
            foreach (var stage in Enum.GetValues<Stage>())
                TurnLabels.For(state, stage);
        }
    }

    /// <summary>A proposed specification waits for a person: approve it, or ask for changes (§5).</summary>
    [Fact]
    public void ADraftWithASpecification_IsAwaitingYou()
    {
        Assert.Equal(TurnLabel.AwaitingYou, TurnLabels.For(LifecycleState.Draft, Stage.Spec));
        Assert.Equal(TurnLabel.NotStarted, TurnLabels.For(LifecycleState.Draft, Stage.Discuss));
    }

    /// <summary>Only a draft's label depends on its stage: a spec run in progress is the agent's turn.</summary>
    [Theory]
    [InlineData(LifecycleState.Queued, TurnLabel.AwaitingAgent)]
    [InlineData(LifecycleState.Running, TurnLabel.AgentWorking)]
    [InlineData(LifecycleState.PausedUser, TurnLabel.Paused)]
    [InlineData(LifecycleState.Blocked, TurnLabel.AwaitingYou)]
    public void AtTheSpecStage_OtherStatesKeepTheirLabel(LifecycleState state, TurnLabel expected) =>
        Assert.Equal(expected, TurnLabels.For(state, Stage.Spec));

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
