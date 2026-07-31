namespace Litos.Gui.Tests;

/// <summary>
/// Covers MainWindow.DecideEnterAction, the pure routing decision behind SubmitAsync's mid-turn
/// handling. Regression coverage for the bug where a turn in flight silently discarded any text
/// the user typed instead of steering it into the running turn (see AgentLoop's SteeringMessage
/// channel, wired only into Litos.Console's Composer until this fix reached Litos.Gui too).
/// </summary>
public class MainWindowEnterActionTests
{
    [Fact]
    public void DecideEnterAction_Submits_WhenNoTurnIsInFlight()
    {
        Assert.Equal(MainWindow.EnterAction.Submit, MainWindow.DecideEnterAction(turnInFlight: false, "hello"));
    }

    [Fact]
    public void DecideEnterAction_Submits_WhenIdleEvenForSlashCommandText()
    {
        Assert.Equal(MainWindow.EnterAction.Submit, MainWindow.DecideEnterAction(turnInFlight: false, "/compact"));
    }

    [Fact]
    public void DecideEnterAction_Steers_WhenATurnIsInFlightAndTextIsPlain()
    {
        Assert.Equal(MainWindow.EnterAction.Steer, MainWindow.DecideEnterAction(turnInFlight: true, "focus on the auth bug instead"));
    }

    [Fact]
    public void DecideEnterAction_RejectsSlashCommand_WhenATurnIsInFlight()
    {
        // Slash commands mutate the transcript directly (e.g. /compact's ApplyCompaction) rather
        // than going through AgentLoop, which would race the in-flight turn's live read of
        // _transcript.Messages — so these must wait instead of being steered.
        Assert.Equal(MainWindow.EnterAction.RejectSlashCommandMidTurn, MainWindow.DecideEnterAction(turnInFlight: true, "/compact"));
    }
}
