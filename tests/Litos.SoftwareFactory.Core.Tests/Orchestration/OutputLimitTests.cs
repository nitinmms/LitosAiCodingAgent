using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// A model call that spends its whole output allowance reasoning, without replying. On F7 a light
/// review of one test file did that twice, and the task sat blocked at its last step. A review or
/// a decision scan never blocks the task for it; an implementation turn still does.
/// </summary>
public class OutputLimitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly TurnEnded CutOff = new(TurnEndReason.OutputLimit, Detail: "The model reached the output limit of 32,768 tokens without producing a reply (36,006 of them were reasoning).");

    private sealed class Run(RunLimits? limits = null)
    {
        private readonly RunOrchestrator _orchestrator = new(limits);

        public RunState State { get; private set; } = RunOrchestrator.NewRun(RunKind.Implement);

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

        /// <summary>Implement and pass verification, leaving the review turn in progress.</summary>
        public RunStep AtReview()
        {
            Started();
            Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [], [], [], []), FilesChanged: true));
            return Report(new Verified(new VerificationOutcome(BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.NotMeasured, [], null, [], [])));
        }
    }

    [Fact]
    public void AReviewCutOffOnce_RunsTheReviewAgain_WithoutStopping()
    {
        var run = new Run();
        Assert.Equal(new StartTurnStep(TurnKind.Review, BriefKind.Review, SessionScope.Review), run.AtReview());

        var next = run.Report(CutOff);

        Assert.Equal(new StartTurnStep(TurnKind.Review, BriefKind.Resume, SessionScope.Review), next);
        Assert.Equal(1, run.State.ReviewCutOffs);
    }

    [Fact]
    public void AReviewCutOffTwice_HandsOffWithTheReviewNotRun_AndSaysSo()
    {
        var run = new Run();
        run.AtReview();
        run.Report(CutOff);

        var next = run.Report(CutOff);

        Assert.IsType<HandoffStep>(next);
        Assert.Equal(ReviewStatus.NotRun, run.State.Review);
        Assert.Contains("The agent review could not complete", Assert.Single(run.State.Disclosures));
        Assert.Contains("verification (build, unit tests and changed-line coverage) passed", run.State.Disclosures[0]);

        var stop = Assert.IsType<StopStep>(run.Report(new HandoffCompleted(true)));
        Assert.Equal(StopReason.HandedOff, stop.Reason);
    }

    [Fact]
    public void AnImplementTurnCutOff_StillBlocks_WithTheReason()
    {
        var run = new Run();
        run.Started();

        var stop = Assert.IsType<StopStep>(run.Report(CutOff));

        Assert.Equal((LifecycleTrigger.Block, StopReason.TurnFaulted), (stop.Trigger, stop.Reason));
        Assert.StartsWith("The model reached the output limit", stop.Message);
        Assert.Equal(new StartTurnStep(TurnKind.Implement, BriefKind.Resume), run.State.ResumePoint);
    }

    [Fact]
    public void AScanCutOff_ImplementsWithoutTheScan_AndSaysSo()
    {
        var run = new Run(new RunLimits { DecisionScan = true });
        run.Started();

        var next = run.Report(CutOff);

        Assert.Equal(new StartTurnStep(TurnKind.Implement, BriefKind.Run), next);
        Assert.Contains("The decision scan did not finish", Assert.Single(run.State.Disclosures));
    }

    /// <summary>A change too small and safe for a review goes straight to handoff, and the
    /// handoff's evidence says it was not reviewed.</summary>
    [Fact]
    public void AReviewNotNeeded_HandsOff_WithTheReviewMarkedNotNeeded()
    {
        var run = new Run();
        run.AtReview();

        var next = run.Report(new ReviewNotNeeded());

        Assert.IsType<HandoffStep>(next);
        Assert.Equal(ReviewStatus.NotNeeded, run.State.Review);
        Assert.True(run.State.ReviewCompleted);
        Assert.Empty(run.State.Disclosures);
    }

    [Fact]
    public void AReviewNotNeeded_OutsideAReviewTurn_IsAHostBug()
    {
        var run = new Run();
        run.Started();

        Assert.Throws<InvalidOperationException>(() => run.Report(new ReviewNotNeeded()));
    }

    [Fact]
    public void TheCheckpointKeepsTheReviewCutOffCount()
    {
        var run = new Run();
        run.AtReview();
        run.Report(CutOff);

        Assert.Equal(1, RunStateJson.Deserialize(RunStateJson.Serialize(run.State)).ReviewCutOffs);
    }
}
