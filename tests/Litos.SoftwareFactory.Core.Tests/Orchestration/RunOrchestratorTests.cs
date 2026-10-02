using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// The §8.5 run sequence, driven the way the host drives it: report an outcome, get the next
/// step. <see cref="Run"/> is a tiny fake host that records every step it was told to take.
/// </summary>
public class RunOrchestratorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Run(RunKind kind = RunKind.Implement, RunLimits? limits = null)
    {
        private readonly RunOrchestrator _orchestrator = new(limits);

        public RunState State { get; private set; } = RunOrchestrator.NewRun(kind);

        public List<RunStep> Steps { get; } = [];

        public DateTimeOffset Now { get; set; } = T0;

        public RunStep Last => Steps[^1];

        public RunStep Report(StepOutcome outcome)
        {
            var transition = _orchestrator.Next(State, outcome, Now);
            State = transition.State;
            Steps.Add(transition.Step);
            return transition.Step;
        }

        /// <summary>Starts the run and passes preflight, leaving the first agent turn in progress.</summary>
        public Run Started()
        {
            Report(new RunStarted());
            Report(new PreflightCompleted(true));
            return this;
        }

        public RunStep Submit() => Report(new TurnEnded(TurnEndReason.Completed, Work(), FilesChanged: true));

        public RunStep Verify(VerificationOutcome outcome, VerificationOutcome? baseline = null) => Report(new Verified(outcome, baseline));

        public RunStep ReviewWith(params ReviewFinding[] findings) =>
            Report(new TurnEnded(TurnEndReason.Completed, new ReviewSubmission(findings)));

        /// <summary>Implement, pass verification, and leave the review turn in progress.</summary>
        public Run AtReview()
        {
            Started();
            Submit();
            Verify(Passing());
            return this;
        }
    }

    private static WorkSubmission Work(string summary = "done") => new(summary, [], [], [], []);

    private static DecisionSubmission Decision(string question = "All rows or the current page?") =>
        new(question, "The request does not say.", ["All rows", "Current page"], "All rows");

    private static ReviewFinding Blocking(string text = "Null reference when the list is empty.") =>
        new(FindingSeverity.Blocking, "src/Orders.cs", 42, text);

    private static ReviewFinding Minor(string text = "Leftover debug output.") => new(FindingSeverity.Minor, "src/Orders.cs", 7, text);

    private static VerificationOutcome Passing(params string[] passingTests) => new(
        BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.Met,
        [.. (passingTests.Length == 0 ? ["T.Passes"] : passingTests).Select(n => new TestCaseResult(n, TestOutcome.Passed))],
        new ChangedLineCoverage(9, 10, []), [], []);

    private static VerificationOutcome Failing(params string[] failingTests) => new(
        BuildStatus.Passed, UnitTestStatus.Failed, CoverageStatus.NotMeasured,
        [.. failingTests.Select(n => new TestCaseResult(n, TestOutcome.Failed, "boom"))], null, [], []);

    private static VerificationOutcome BuildFailed() => new(
        BuildStatus.Failed, UnitTestStatus.NotRun, CoverageStatus.NotMeasured, [], null, [], []);

    private static VerificationOutcome CoverageShort() => new(
        BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.BelowThreshold,
        [new TestCaseResult("T.Passes", TestOutcome.Passed)],
        new ChangedLineCoverage(5, 10, [new UncoveredLines("src/Orders.cs", [3, 4, 5, 6, 7])]), [], []);

    private static StopStep AssertStopped(RunStep step, LifecycleTrigger trigger, StopReason reason)
    {
        var stop = Assert.IsType<StopStep>(step);
        Assert.Equal(trigger, stop.Trigger);
        Assert.Equal(reason, stop.Reason);
        Assert.False(string.IsNullOrWhiteSpace(stop.Message));
        return stop;
    }

    private static StartTurnStep AssertTurn(RunStep step, TurnKind kind, BriefKind brief, SessionScope session = SessionScope.Thread)
    {
        var turn = Assert.IsType<StartTurnStep>(step);
        Assert.Equal(kind, turn.Kind);
        Assert.Equal(brief, turn.Brief);
        Assert.Equal(session, turn.Session);
        return turn;
    }

    // ---- The straight path ----

    [Fact]
    public void Success_PreflightImplementVerifyReviewHandoff()
    {
        var run = new Run();

        Assert.IsType<PreflightStep>(run.Report(new RunStarted()));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Implement, BriefKind.Run);
        Assert.IsType<VerifyStep>(run.Submit());
        AssertTurn(run.Verify(Passing()), TurnKind.Review, BriefKind.Review, SessionScope.Review);
        Assert.IsType<HandoffStep>(run.ReviewWith());
        AssertStopped(run.Report(new HandoffCompleted(true)), LifecycleTrigger.Handoff, StopReason.HandedOff);

        Assert.Equal(0, run.State.RepairCyclesUsed);
        Assert.Equal(ReviewStatus.Clean, run.State.Review);
        Assert.Empty(run.State.Disclosures);
        Assert.Null(run.State.ResumePoint); // a handed-off run is finished, not resumable
    }

    [Fact]
    public void Success_KeepsTheSubmissionAndTheVerificationForTheHandoff()
    {
        var run = new Run().Started();
        var verification = Passing();

        run.Report(new TurnEnded(TurnEndReason.Completed, Work("Added CSV export"), FilesChanged: true));
        run.Verify(verification);

        Assert.Equal("Added CSV export", run.State.LastSubmission!.Summary);
        Assert.Same(verification, run.State.LastVerification);
    }

    [Fact]
    public void Stage_FollowsTheRun()
    {
        var run = new Run();
        run.Report(new RunStarted());
        run.Report(new PreflightCompleted(true));
        Assert.Equal(Stage.Implement, run.State.Stage);
        run.Submit();
        Assert.Equal(Stage.Verify, run.State.Stage);
        run.Verify(Passing());
        Assert.Equal(Stage.Review, run.State.Stage);
        run.ReviewWith();
        Assert.Equal(Stage.Handoff, run.State.Stage);
        run.Report(new HandoffCompleted(true));
        Assert.Equal(Stage.Handoff, run.State.Stage); // awaiting human testing sits in Handoff
    }

    // ---- Preflight ----

    [Fact]
    public void PreflightFails_BlocksWithTheReason_AndResumeRetriesPreflight()
    {
        var run = new Run();
        run.Report(new RunStarted());

        var stop = AssertStopped(
            run.Report(new PreflightCompleted(false, "The working copy has uncommitted changes.")),
            LifecycleTrigger.Block, StopReason.PreflightFailed);
        Assert.Equal("The working copy has uncommitted changes.", stop.Message);

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Implement, BriefKind.Run);
    }

    // ---- Repair after verification ----

    [Fact]
    public void FailingTest_StartsARepairTurn_ThenVerifiesAgain()
    {
        var run = new Run().Started();
        run.Submit();

        AssertTurn(run.Verify(Failing("T.Export")), TurnKind.Repair, BriefKind.Repair);
        Assert.Equal(1, run.State.RepairCyclesUsed);
        Assert.Equal(Stage.Implement, run.State.Stage);

        Assert.IsType<VerifyStep>(run.Submit());
        AssertTurn(run.Verify(Passing()), TurnKind.Review, BriefKind.Review, SessionScope.Review);
    }

    [Fact]
    public void BuildFailure_StartsARepairTurn()
    {
        var run = new Run().Started();
        run.Submit();

        AssertTurn(run.Verify(BuildFailed()), TurnKind.Repair, BriefKind.Repair);
    }

    [Fact]
    public void TestCommandWithNoReport_IsAFailure_NeverAPass()
    {
        var run = new Run().Started();
        run.Submit();
        var noReport = new VerificationOutcome(
            BuildStatus.Passed, UnitTestStatus.Failed, CoverageStatus.NotMeasured, [], null, [], ["Failed (no report)"]);

        AssertTurn(run.Verify(noReport), TurnKind.Repair, BriefKind.Repair);
    }

    [Fact]
    public void RepairThatChangesTheFailingSet_UsesTheNextCycle()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.A", "T.B"));
        run.Submit();

        AssertTurn(run.Verify(Failing("T.B")), TurnKind.Repair, BriefKind.Repair);
        Assert.Equal(2, run.State.RepairCyclesUsed);
    }

    [Fact]
    public void RepairThatMakesNoDifference_Blocks_EvenWithACycleLeft()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.A", "T.B"));
        run.Submit();

        var stop = AssertStopped(run.Verify(Failing("T.B", "T.A")), LifecycleTrigger.Block, StopReason.RepairMadeNoDifference);

        Assert.Contains("2 tests", stop.Message);
        Assert.Equal(1, run.State.RepairCyclesUsed);
    }

    [Fact]
    public void BuildStillFailingAfterRepair_IsNoDifference()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(BuildFailed());
        run.Submit();

        var stop = AssertStopped(run.Verify(BuildFailed()), LifecycleTrigger.Block, StopReason.RepairMadeNoDifference);
        Assert.Contains("the build", stop.Message);
    }

    [Fact]
    public void PersistentFailure_BlocksAfterTwoRepairCycles()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.A"));
        run.Submit();
        run.Verify(Failing("T.B"));
        run.Submit();

        var stop = AssertStopped(run.Verify(Failing("T.C")), LifecycleTrigger.Block, StopReason.RepairCyclesExhausted);

        Assert.Contains("2 repair cycles", stop.Message);
        Assert.DoesNotContain(run.Steps, s => s is HandoffStep); // never handed off as if it passed
    }

    [Fact]
    public void RepairCycleLimit_IsConfigurable()
    {
        var run = new Run(limits: new RunLimits { MaxRepairCycles = 0 }).Started();
        run.Submit();

        AssertStopped(run.Verify(Failing("T.A")), LifecycleTrigger.Block, StopReason.RepairCyclesExhausted);
    }

    [Fact]
    public void FailureThatAlsoFailsOnTheBaseline_IsPreExisting_NotTheTasksFailure()
    {
        var run = new Run().Started();
        run.Submit();

        var step = run.Verify(Failing("T.AlreadyBroken"), baseline: Failing("T.AlreadyBroken"));

        AssertTurn(step, TurnKind.Review, BriefKind.Review, SessionScope.Review);
        Assert.Equal(0, run.State.RepairCyclesUsed);
    }

    [Fact]
    public void NewFailureBesideAPreExistingOne_IsRepaired()
    {
        var run = new Run().Started();
        run.Submit();

        var step = run.Verify(Failing("T.AlreadyBroken", "T.New"), baseline: Failing("T.AlreadyBroken"));

        AssertTurn(step, TurnKind.Repair, BriefKind.Repair);
        Assert.Equal(["T.New"], run.State.FailuresBeforeRepair);
    }

    [Fact]
    public void Baseline_IsRememberedForLaterVerifications()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.AlreadyBroken", "T.New"), baseline: Failing("T.AlreadyBroken"));
        run.Submit();

        // The second verification is reported without a baseline; the first one's still applies.
        AssertTurn(run.Verify(Failing("T.AlreadyBroken")), TurnKind.Review, BriefKind.Review, SessionScope.Review);
    }

    [Fact]
    public void VerificationUnavailable_Blocks_AndResumeVerifiesAgain()
    {
        var run = new Run().Started();
        run.Submit();
        var unavailable = new VerificationOutcome(
            BuildStatus.Unavailable, UnitTestStatus.NotRun, CoverageStatus.NotMeasured, [], null, [], ["'dotnet' was not found."]);

        var stop = AssertStopped(run.Verify(unavailable), LifecycleTrigger.Block, StopReason.VerificationUnavailable);
        Assert.Contains("'dotnet' was not found.", stop.Message);
        Assert.Equal(0, run.State.RepairCyclesUsed); // a missing toolchain is not the agent's to repair

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        Assert.IsType<VerifyStep>(run.Report(new PreflightCompleted(true)));
    }

    // ---- Coverage ----

    [Fact]
    public void CoverageBelowThreshold_TriggersARepairCycle()
    {
        var run = new Run().Started();
        run.Submit();

        AssertTurn(run.Verify(CoverageShort()), TurnKind.Repair, BriefKind.Repair);
        Assert.Null(run.State.FailuresBeforeRepair); // nothing is failing; only coverage is short
    }

    [Fact]
    public void CoveragePersistentlyBelow_IsDisclosedInTheHandoff_NotBlocked()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(CoverageShort());
        run.Submit();
        run.Verify(CoverageShort());
        run.Submit();

        AssertTurn(run.Verify(CoverageShort()), TurnKind.Review, BriefKind.Review, SessionScope.Review);
        var disclosure = Assert.Single(run.State.Disclosures);
        Assert.Contains("50%", disclosure);
        Assert.Contains("below the project threshold", disclosure);
    }

    // ---- Review ----

    [Fact]
    public void MinorFindingsOnly_GoStraightToHandoff_AndStayOpen()
    {
        var run = new Run().AtReview();

        Assert.IsType<HandoffStep>(run.ReviewWith(Minor()));
        Assert.Equal(ReviewStatus.FindingsOpen, run.State.Review);
        Assert.Single(run.State.Findings);
        Assert.Equal(0, run.State.RepairCyclesUsed);
    }

    [Fact]
    public void BlockingFinding_LeadsToOneRepair_OneVerify_ThenHandoff_WithoutReviewingAgain()
    {
        var run = new Run().AtReview();

        AssertTurn(run.ReviewWith(Blocking(), Minor()), TurnKind.Repair, BriefKind.Repair);
        Assert.True(run.State.ReviewRepairPending);
        Assert.Equal(1, run.State.RepairCyclesUsed);

        Assert.IsType<VerifyStep>(run.Submit());
        Assert.IsType<HandoffStep>(run.Verify(Passing()));

        Assert.Equal(ReviewStatus.FindingsFixed, run.State.Review);
        Assert.Single(run.Steps.OfType<StartTurnStep>(), s => s.Brief == BriefKind.Review);
    }

    [Fact]
    public void RepairCycles_AreSharedBetweenTestFailuresAndReviewFindings()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.A"));     // cycle 1
        run.Submit();
        run.Verify(Failing("T.B"));     // cycle 2
        run.Submit();
        run.Verify(Passing());           // review starts

        // Both cycles went on test failures, so none is left for the review's finding: the run
        // stops rather than handing a known blocking issue to a tester.
        var stop = AssertStopped(run.ReviewWith(Blocking(), Minor()), LifecycleTrigger.Block, StopReason.RepairCyclesExhausted);

        Assert.Contains("1 blocking issue", stop.Message);
        Assert.Equal(2, run.State.RepairCyclesUsed);
        Assert.Equal(ReviewStatus.FindingsOpen, run.State.Review);
        Assert.Equal(2, run.State.Findings.Count);
        Assert.Equal(Stage.Review, run.State.Stage);
        Assert.DoesNotContain(run.Steps, s => s is HandoffStep);
    }

    [Fact]
    public void BlockedOnReviewFindings_Resume_RepairsThem_Verifies_AndHandsOff_WithoutReviewingAgain()
    {
        var run = new Run(limits: new RunLimits { MaxRepairCycles = 0 }).AtReview();
        var stop = AssertStopped(run.ReviewWith(Blocking(), Blocking("Second issue.")), LifecycleTrigger.Block, StopReason.RepairCyclesExhausted);
        Assert.Contains("2 blocking issues", stop.Message);

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Repair, BriefKind.Repair);
        Assert.True(run.State.ReviewRepairPending); // so the repair brief lists the findings
        Assert.Equal(1, run.State.RepairCyclesUsed); // this repair is the first cycle of the fresh allowance

        Assert.IsType<VerifyStep>(run.Submit());
        Assert.IsType<HandoffStep>(run.Verify(Passing()));
        Assert.Equal(ReviewStatus.FindingsFixed, run.State.Review);
        Assert.Single(run.Steps.OfType<StartTurnStep>(), s => s.Brief == BriefKind.Review);
    }

    [Fact]
    public void MinorFindings_WithNoRepairCyclesLeft_StillHandOff()
    {
        var run = new Run(limits: new RunLimits { MaxRepairCycles = 0 }).AtReview();

        Assert.IsType<HandoffStep>(run.ReviewWith(Minor()));
    }

    [Fact]
    public void ReviewRepairThatBreaksATest_UsesTheRemainingCycle()
    {
        var run = new Run().AtReview();
        run.ReviewWith(Blocking());      // cycle 1
        run.Submit();

        AssertTurn(run.Verify(Failing("T.Regression")), TurnKind.Repair, BriefKind.Repair); // cycle 2
        run.Submit();
        Assert.IsType<HandoffStep>(run.Verify(Passing()));
        Assert.Equal(ReviewStatus.FindingsFixed, run.State.Review);
    }

    [Fact]
    public void ReviewRepairThatStillFails_WithNoCyclesLeft_Blocks()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.A"));      // cycle 1
        run.Submit();
        run.Verify(Passing());
        run.ReviewWith(Blocking());      // cycle 2
        run.Submit();

        AssertStopped(run.Verify(Failing("T.Regression")), LifecycleTrigger.Block, StopReason.RepairCyclesExhausted);
    }

    [Fact]
    public void ReviewEndsWithoutSubmitReview_IsNudgedInTheReviewSession_ThenBlocked()
    {
        var run = new Run().AtReview();

        AssertTurn(run.Report(new TurnEnded(TurnEndReason.Completed)), TurnKind.Nudge, BriefKind.Nudge, SessionScope.Review);
        Assert.Equal(Stage.Review, run.State.Stage);

        var stop = AssertStopped(run.Report(new TurnEnded(TurnEndReason.Completed)), LifecycleTrigger.Block, StopReason.NoCompletionCall);
        Assert.Contains("submit_review", stop.Message);
    }

    [Fact]
    public void ReviewNudge_ThatThenSubmits_Continues()
    {
        var run = new Run().AtReview();
        run.Report(new TurnEnded(TurnEndReason.Completed));

        Assert.IsType<HandoffStep>(run.ReviewWith());
    }

    [Fact]
    public void WorkSubmissionDuringReview_IsNotAReview()
    {
        var run = new Run().AtReview();

        AssertTurn(
            run.Report(new TurnEnded(TurnEndReason.Completed, Work())), TurnKind.Nudge, BriefKind.Nudge, SessionScope.Review);
    }

    // ---- Decisions ----

    [Fact]
    public void Decision_StopsAwaitingDecision_AndTheAnswerResumesTheSameWork()
    {
        var run = new Run().Started();

        var stop = AssertStopped(
            run.Report(new TurnEnded(TurnEndReason.Completed, Decision(), FilesChanged: true)),
            LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded);
        Assert.Equal("All rows or the current page?", stop.Message);
        Assert.Equal(1, run.State.DecisionsAsked);
        Assert.NotNull(run.State.OpenDecision);

        Assert.IsType<PreflightStep>(run.Report(new DecisionAnswered("All filtered rows.")));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Implement, BriefKind.DecisionAnswer);

        Assert.Null(run.State.OpenDecision);
        Assert.Equal(new AnsweredDecision("All rows or the current page?", "All filtered rows."), Assert.Single(run.State.Decisions));
    }

    [Fact]
    public void Decision_DuringARepairTurn_ResumesAsARepairTurn()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.A"));
        run.Report(new TurnEnded(TurnEndReason.Completed, Decision()));
        run.Report(new DecisionAnswered("Yes."));

        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Repair, BriefKind.DecisionAnswer);
        Assert.Equal(1, run.State.RepairCyclesUsed); // answering is not another repair cycle
    }

    [Fact]
    public void AwaitingDecision_CannotBeResumedWithoutAnAnswer()
    {
        var run = new Run().Started();
        run.Report(new TurnEnded(TurnEndReason.Completed, Decision()));

        Assert.Throws<InvalidOperationException>(() => run.Report(new Resumed()));
    }

    [Fact]
    public void DecisionAnswered_WithNoOpenDecision_IsRejected()
    {
        var run = new Run().Started();

        Assert.Throws<InvalidOperationException>(() => run.Report(new DecisionAnswered("answer")));
    }

    [Fact]
    public void FourthDecision_IsNotAsked_TheFactoryProceedsOnTheRecommendationAndDisclosesIt()
    {
        var run = new Run().Started();
        for (var i = 1; i <= 3; i++)
        {
            AssertStopped(
                run.Report(new TurnEnded(TurnEndReason.Completed, Decision($"Question {i}?"))),
                LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded);
            run.Report(new DecisionAnswered($"Answer {i}"));
            run.Report(new PreflightCompleted(true));
        }

        var step = run.Report(new TurnEnded(TurnEndReason.Completed, Decision("Question 4?")));

        AssertTurn(step, TurnKind.Nudge, BriefKind.ProceedOnRecommendation);
        Assert.Equal(3, run.State.DecisionsAsked);
        Assert.Null(run.State.OpenDecision);
        Assert.Contains("Question 4?", Assert.Single(run.State.Disclosures));

        Assert.IsType<VerifyStep>(run.Submit());
    }

    [Fact]
    public void AskingAgainAfterBeingToldToProceed_Blocks_RatherThanLooping()
    {
        var run = new Run(limits: new RunLimits { MaxDecisions = 0 }).Started();
        run.Report(new TurnEnded(TurnEndReason.Completed, Decision(), FilesChanged: true));

        AssertStopped(
            run.Report(new TurnEnded(TurnEndReason.Completed, Decision(), FilesChanged: true)),
            LifecycleTrigger.Block, StopReason.NoCompletionCall);
    }

    // ---- No completion call, no progress ----

    [Fact]
    public void TurnEndsWithoutACompletionTool_GetsOneNudge()
    {
        var run = new Run().Started();

        AssertTurn(run.Report(new TurnEnded(TurnEndReason.Completed, FilesChanged: true)), TurnKind.Nudge, BriefKind.Nudge);
        Assert.Equal(TurnKind.Implement, run.State.WorkTurn);
        Assert.Equal(TurnKind.Nudge, run.State.CurrentTurn);
    }

    [Fact]
    public void NudgeThenStillNoCompletionTool_Blocks()
    {
        var run = new Run().Started();
        run.Report(new TurnEnded(TurnEndReason.Completed, FilesChanged: true));

        var stop = AssertStopped(
            run.Report(new TurnEnded(TurnEndReason.Completed, FilesChanged: true)), LifecycleTrigger.Block, StopReason.NoCompletionCall);

        Assert.Contains("submit_work or request_decision", stop.Message);
        Assert.Single(run.Steps.OfType<StartTurnStep>(), s => s.Kind == TurnKind.Nudge); // exactly one nudge
    }

    [Fact]
    public void NudgeThenSubmitWork_ContinuesToVerify()
    {
        var run = new Run().Started();
        run.Report(new TurnEnded(TurnEndReason.Completed, FilesChanged: true));

        Assert.IsType<VerifyStep>(run.Submit());
    }

    [Fact]
    public void EachWorkTurn_GetsItsOwnNudge()
    {
        var run = new Run().Started();
        run.Report(new TurnEnded(TurnEndReason.Completed, FilesChanged: true)); // nudge 1
        run.Submit();
        run.Verify(Failing("T.A"));                                              // repair turn

        AssertTurn(run.Report(new TurnEnded(TurnEndReason.Completed, FilesChanged: true)), TurnKind.Nudge, BriefKind.Nudge);
    }

    [Fact]
    public void TurnChangesNothingAndCallsNothing_BlocksAtOnce()
    {
        var run = new Run().Started();

        var stop = AssertStopped(
            run.Report(new TurnEnded(TurnEndReason.Completed, FilesChanged: false)), LifecycleTrigger.Block, StopReason.NoProgress);

        Assert.Contains("changed no files", stop.Message);
        Assert.DoesNotContain(run.Steps, s => s is StartTurnStep { Kind: TurnKind.Nudge });
    }

    // ---- Budget, pause, cancel, faults ----

    [Fact]
    public void BudgetRefused_PausesForBudget_AndResumesTheSameWorkWithItsCounts()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.A")); // repair cycle 1 in progress

        var stop = AssertStopped(
            run.Report(new TurnEnded(TurnEndReason.BudgetRefused, Detail: "Needs 20,900 tokens; 20,000 remain.")),
            LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted);
        Assert.Equal("Needs 20,900 tokens; 20,000 remain.", stop.Message);

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Repair, BriefKind.Resume);
        Assert.Equal(1, run.State.RepairCyclesUsed); // a budget pause does not reset the allowance
    }

    [Fact]
    public void BudgetRefused_DuringReview_ResumesInTheReviewSession()
    {
        var run = new Run().AtReview();
        run.Report(new TurnEnded(TurnEndReason.BudgetRefused));
        run.Report(new Resumed());

        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Review, BriefKind.Resume, SessionScope.Review);
    }

    [Fact]
    public void PausedByUser_StopsAsPaused_AndResumes()
    {
        var run = new Run().Started();

        AssertStopped(run.Report(new TurnEnded(TurnEndReason.PausedByUser)), LifecycleTrigger.Pause, StopReason.PausedByUser);

        run.Report(new Resumed());
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Implement, BriefKind.Resume);
    }

    [Fact]
    public void Cancelled_Stops_AndCannotBeResumed()
    {
        var run = new Run().Started();

        var stop = AssertStopped(run.Report(new TurnEnded(TurnEndReason.Cancelled)), LifecycleTrigger.Cancel, StopReason.Cancelled);

        Assert.Contains("kept", stop.Message); // cancellation never reverts edits
        Assert.Throws<InvalidOperationException>(() => run.Report(new Resumed()));
    }

    [Theory]
    [InlineData(TurnEndReason.Faulted, StopReason.TurnFaulted)]
    [InlineData(TurnEndReason.ToolCallLimit, StopReason.ToolCallLimit)]
    [InlineData(TurnEndReason.TimeLimit, StopReason.TurnTimeLimit)]
    public void TurnFailures_Block(TurnEndReason reason, StopReason expected)
    {
        var run = new Run().Started();

        AssertStopped(run.Report(new TurnEnded(reason)), LifecycleTrigger.Block, expected);
        Assert.Equal(new StartTurnStep(TurnKind.Implement, BriefKind.Resume), run.State.ResumePoint);
    }

    [Fact]
    public void Faulted_CarriesTheWorkersError()
    {
        var run = new Run().Started();

        var stop = (StopStep)run.Report(new TurnEnded(TurnEndReason.Faulted, Detail: "Compaction failed: provider returned 500."));

        Assert.Equal("Compaction failed: provider returned 500.", stop.Message);
    }

    [Fact]
    public void LimitMessages_StateTheConfiguredLimit()
    {
        var run = new Run(limits: new RunLimits { MaxToolCallsPerTurn = 50, TurnTimeout = TimeSpan.FromMinutes(10) }).Started();
        Assert.Contains("50 tool calls", ((StopStep)run.Report(new TurnEnded(TurnEndReason.ToolCallLimit))).Message);

        var other = new Run(limits: new RunLimits { TurnTimeout = TimeSpan.FromMinutes(10) }).Started();
        Assert.Contains("10 minutes", ((StopStep)other.Report(new TurnEnded(TurnEndReason.TimeLimit))).Message);
    }

    [Fact]
    public void ResumeAfterABlock_GrantsAFreshRepairAllowance()
    {
        var run = new Run().Started();
        run.Submit();
        run.Verify(Failing("T.A"));
        run.Submit();
        run.Verify(Failing("T.B"));
        run.Submit();
        run.Verify(Failing("T.C")); // blocked: cycles exhausted

        run.Report(new Resumed());
        Assert.IsType<VerifyStep>(run.Report(new PreflightCompleted(true)));

        Assert.Equal(0, run.State.RepairCyclesUsed);
        AssertTurn(run.Verify(Failing("T.C")), TurnKind.Repair, BriefKind.Repair);
    }

    // ---- Handoff ----

    [Fact]
    public void PushFailure_BlocksWithTheReason_AndResumeRetriesTheHandoff()
    {
        var run = new Run().AtReview();
        run.ReviewWith();

        var stop = AssertStopped(
            run.Report(new HandoffCompleted(false, "Push rejected: protected branch.")), LifecycleTrigger.Block, StopReason.HandoffFailed);
        Assert.Equal("Push rejected: protected branch.", stop.Message);
        Assert.Equal(Stage.Handoff, run.State.Stage);

        run.Report(new Resumed());
        Assert.IsType<HandoffStep>(run.Report(new PreflightCompleted(true)));
        AssertStopped(run.Report(new HandoffCompleted(true)), LifecycleTrigger.Handoff, StopReason.HandedOff);
    }

    // ---- Rework ----

    [Fact]
    public void Rework_StartsWithAReworkTurn_ThenFollowsTheSameSequence()
    {
        var run = new Run(RunKind.Rework);

        Assert.IsType<PreflightStep>(run.Report(new RunStarted()));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Rework, BriefKind.Rework);
        Assert.IsType<VerifyStep>(run.Submit());
        AssertTurn(run.Verify(Failing("T.Comma")), TurnKind.Repair, BriefKind.Repair);
        run.Submit();
        AssertTurn(run.Verify(Passing()), TurnKind.Review, BriefKind.Review, SessionScope.Review);
        Assert.IsType<HandoffStep>(run.ReviewWith());
        AssertStopped(run.Report(new HandoffCompleted(true)), LifecycleTrigger.Handoff, StopReason.HandedOff);
    }

    [Fact]
    public void Rework_IsANewRun_WithItsOwnLimits()
    {
        var state = RunOrchestrator.NewRun(RunKind.Rework);

        Assert.Equal(0, state.RepairCyclesUsed);
        Assert.Equal(0, state.DecisionsAsked);
        Assert.False(state.ReviewCompleted);
    }

    // ---- Run time limit ----

    [Fact]
    public void RunTimeLimit_BlocksBeforeTheNextStep_AndResumeTakesThatStep()
    {
        var run = new Run().Started();
        run.Now = T0 + TimeSpan.FromHours(3) + TimeSpan.FromSeconds(1);

        var stop = AssertStopped(run.Submit(), LifecycleTrigger.Block, StopReason.RunTimeLimit);
        Assert.Contains("3 hours", stop.Message);

        run.Report(new Resumed());
        Assert.IsType<VerifyStep>(run.Report(new PreflightCompleted(true)));
    }

    [Fact]
    public void RunTimeLimit_OfOneHour_ReadsAsOneHour()
    {
        var run = new Run(limits: new RunLimits { RunTimeout = TimeSpan.FromHours(1) }).Started();
        run.Now = T0 + TimeSpan.FromHours(2);

        Assert.Contains("1 hour.", ((StopStep)run.Submit()).Message);
    }

    [Fact]
    public void RunTimeLimit_CountsFromTheLastResume_NotFromTheFirstStart()
    {
        var run = new Run().Started();
        run.Report(new TurnEnded(TurnEndReason.PausedByUser));
        run.Now = T0 + TimeSpan.FromHours(10); // paused overnight
        run.Report(new Resumed());
        run.Report(new PreflightCompleted(true));
        run.Now += TimeSpan.FromHours(1);

        Assert.IsType<VerifyStep>(run.Submit());
    }

    [Fact]
    public void RunTimeLimit_DoesNotReplaceAStopThatAlreadyHappened()
    {
        var run = new Run().AtReview();
        run.ReviewWith();
        run.Now = T0 + TimeSpan.FromHours(5);

        AssertStopped(run.Report(new HandoffCompleted(true)), LifecycleTrigger.Handoff, StopReason.HandedOff);
    }

    // ---- Recovering a run that was cut off mid-step ----
    //
    // When the host or its worker is lost, the checkpoint is the step that was under way, not a
    // stop with a resume point. The first real run hit this: it could be marked Interrupted but
    // recovering it failed with "A run in phase Turn cannot start".

    [Fact]
    public void Recovered_MidImplementTurn_RunsPreflight_ThenContinuesTheTurnInItsOwnSession()
    {
        var run = new Run().Started(); // the implement turn is in progress

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Implement, BriefKind.Resume);
    }

    [Fact]
    public void Recovered_MidReviewTurn_ContinuesTheReview_InTheReviewSession()
    {
        var run = new Run().AtReview();

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Review, BriefKind.Resume, SessionScope.Review);
    }

    [Fact]
    public void Recovered_MidNudge_ContinuesTheWorkTheNudgeWasFor_WithItsFollowUpAvailableAgain()
    {
        var run = new Run().AtReview();
        run.Report(new TurnEnded(TurnEndReason.Completed)); // no submit_review: the nudge starts
        Assert.Equal(TurnKind.Nudge, run.State.CurrentTurn);

        run.Report(new Resumed());

        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Review, BriefKind.Resume, SessionScope.Review);
        Assert.False(run.State.NudgeUsed);
    }

    [Fact]
    public void Recovered_MidVerification_VerifiesAgain()
    {
        var run = new Run().Started();
        Assert.IsType<VerifyStep>(run.Submit());

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        Assert.IsType<VerifyStep>(run.Report(new PreflightCompleted(true)));
        // What the agent submitted before the interruption is still what gets handed off.
        Assert.Equal("done", run.State.LastSubmission!.Summary);
    }

    [Fact]
    public void Recovered_MidHandoff_HandsOffAgain()
    {
        var run = new Run().AtReview();
        Assert.IsType<HandoffStep>(run.ReviewWith());

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        Assert.IsType<HandoffStep>(run.Report(new PreflightCompleted(true)));
    }

    [Fact]
    public void Recovered_MidPreflight_RunsPreflightAgain_ForTheSameNextStep()
    {
        var run = new Run();
        run.Report(new RunStarted()); // preflight is in progress

        Assert.IsType<PreflightStep>(run.Report(new Resumed()));
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Implement, BriefKind.Run);
    }

    /// <summary>An interruption is not a blocker somebody resolved: the run keeps the repair
    /// cycles it has used, and its run-time limit counts from the recovery.</summary>
    [Fact]
    public void Recovered_KeepsItsCounts_AndRestartsItsClock()
    {
        var run = new Run().Started();
        run.Submit();
        AssertTurn(run.Verify(Failing("T.Fails")), TurnKind.Repair, BriefKind.Repair);
        Assert.Equal(1, run.State.RepairCyclesUsed);

        run.Now = T0.AddHours(30);
        Assert.IsType<PreflightStep>(run.Report(new Resumed()));

        Assert.Equal(1, run.State.RepairCyclesUsed);
        Assert.Equal(T0.AddHours(30), run.State.ActiveSince);
        AssertTurn(run.Report(new PreflightCompleted(true)), TurnKind.Repair, BriefKind.Resume);
    }

    [Fact]
    public void Recovered_BeforeTheRunEverStarted_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new Run().Report(new Resumed()));
    }

    // ---- Misuse by the host ----

    [Fact]
    public void OutcomeForTheWrongStep_IsRejected()
    {
        var run = new Run().Started(); // a turn is in progress

        Assert.Throws<InvalidOperationException>(() => run.Report(new Verified(Passing())));
        Assert.Throws<InvalidOperationException>(() => run.Report(new HandoffCompleted(true)));
        Assert.Throws<InvalidOperationException>(() => run.Report(new PreflightCompleted(true)));
        Assert.Throws<InvalidOperationException>(() => run.Report(new RunStarted()));
    }

    [Fact]
    public void TurnEnded_BeforeAnyTurnStarted_IsRejected()
    {
        var run = new Run();

        Assert.Throws<InvalidOperationException>(() => run.Report(new TurnEnded(TurnEndReason.Completed)));
    }

    [Fact]
    public void State_IsNeverMutated_SoTheHostCanPersistEachTransition()
    {
        var orchestrator = new RunOrchestrator();
        var initial = RunOrchestrator.NewRun(RunKind.Implement);

        var next = orchestrator.Next(initial, new RunStarted(), T0);

        Assert.Equal(RunPhase.NotStarted, initial.Phase);
        Assert.Equal(RunPhase.Preflight, next.State.Phase);
    }

    /// <summary>Every stop the orchestrator can produce names a transition the lifecycle allows
    /// out of Running — otherwise the host could not apply it.</summary>
    [Theory]
    [InlineData(LifecycleTrigger.Handoff)]
    [InlineData(LifecycleTrigger.RequestDecision)]
    [InlineData(LifecycleTrigger.ExhaustBudget)]
    [InlineData(LifecycleTrigger.Pause)]
    [InlineData(LifecycleTrigger.Cancel)]
    [InlineData(LifecycleTrigger.Block)]
    public void StopTriggers_AreLegalFromRunning(LifecycleTrigger trigger)
    {
        Assert.True(TaskLifecycle.CanApply(LifecycleState.Running, trigger));
    }

    [Fact]
    public void Limits_DefaultToTheBlueprintsValues()
    {
        var limits = new RunLimits();

        Assert.Equal(2, limits.MaxRepairCycles);
        Assert.Equal(3, limits.MaxDecisions);
        Assert.Equal(200, limits.MaxToolCallsPerTurn);
        Assert.Equal(TimeSpan.FromMinutes(45), limits.TurnTimeout);
        Assert.Equal(TimeSpan.FromHours(3), limits.RunTimeout);
        Assert.Equal(1_500, limits.InlineReviewMaxChangedLines);
        Assert.Equal(4_000, limits.RepairExcerptTokens);
    }
}

