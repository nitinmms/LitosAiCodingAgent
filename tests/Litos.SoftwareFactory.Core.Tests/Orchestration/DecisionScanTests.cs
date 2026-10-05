using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// The decision scan. No task in the evaluation ever called request_decision: on F6 the agent saw
/// the file-format question twice and decided it alone. A read-only scan before implementation
/// lists the choices a request leaves open, and DecisionPolicy decides by rule which a person makes.
/// </summary>
public class DecisionPolicyTests
{
    private static OpenChoice Choice(string category, string? settledBy = null, int options = 2, string question = "What happens to version-1 files?") =>
        new(question, category, [.. Enumerable.Range(1, options).Select(i => $"Option {i}")], "Option 1", "Every database is version 1.", settledBy);

    private static PlanSubmission Plan(params OpenChoice[] choices) => new("Store expiry per record.", ["src/LogFormat.cs"], choices);

    [Theory]
    [InlineData(ChoiceCategories.ExistingData)]
    [InlineData(ChoiceCategories.PublicBehaviour)]
    [InlineData(ChoiceCategories.DataDeletion)]
    [InlineData(ChoiceCategories.Dependency)]
    [InlineData(ChoiceCategories.UserVisible)]
    public void AnUnsettledChoiceInAnAlwaysAskCategory_IsAsked(string category) =>
        Assert.Single(DecisionPolicy.Decide(Plan(Choice(category)), maxQuestions: 3).Ask);

    [Fact]
    public void AChoiceTheRequestSettles_IsAssumed_WithWhatSettlesIt()
    {
        var decided = DecisionPolicy.Decide(Plan(Choice(ChoiceCategories.Dependency, settledBy: "The request says: use the openai SDK.", question: "Which SDK?")), 3);

        Assert.Empty(decided.Ask);
        Assert.Equal("Which SDK: Option 1 (The request says: use the openai SDK.)", DecisionPolicy.Assumption(Assert.Single(decided.Assume)));
    }

    /// <summary>F7's scan marked the format-upgrade question as settled by "Versions must survive
    /// reopening and compaction", which does not settle it: F6's failure mode.</summary>
    [Fact]
    public void AnExistingDataChoice_IsAskedEvenWhenTheScanSaysItIsSettled()
    {
        var decided = DecisionPolicy.Decide(Plan(Choice(
            ChoiceCategories.ExistingData, settledBy: "Versions must survive reopening and compaction.",
            question: "How is the on-disk format changed, and when does an existing file get upgraded?")), 3);

        Assert.Equal("How is the on-disk format changed, and when does an existing file get upgraded?", Assert.Single(decided.Ask).Question);
        Assert.Empty(decided.Assume);
    }

    [Fact]
    public void AnOtherChoice_OrOneWithoutTwoOptions_IsAssumed()
    {
        var decided = DecisionPolicy.Decide(Plan(Choice(ChoiceCategories.Other), Choice(ChoiceCategories.ExistingData, options: 1)), 3);

        Assert.Empty(decided.Ask);
        Assert.Equal(2, decided.Assume.Count);
    }

    [Fact]
    public void AtMostTheAllowedQuestionsAreAsked_InTheScansOrder_AndTheRestAssumed()
    {
        var decided = DecisionPolicy.Decide(Plan(
            Choice(ChoiceCategories.ExistingData, question: "Q1?"),
            Choice(ChoiceCategories.Dependency, question: "Q2?"),
            Choice(ChoiceCategories.UserVisible, question: "Q3?")), maxQuestions: 2);

        Assert.Equal(["Q1?", "Q2?"], decided.Ask.Select(c => c.Question));
        Assert.Equal("Q3?", Assert.Single(decided.Assume).Question);
    }

    [Fact]
    public void ACategoryIsMatchedRegardlessOfCase() =>
        Assert.Single(DecisionPolicy.Decide(Plan(Choice("Existing-Data")), 3).Ask);

    [Fact]
    public void AChoiceBecomesADecisionCard_WithAtMostFourOptions()
    {
        var card = DecisionPolicy.ToDecision(Choice(ChoiceCategories.ExistingData, options: 6));

        Assert.Equal("What happens to version-1 files?", card.Question);
        Assert.Equal("Every database is version 1.", card.WhyItBlocks);
        Assert.Equal(4, card.Options.Count);
        Assert.Equal("Option 1", card.Recommendation);
    }

