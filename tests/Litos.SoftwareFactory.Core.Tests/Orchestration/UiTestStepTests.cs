using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// The UI test step (README_Litos.Probe.Web.md W2.5, testability-architecture.md §6.4, §7):
/// Verify → browser smoke → Review → Handoff, with repairs in the shared cycles.
/// </summary>
public class UiTestStepTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class Run(bool uiTests = true, int maxRepairs = 2)
    {
        private readonly RunOrchestrator _orchestrator = new(new RunLimits { MaxRepairCycles = maxRepairs });

        public RunState State { get; private set; } = RunOrchestrator.NewRun(RunKind.Implement) with { UiTestEnabled = uiTests };

        public RunStep Report(StepOutcome outcome)
        {
            var transition = _orchestrator.Next(State, outcome, T0);
            State = transition.State;
            return transition.Step;
        }

        /// <summary>Implemented and verified: the step after a passing verification.</summary>
        public RunStep Verified()
        {
            Report(new RunStarted());
            Report(new PreflightCompleted(true));
            Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [], [], [], []), FilesChanged: true));
            return Report(new Verified(Passing()));
        }

        public RunStep UiTested(UiTestResult result) => Report(new UiTested(result));

        public RunStep Reviewed() => Report(new TurnEnded(TurnEndReason.Completed, new ReviewSubmission([])));
    }

    private static VerificationOutcome Passing() => new(
        BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.Met, [new TestCaseResult("T.Passes", TestOutcome.Passed)], new ChangedLineCoverage(9, 10, []), [], []);

    private static UiTestResult Clean() => new() { Evidence = new UiTestEvidence { Status = UiTestingStatus.Passed, ContractRevision = 3 } };

    private static UiTestResult Broken(params string[] ids) => new()
    {
        Defects = [.. ids.Select(id => new UiDefect(id, $"{id} answered HTTP 500.", ["web: fail: NullReferenceException"]))],
        Evidence = new UiTestEvidence { Status = UiTestingStatus.Failed },
    };

    [Fact]
    public void A_passing_verification_goes_to_the_browser_before_the_review()
    {
        var run = new Run();

        Assert.IsType<UiTestStep>(run.Verified());
        Assert.Equal(RunPhase.UiTest, run.State.Phase);
        Assert.Equal(Stage.Verify, run.State.Stage);

        var review = Assert.IsType<StartTurnStep>(run.UiTested(Clean()));
        Assert.Equal(TurnKind.Review, review.Kind);
        Assert.True(run.State.UiTestCurrent);
        Assert.Equal(3, run.State.UiTest!.ContractRevision);
        Assert.IsType<HandoffStep>(run.Reviewed());
    }

    [Fact]
    public void Without_an_adopted_contract_there_is_no_browser_step()
    {
        var run = new Run(uiTests: false);
        var review = Assert.IsType<StartTurnStep>(run.Verified());
        Assert.Equal(TurnKind.Review, review.Kind);
    }

    [Fact]
    public void An_application_defect_starts_a_repair_that_carries_the_failures_and_is_tested_again()
    {
        var run = new Run();
        run.Verified();

        var repair = Assert.IsType<StartTurnStep>(run.UiTested(Broken("visit:admin:/admin/users")));

        Assert.Equal((TurnKind.Repair, BriefKind.Repair), (repair.Kind, repair.Brief));
        Assert.Equal(1, run.State.RepairCyclesUsed);
        Assert.Equal("visit:admin:/admin/users", Assert.Single(run.State.UiDefects).Id);

        run.Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("fixed", [], [], [], []), FilesChanged: true));
        Assert.IsType<UiTestStep>(run.Report(new Verified(Passing())));
        Assert.IsType<StartTurnStep>(run.UiTested(Clean()));
        Assert.Empty(run.State.UiDefects);
    }

    [Fact]
    public void The_same_browser_failures_after_a_repair_block_the_run()
    {
        var run = new Run();
        run.Verified();
        run.UiTested(Broken("denied:patient:/admin"));
        run.Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("fixed", [], [], [], []), FilesChanged: true));
        run.Report(new Verified(Passing()));

        var stop = Assert.IsType<StopStep>(run.UiTested(Broken("denied:patient:/admin")));

        Assert.Equal((LifecycleTrigger.Block, StopReason.RepairMadeNoDifference), (stop.Trigger, stop.Reason));
        Assert.Contains("denied:patient:/admin", stop.Message);
        Assert.IsType<UiTestStep>(run.State.ResumePoint);
    }

    [Fact]
    public void Browser_failures_share_the_repair_cycles_with_verification()
    {
        var run = new Run(maxRepairs: 1);
        run.Verified();
        run.UiTested(Broken("a"));
        run.Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("fixed", [], [], [], []), FilesChanged: true));
        run.Report(new Verified(Passing()));

        var stop = Assert.IsType<StopStep>(run.UiTested(Broken("b")));

        Assert.Equal(StopReason.RepairCyclesExhausted, stop.Reason);
        Assert.Contains("in the browser", stop.Message);
    }

    [Fact]
    public void An_app_that_will_not_start_blocks_and_resumes_at_the_browser_step()
    {
        var run = new Run();
        run.Verified();

        var stop = Assert.IsType<StopStep>(run.UiTested(new UiTestResult { EnvironmentProblem = "Docker is not running." }));

        Assert.Equal((LifecycleTrigger.Block, StopReason.UiTestUnavailable), (stop.Trigger, stop.Reason));
        Assert.Contains("Docker is not running.", stop.Message);
        Assert.IsType<UiTestStep>(run.State.ResumePoint);
        Assert.Equal(Stage.Verify, run.State.Stage);
        Assert.Equal(0, run.State.RepairCyclesUsed);
    }

    [Fact]
    public void Notes_for_a_person_are_disclosed_once_and_do_not_stop_the_run()
    {
        var run = new Run();
        run.Verified();
        var notes = new UiTestResult { Notes = ["Browser check visit:staff:/x needs a person, not a code change: answered 404."], Evidence = new UiTestEvidence { Status = UiTestingStatus.Failed } };

        Assert.IsType<StartTurnStep>(run.UiTested(notes));
        Assert.Single(run.State.Disclosures);
    }

    [Fact]
    public void A_review_repair_is_verified_and_tested_in_the_browser_again()
    {
        var run = new Run();
        run.Verified();
        run.UiTested(Clean());
        run.Report(new TurnEnded(TurnEndReason.Completed, new ReviewSubmission([new ReviewFinding(FindingSeverity.Blocking, "a.cs", 1, "bug")])));
        run.Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("fixed", [], [], [], []), FilesChanged: true));

        Assert.IsType<UiTestStep>(run.Report(new Verified(Passing())));
        Assert.IsType<HandoffStep>(run.UiTested(Clean()));
    }

    [Fact]
    public void A_stop_during_the_browser_step_redoes_it_on_resume()
    {
        var run = new Run();
        run.Verified();

        var stop = Assert.IsType<StopStep>(run.Report(new StepStopped(Cancel: false)));

        Assert.Equal(StopReason.PausedByUser, stop.Reason);
        Assert.IsType<UiTestStep>(run.State.ResumePoint);
    }

    [Fact]
    public void The_checkpoint_keeps_the_browser_state()
    {
        var run = new Run();
        run.Verified();
        run.UiTested(Broken("visit:admin:/"));

        var back = RunStateJson.Deserialize(RunStateJson.Serialize(run.State));

        Assert.True(back.UiTestEnabled);
        Assert.Equal(["visit:admin:/"], back.UiFailuresBeforeRepair);
        Assert.Equal("web: fail: NullReferenceException", back.UiDefects.Single().Log.Single());
        Assert.Equal(UiTestingStatus.Failed, back.UiTest!.Status);
        Assert.IsType<UiTestStep>(RunStateJson.Deserialize(RunStateJson.Serialize(run.State with { ResumePoint = new UiTestStep() })).ResumePoint);
    }

    [Fact]
    public void The_handoff_carries_the_browser_evidence_or_says_it_is_not_set_up()
    {
        var facts = new HandoffFacts("factory/x", "abc", null, null);
        var tested = new Run();
        tested.Verified();
        tested.UiTested(Clean());

        Assert.Equal(UiTestingStatus.Passed, HandoffComposer.Evidence(tested.State, facts).UiTesting!.Status);
        Assert.Equal(UiTestingStatus.NotSetUp, HandoffComposer.Evidence(new Run(uiTests: false).State, facts).UiTesting!.Status);
    }

    [Theory]
    [InlineData(true, "The factory still used the adopted contract (profile revision 3)")]
    [InlineData(false, "adopt it on the project's Testability panel")]
    public void A_branch_that_changes_the_contract_file_says_it_was_not_obeyed(bool adopted, string note)
    {
        var run = new Run(uiTests: adopted);
        run.Verified();
        if (adopted)
            run.UiTested(Clean());

        Assert.Contains(note, HandoffComposer.ContractDrift(run.State, ["src/a.cs", "litos.test.yaml"]));
        Assert.Null(HandoffComposer.ContractDrift(run.State, ["src/a.cs", "docs/litos.test.yaml.md"]));
    }

    [Fact]
    public void The_repair_brief_lists_the_browser_failures_and_says_to_change_the_application()
    {
        var run = new Run();
        run.Verified();
        run.UiTested(Broken("visit:patient:/my/appointments"));

        var brief = BriefComposer.Compose(new StartTurnStep(TurnKind.Repair, BriefKind.Repair), new RunContext("SalesApp", "factory/csv", "main", "Orders need a CSV export."), run.State, new RunLimits());

        Assert.Contains("Failing in the browser (1)", brief);
        Assert.Contains("visit:patient:/my/appointments", brief);
        Assert.Contains("Change the application, not the test", brief);
        Assert.Contains("NullReferenceException", brief);
    }
}
