using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// The handoff tells a tester who has never seen the branch where to get it and how to run it:
/// the host supplies the repository, the agent suggests the run steps, and steps given once stand
/// until a later turn or run gives new ones.
/// </summary>
public class HandoffRunInstructionsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private static readonly RunInstructions Npm = new(["Node.js 20 or later"], [new HowToRunStep("Install the dependencies.", "npm install"), new HowToRunStep("Open http://localhost:5173.")], "Nothing new to set up.");

    private static readonly RunInstructions Dotnet = new([".NET 9 SDK"], [new HowToRunStep("Start it.", "dotnet run")]);

    private static WorkSubmission Work(RunInstructions? howToRun = null) => new("Done.", [], [], [], [], howToRun);

    private static RunState Submitted(WorkSubmission work) => RunOrchestrator.NewRun(RunKind.Implement) with { LastSubmission = work };

    [Fact]
    public void Evidence_CarriesTheRepository_TheDefaultBranch_AndTheSubmittedRunInstructions()
    {
        var evidence = HandoffComposer.Evidence(Submitted(Work(Npm)), new HandoffFacts("factory/3f9a-count", "abc1234", null, null)
        {
            RepositoryUrl = "https://github.com/acme/stories",
            CloneUrl = "https://github.com/acme/stories.git",
            DefaultBranch = "main",
        });

        Assert.Equal("https://github.com/acme/stories", evidence.RepositoryUrl);
        Assert.Equal("https://github.com/acme/stories.git", evidence.CloneUrl);
        Assert.Equal("main", evidence.DefaultBranch);
        Assert.Same(Npm, evidence.HowToRun);
    }

    [Fact]
    public void Evidence_WithoutRunInstructions_KeepsTheLastHandoffs()
    {
        var evidence = HandoffComposer.Evidence(Submitted(Work()), new HandoffFacts("factory/x", null, null, null) { PreviousHowToRun = Npm });

        Assert.Same(Npm, evidence.HowToRun);
    }

    [Fact]
    public void Evidence_NewRunInstructions_ReplaceTheLastHandoffs()
    {
        var evidence = HandoffComposer.Evidence(Submitted(Work(Dotnet)), new HandoffFacts("factory/x", null, null, null) { PreviousHowToRun = Npm });

        Assert.Same(Dotnet, evidence.HowToRun);
    }

    [Fact]
    public void Evidence_WithNoRunInstructionsAnywhere_HasNone()
    {
        Assert.Null(HandoffComposer.Evidence(Submitted(Work()), new HandoffFacts("factory/x", null, null, null)).HowToRun);
    }

    private static (RunOrchestrator Orchestrator, RunState State) RepairAfter(WorkSubmission first)
    {
        var orchestrator = new RunOrchestrator();
        var state = RunOrchestrator.NewRun(RunKind.Implement);
        state = orchestrator.Next(state, new RunStarted(), T0).State;
        state = orchestrator.Next(state, new PreflightCompleted(true), T0).State;
        state = orchestrator.Next(state, new TurnEnded(TurnEndReason.Completed, first, FilesChanged: true), T0).State;
        var failing = new VerificationOutcome(BuildStatus.Failed, UnitTestStatus.NotRun, CoverageStatus.NotMeasured, [], null, [], ["error CS1002: ; expected"]);
        var repair = orchestrator.Next(state, new Verified(failing), T0);
        Assert.Equal(TurnKind.Repair, Assert.IsType<StartTurnStep>(repair.Step).Kind);
        return (orchestrator, repair.State);
    }

    [Fact]
    public void ARepairThatDoesNotSayHowToRun_KeepsTheEarlierTurnsInstructions()
    {
        var (orchestrator, state) = RepairAfter(Work(Npm));

        var next = orchestrator.Next(state, new TurnEnded(TurnEndReason.Completed, new WorkSubmission("Fixed the build.", [], [], [], []), FilesChanged: true), T0);

        Assert.Equal("Fixed the build.", next.State.LastSubmission!.Summary);
        Assert.Same(Npm, next.State.LastSubmission.HowToRun);
    }

    [Fact]
    public void ARepairThatSaysHowToRun_ReplacesTheEarlierTurnsInstructions()
    {
        var (orchestrator, state) = RepairAfter(Work(Npm));

        var next = orchestrator.Next(state, new TurnEnded(TurnEndReason.Completed, Work(Dotnet), FilesChanged: true), T0);

        Assert.Same(Dotnet, next.State.LastSubmission!.HowToRun);
    }

    [Fact]
    public void RunInstructions_SurviveTheRunStatesRoundTrip()
    {
        var restored = RunStateJson.Deserialize(RunStateJson.Serialize(Submitted(Work(Npm))));

        var howToRun = restored.LastSubmission!.HowToRun!;
        Assert.Equal(["Node.js 20 or later"], howToRun.Prerequisites);
        Assert.Equal(Npm.Steps, howToRun.Steps);
        Assert.Equal("Nothing new to set up.", howToRun.SetupChanges);
    }
}