    [Fact]
    public void AnAssumptionWithNoRecommendation_UsesTheFirstOption() =>
        Assert.Equal("Sweep or not: On read", DecisionPolicy.Assumption(new OpenChoice("Sweep or not?", ChoiceCategories.Other, ["On read", "Sweep"])));

    [Fact]
    public void TheThreadNoteSaysWhatWasFound_Asked_AndAssumed()
    {
        Assert.Equal("Decision scan: the request leaves no choice open. Implementing.", DecisionPolicy.Decide(Plan(), 3).Describe());

        var note = DecisionPolicy.Decide(Plan(Choice(ChoiceCategories.ExistingData), Choice(ChoiceCategories.Other, question: "Sweep or not?")), 3).Describe();
        Assert.Equal("Decision scan: 2 open choices, asking you 1. Assuming: Sweep or not: Option 1.", note);
    }
}

public class DecisionScanRunTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly RunLimits Scanning = new() { DecisionScan = true };

    private sealed class Run(RunKind kind = RunKind.Implement, RunLimits? limits = null)
    {
        private readonly RunOrchestrator _orchestrator = new(limits ?? Scanning);

        public RunState State { get; private set; } = RunOrchestrator.NewRun(kind);

        public RunStep Report(StepOutcome outcome)
        {
            var transition = _orchestrator.Next(State, outcome, T0);
            State = transition.State;
            return transition.Step;
        }

        public RunStep Started()
        {
            Report(new RunStarted());
            return Report(new PreflightCompleted(true));
        }

        public RunStep Scanned(params OpenChoice[] choices) =>
            Report(new TurnEnded(TurnEndReason.Completed, new PlanSubmission("Store expiry per record.", ["src/LogFormat.cs"], choices)));
    }

    private static OpenChoice Asked(string question) =>
        new(question, ChoiceCategories.ExistingData, ["Keep reading them", "Stop reading them"], "Keep reading them", "Every database is version 1.");

    private static OpenChoice Assumed(string question) => new(question, ChoiceCategories.Other, ["On read", "A sweep"], "On read");

    [Fact]
    public void AFirstRun_StartsWithTheScan_InItsOwnSession()
    {
        Assert.Equal(new StartTurnStep(TurnKind.Scan, BriefKind.Scan, SessionScope.Scan), new Run().Started());
    }

    [Fact]
    public void WithTheScanOff_AFirstRunImplementsAtOnce() =>
        Assert.Equal(new StartTurnStep(TurnKind.Implement, BriefKind.Run), new Run(limits: new RunLimits()).Started());

    [Fact]
    public void AReworkRun_DoesNotScan() =>
        Assert.Equal(new StartTurnStep(TurnKind.Rework, BriefKind.Rework), new Run(RunKind.Rework).Started());

    [Fact]
    public void NothingToAsk_ImplementsStraightAway_WithTheAssumptionsAndThePlan()
    {
        var run = new Run();
        run.Started();

        var next = run.Scanned(Assumed("On read or a sweep?"));

        Assert.Equal(new StartTurnStep(TurnKind.Implement, BriefKind.Run), next);
        Assert.Equal(["On read or a sweep: On read"], run.State.Assumptions);
        Assert.Equal("Store expiry per record.", run.State.Plan!.Approach);
        Assert.Equal(0, run.State.DecisionsAsked);
    }

    [Fact]
    public void QuestionsAreAskedOneAtATime_ThenTheRunImplementsWithEveryAnswer()
    {
        var run = new Run();
        run.Started();

        var first = Assert.IsType<StopStep>(run.Scanned(Asked("Q1?"), Assumed("Sweep?"), Asked("Q2?")));
        Assert.Equal((LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded, "Q1?"), (first.Trigger, first.Reason, first.Message));
        Assert.Equal("Q1?", run.State.OpenDecision!.Question);
        Assert.Equal(["Keep reading them", "Stop reading them"], run.State.OpenDecision.Options);

        var second = Assert.IsType<StopStep>(run.Report(new DecisionAnswered("Keep reading them.")));
        Assert.Equal("Q2?", second.Message);
        Assert.Equal(2, run.State.DecisionsAsked);

        Assert.IsType<PreflightStep>(run.Report(new DecisionAnswered("Stop reading them.")));
        var implement = run.Report(new PreflightCompleted(true));

        Assert.Equal(new StartTurnStep(TurnKind.Implement, BriefKind.Run), implement);
        Assert.Equal([("Q1?", "Keep reading them."), ("Q2?", "Stop reading them.")], run.State.Decisions.Select(d => (d.Question, d.Answer)));
        Assert.Empty(run.State.PendingQuestions);
        Assert.Equal(["Sweep: On read"], run.State.Assumptions);
    }

    [Fact]
    public void TheScanUsesTheRunsDecisionAllowance()
    {
        var run = new Run(limits: Scanning with { MaxDecisions = 1 });
        run.Started();

        run.Scanned(Asked("Q1?"), Asked("Q2?"));

        Assert.Empty(run.State.PendingQuestions);
        Assert.Equal(["Q2: Keep reading them"], run.State.Assumptions);
        Assert.IsType<PreflightStep>(run.Report(new DecisionAnswered("Keep.")));
    }

    [Fact]
    public void AScanThatDoesNotSubmit_IsRemindedOnce_ThenTheRunImplementsWithoutIt_AndSaysSo()
    {
        var run = new Run();
        run.Started();

        Assert.Equal(new StartTurnStep(TurnKind.Nudge, BriefKind.Nudge, SessionScope.Scan), run.Report(new TurnEnded(TurnEndReason.Completed)));
        var next = run.Report(new TurnEnded(TurnEndReason.Completed));

        Assert.Equal(new StartTurnStep(TurnKind.Implement, BriefKind.Run), next);
        Assert.Contains("The decision scan did not finish", Assert.Single(run.State.Disclosures));
    }

    [Fact]
    public void AScanThatRunsOutOfToolCalls_IsReminded_NotBlocked()
    {
        var run = new Run();
        run.Started();

        Assert.Equal(new StartTurnStep(TurnKind.Nudge, BriefKind.Nudge, SessionScope.Scan), run.Report(new TurnEnded(TurnEndReason.ToolCallLimit)));
        Assert.Equal(new StartTurnStep(TurnKind.Implement, BriefKind.Run), run.Report(new TurnEnded(TurnEndReason.ToolCallLimit)));
    }

    [Fact]
    public void AFaultedScan_BlocksAndResumesTheScan()
    {
        var run = new Run();
        run.Started();

        var stop = Assert.IsType<StopStep>(run.Report(new TurnEnded(TurnEndReason.Faulted, Detail: "The provider failed.")));

        Assert.Equal(StopReason.TurnFaulted, stop.Reason);
        Assert.Equal(new StartTurnStep(TurnKind.Scan, BriefKind.Resume, SessionScope.Scan), run.State.ResumePoint);
    }

    [Fact]
    public void ScanOnly_StopsAfterTheScan_SayingWhatItWouldAskAndAssume()
    {
        var run = new Run(limits: Scanning with { ScanOnly = true });
        run.Started();

        var stop = Assert.IsType<StopStep>(run.Scanned(Asked("What happens to version-1 files?"), Assumed("Sweep?")));

        Assert.Equal((LifecycleTrigger.Block, StopReason.ScanOnly), (stop.Trigger, stop.Reason));
        Assert.StartsWith("Scan only. Would ask: What happens to version-1 files?. Decision scan: 2 open choices, asking you 1.", stop.Message);
        Assert.Null(run.State.ResumePoint);
        Assert.Null(run.State.OpenDecision);
    }

    [Fact]
    public void TheCheckpointKeepsThePlan_TheQuestionsStillToAsk_AndTheAssumptions()
    {
        var run = new Run();
        run.Started();
        run.Scanned(Asked("Q1?"), Asked("Q2?"), Assumed("Sweep?"));

        var restored = RunStateJson.Deserialize(RunStateJson.Serialize(run.State));

        Assert.Equal("Store expiry per record.", restored.Plan!.Approach);
        Assert.Equal("Q2?", Assert.Single(restored.PendingQuestions).Question);
        Assert.Equal(["Sweep: On read"], restored.Assumptions);
        Assert.Equal(TurnKind.Scan, restored.WorkTurn);
    }
}
