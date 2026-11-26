using Litos.Gui.Buzzies;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>
/// When the Routines window may rebuild its cards (Implement_Buzzies.md, Phase 1). Rebuilding clears
/// and re-creates every card, so a rebuild landing while a button's Click handler is running destroys
/// that button mid-gesture — found live on 2026-11-26 with "Run now". The gate is what prevents that,
/// and the rebuild it defers must still happen exactly once.
/// </summary>
public class RoutineRefreshGateTests
{
    [Fact]
    public void WithNothingHeld_AChangeRebuildsImmediately()
    {
        var gate = new RoutineRefreshGate();

        Assert.True(gate.Request());
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public void WhileHeld_AChangeDoesNotRebuild()
    {
        var gate = new RoutineRefreshGate();
        gate.Hold();

        // Deferred, not dropped: the rebuild when the hold ends picks this up.
        Assert.False(gate.Request());
        Assert.True(gate.IsHeld);
    }

    [Fact]
    public void EndingTheOutermostHold_AsksForOneRebuildForEverythingThatHappened()
    {
        // Run now raises Changed repeatedly, and Cancel raises none at all; both end in one rebuild.
        var gate = new RoutineRefreshGate();
        gate.Hold();
        Assert.False(gate.Request());
        Assert.False(gate.Request());
        Assert.False(gate.Request());

        Assert.True(gate.Release());
        Assert.False(gate.IsHeld);

        // And normal refreshing resumes afterwards.
        Assert.True(gate.Request());
    }

    [Fact]
    public void NestedHolds_DoNotRebuildUntilTheLastOneEnds()
    {
        var gate = new RoutineRefreshGate();
        gate.Hold();
        gate.Hold();
        gate.Request();

        Assert.False(gate.Release());
        Assert.True(gate.IsHeld);
        Assert.False(gate.Request());

        Assert.True(gate.Release());
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public void AStrayRelease_RebuildsNothingAndLeavesTheGateOpen()
    {
        // A mismatched release must not drive the count negative, which would start rebuilding
        // while a real action is still running.
        var gate = new RoutineRefreshGate();

        Assert.False(gate.Release());
        Assert.False(gate.IsHeld);

        gate.Hold();
        Assert.False(gate.Request());
        gate.Release();
        gate.Release();

        gate.Hold();
        Assert.False(gate.Request());
    }
}