/// <summary>
/// The checkpoint a run resumes from. A field that failed to come back would silently reset a
/// limit or lose a decision, so the whole state is compared after a round trip.
/// </summary>
public class RunStateJsonTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static RunState Drive(params StepOutcome[] outcomes)
    {
        var orchestrator = new RunOrchestrator();
        var state = RunOrchestrator.NewRun(RunKind.Implement);
        foreach (var outcome in outcomes)
            state = orchestrator.Next(state, outcome, T0).State;
        return state;
    }

    private static VerificationOutcome Failing() => new(
        BuildStatus.Passed, UnitTestStatus.Failed, CoverageStatus.BelowThreshold,
        [new TestCaseResult("T.Broken", TestOutcome.Failed, "boom", 0.5), new TestCaseResult("T.Fine", TestOutcome.Passed)],
        new ChangedLineCoverage(5, 10, [new UncoveredLines("src/Orders.cs", [3, 4])]) { UnmeasuredFiles = ["src/New.cs"] },
        [new CommandRun("api", "build", "dotnet build", 0, TimeSpan.FromSeconds(3), false, "logs/01.log")],
        ["a problem"]);

    private static void AssertSame(RunState expected, RunState actual) =>
        Assert.Equal(RunStateJson.Serialize(expected), RunStateJson.Serialize(actual));

    [Fact]
    public void RoundTrip_ARunInTheMiddleOfARepair_KeepsEverything()
    {
        var state = Drive(
            new RunStarted(), new PreflightCompleted(true),
            new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [new CriterionCoverage("works", ["T.A"])], ["T.A"], ["limit"], ["step"]), FilesChanged: true),
            new Verified(Failing(), Failing()));

        var back = RunStateJson.Deserialize(RunStateJson.Serialize(state));

        AssertSame(state, back);
        Assert.Equal(RunPhase.Turn, back.Phase);
        Assert.Equal(TurnKind.Repair, back.WorkTurn);
        Assert.Equal(1, back.RepairCyclesUsed);
        Assert.Equal("done", back.LastSubmission!.Summary);
        Assert.Equal(["T.A"], back.LastSubmission.Criteria[0].Tests);
        Assert.Equal(CoverageStatus.BelowThreshold, back.LastVerification!.Coverage);
        Assert.Equal("boom", back.LastVerification.Tests[0].Message);
        Assert.Equal([3, 4], back.LastVerification.ChangedLines!.Uncovered[0].Lines);
        Assert.Equal(["src/New.cs"], back.LastVerification.ChangedLines.UnmeasuredFiles);
        Assert.Equal(TimeSpan.FromSeconds(3), back.LastVerification.Commands[0].Duration);
        Assert.NotNull(back.Baseline);
        Assert.Equal(T0, back.ActiveSince);
    }

    [Fact]
    public void RoundTrip_AStoppedRun_KeepsItsStopItsResumePointAndItsOpenDecision()
    {
        var state = Drive(
            new RunStarted(), new PreflightCompleted(true),
            new TurnEnded(TurnEndReason.Completed, new DecisionSubmission("All rows?", "unclear", ["yes", "no"], "yes", "Orders")));

        var back = RunStateJson.Deserialize(RunStateJson.Serialize(state));

        AssertSame(state, back);
        Assert.Equal(new StopStep(LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded, "All rows?"), back.LastStop);
        Assert.Equal("All rows?", back.OpenDecision!.Question);
        Assert.Equal(["yes", "no"], back.OpenDecision.Options);
        Assert.Null(back.ResumePoint);
        Assert.Equal(1, back.DecisionsAsked);
    }

    [Fact]
    public void RoundTrip_ResumePointsOfEveryKind_ComeBackAsTheSameStep()
    {
        RunStep[] steps =
        [
            new PreflightStep(), new VerifyStep(), new HandoffStep(),
            new StartTurnStep(TurnKind.Review, BriefKind.Resume, SessionScope.Review),
            new StopStep(LifecycleTrigger.Block, StopReason.HandoffFailed, "Push rejected."),
        ];

        foreach (var step in steps)
        {
            var state = RunOrchestrator.NewRun(RunKind.Rework) with { ResumePoint = step, AfterPreflight = step };
            var back = RunStateJson.Deserialize(RunStateJson.Serialize(state));

            Assert.Equal(step, back.ResumePoint);
            Assert.Equal(step, back.AfterPreflight);
            Assert.Equal(RunKind.Rework, back.Kind);
        }
    }

    /// <summary>Rewrites JSON the way PostgreSQL's jsonb stores it: object keys ordered by
    /// length, then by their bytes. A step's "step" discriminator then no longer comes first.</summary>
    private static string AsJsonbStoresIt(string json)
    {
        static System.Text.Json.Nodes.JsonNode? Reorder(System.Text.Json.Nodes.JsonNode? node) => node switch
        {
            System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(o
                .OrderBy(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, Reorder(p.Value?.DeepClone())))),
            System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray([.. a.Select(item => Reorder(item?.DeepClone()))]),
            _ => node,
        };

        return Reorder(System.Text.Json.Nodes.JsonNode.Parse(json))!.ToJsonString();
    }

    /// <summary>The first real run failed here: the checkpoint is kept in a jsonb column, which
    /// rearranges keys, and the resume point could not be read back.</summary>
    [Fact]
    public void Deserialize_ACheckpointWhoseKeysWereRearrangedByTheDatabase_StillReadsEveryKindOfStep()
    {
        RunStep[] steps =
        [
            new PreflightStep(), new VerifyStep(), new HandoffStep(),
            new StartTurnStep(TurnKind.Implement, BriefKind.Resume),
            new StartTurnStep(TurnKind.Review, BriefKind.Resume, SessionScope.Review),
            new StopStep(LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted, "The task's budget has 31,834 left."),
        ];

        foreach (var step in steps)
        {
            var state = RunOrchestrator.NewRun(RunKind.Implement) with { ResumePoint = step, AfterPreflight = step };
            var stored = AsJsonbStoresIt(RunStateJson.Serialize(state));

            // The rearrangement is real: a turn step's "kind" now sorts ahead of its "step".
            if (step is StartTurnStep)
                Assert.DoesNotContain("\"resumePoint\":{\"step\"", stored);

            var back = RunStateJson.Deserialize(stored);
            Assert.Equal(step, back.ResumePoint);
            Assert.Equal(step, back.AfterPreflight);
        }
    }

    [Fact]
    public void Deserialize_ARearrangedCheckpointOfAPausedRun_ResumesWhereItStopped()
    {
        var resumeAt = new StartTurnStep(TurnKind.Implement, BriefKind.Resume);
        var paused = RunOrchestrator.NewRun(RunKind.Implement) with
        {
            Phase = RunPhase.Stopped,
            ResumePoint = resumeAt,
            LastStop = new StopStep(LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted, "The task's budget has 31,834 left."),
        };

        var back = RunStateJson.Deserialize(AsJsonbStoresIt(RunStateJson.Serialize(paused)));
        var resumed = new RunOrchestrator().Next(back, new Resumed(), new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(RunStateJson.Serialize(paused), RunStateJson.Serialize(back));
        Assert.IsType<PreflightStep>(resumed.Step);
        Assert.Equal(resumeAt, resumed.State.AfterPreflight);
    }

    [Fact]
    public void RoundTrip_ReviewStateDecisionsAndDisclosures()
    {
        var state = RunOrchestrator.NewRun(RunKind.Implement) with
        {
            ReviewCompleted = true,
            Review = ReviewStatus.FindingsOpen,
            ReviewRepairPending = true,
            NudgeUsed = true,
            Findings = [new ReviewFinding(FindingSeverity.Blocking, "src/Orders.cs", 42, "Crashes."), new ReviewFinding(FindingSeverity.Minor, "a.cs", null, "Nit.")],
            Decisions = [new AnsweredDecision("All rows?", "Yes.")],
            Disclosures = ["Coverage is below the threshold."],
            FailuresBeforeRepair = ["<build>", "T.A"],
        };

        var back = RunStateJson.Deserialize(RunStateJson.Serialize(state));

        AssertSame(state, back);
        Assert.Equal(state.Findings, back.Findings);
        Assert.Equal(state.Decisions, back.Decisions);
        Assert.Equal(state.Disclosures, back.Disclosures);
        Assert.Equal(["<build>", "T.A"], back.FailuresBeforeRepair);
        Assert.True(back.ReviewRepairPending && back.ReviewCompleted && back.NudgeUsed);
    }

    /// <summary>A resumed run must behave exactly as if the host had never restarted.</summary>
    [Fact]
    public void ARunRestoredFromItsCheckpoint_ContinuesTheSameWay()
    {
        var orchestrator = new RunOrchestrator();
        var live = Drive(
            new RunStarted(), new PreflightCompleted(true),
            new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [], [], [], []), FilesChanged: true),
            new Verified(Failing()));
        var restored = RunStateJson.Deserialize(RunStateJson.Serialize(live));
        var next = new TurnEnded(TurnEndReason.Completed, new WorkSubmission("fixed", [], [], [], []), FilesChanged: true);

        var fromLive = orchestrator.Next(live, next, T0);
        var fromRestored = orchestrator.Next(restored, next, T0);

        Assert.Equal(fromLive.Step, fromRestored.Step);
        AssertSame(fromLive.State, fromRestored.State);
    }

    [Fact]
    public void Deserialize_Empty_Throws()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => RunStateJson.Deserialize("null"));
    }
}

public class ContextPolicyTests
{
    [Theory]
    [InlineData(TurnKind.Rework, 0.61, true)]
    [InlineData(TurnKind.Repair, 0.90, true)]
    [InlineData(TurnKind.Rework, 0.60, false)] // above 60%, not at it
    [InlineData(TurnKind.Repair, 0.10, false)]
    [InlineData(TurnKind.Implement, 0.95, false)]
    [InlineData(TurnKind.Review, 0.95, false)]
    [InlineData(TurnKind.Nudge, 0.95, false)]
    [InlineData(TurnKind.Chat, 0.95, false)]
    public void ShouldCompactBefore_OnlyLargeTurnsAboveSixtyPercent(TurnKind kind, double fraction, bool expected)
    {
        Assert.Equal(expected, ContextPolicy.ShouldCompactBefore(kind, fraction));
    }

    [Fact]
    public void ShouldCompactBefore_UnknownUsage_DoesNotCompact()
    {
        Assert.False(ContextPolicy.ShouldCompactBefore(TurnKind.Rework, null));
    }
}
