using Litos.Gui.Buzzies.Routines;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>
/// "Open conversation" in the Routines window (Implement_Buzzies.md, Phase 1). The chat lives in the
/// window Routines was shown over, and an owned window always sits above its owner, so loading the
/// run's session while Routines stayed open put it behind Routines: found live on 2026-11-26, the
/// user had to close the Routines window to see the conversation they had just asked for.
///
/// These use the seam's <c>Action</c> rather than a real <c>Window</c>: constructing one needs an
/// Avalonia windowing platform, and the ordering is the whole point of the test.
/// </summary>
public class RoutinesWindowTests
{
    private static RoutineRun Run() => new(
        "run-1", "routine-1", "scout", "manual:1", RunTrigger.Manual, RunStatus.Succeeded,
        DateTimeOffset.UnixEpoch, SessionId: "session-1");

    [Fact]
    public async Task OpenConversation_ClosesTheRoutinesWindowBeforeLoadingTheSession()
    {
        var order = new List<string>();

        await Litos.Gui.Buzzies.RoutinesWindow.OpenConversationAsync(
            () => order.Add("closed"),
            _ => { order.Add("loaded"); return Task.CompletedTask; },
            Run());

        // Loaded-while-still-open is exactly the bug: the chat appears behind this window.
        Assert.Equal(["closed", "loaded"], order);
    }

    [Fact]
    public async Task OpenConversation_LoadsTheRunThatWasClicked()
    {
        RoutineRun? opened = null;

        await Litos.Gui.Buzzies.RoutinesWindow.OpenConversationAsync(
            () => { },
            run => { opened = run; return Task.CompletedTask; },
            Run());

        Assert.NotNull(opened);
        Assert.Equal("session-1", opened.SessionId);
    }

    [Fact]
    public async Task OpenConversation_ClosesTheWindowEvenIfLoadingThrows()
    {
        // A failed load must not strand the user with a Routines window over an unchanged chat.
        var closed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Litos.Gui.Buzzies.RoutinesWindow.OpenConversationAsync(
                () => closed = true,
                _ => throw new InvalidOperationException("boom"),
                Run()));

        Assert.True(closed);
    }
}