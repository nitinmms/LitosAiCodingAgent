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
    public void Unset_secrets_block_for_a_person_and_resume_at_the_gate_never_a_repair()
    {
        var run = new Run();
        run.Verified();

        var stop = Assert.IsType<StopStep>(run.UiTested(new UiTestResult { MissingSecrets = ["TEST_ADMIN_PASS", "TEST_STAFF_PASS"] }));

        Assert.Equal((LifecycleTrigger.Block, StopReason.SecretsNotSet), (stop.Trigger, stop.Reason));
        Assert.Equal(
            "The Testability Gate needs TEST_ADMIN_PASS, TEST_STAFF_PASS, which are not set. Set them on the project's Testability panel, then resume: the factory runs the Gate again.",
            stop.Message);
        Assert.IsType<UiTestStep>(run.State.ResumePoint);
        Assert.Equal(0, run.State.RepairCyclesUsed);
        Assert.False(run.State.UiTestCurrent);
    }

    [Fact]
    public void One_unset_secret_reads_in_the_singular()
    {
        var run = new Run();
        run.Verified();

        var stop = Assert.IsType<StopStep>(run.UiTested(new UiTestResult { MissingSecrets = ["TEST_ADMIN_PASS"] }));

        Assert.Contains("needs TEST_ADMIN_PASS, which is not set. Set it on", stop.Message);
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
    [InlineData(true, true, "The factory still used the adopted contract (profile revision 3)")]
    [InlineData(false, false, "adopt it on the project's Testability panel")]
    // Finding 17: a Make-testable task runs the browser step on its own draft, so the change is its job.
    [InlineData(true, false, "adopt it on the project's Testability panel")]
    public void A_branch_that_changes_the_contract_file_says_it_was_not_obeyed_only_under_an_adopted_one(bool uiTests, bool adopted, string note)
    {
        var run = new Run(uiTests: uiTests);
        run.Verified();
        if (uiTests)
            run.UiTested(Clean());

        var drift = HandoffComposer.ContractDrift(run.State, ["src/a.cs", "litos.test.yaml"], adopted);
        Assert.Contains(note, drift);
        if (!adopted)
            Assert.DoesNotContain("still used", drift);
        Assert.Null(HandoffComposer.ContractDrift(run.State, ["src/a.cs", "docs/litos.test.yaml.md"], adopted));
    }

    [Fact]
    public void The_browser_evidence_names_the_handoff_commit_while_it_is_current()
    {
        // Finding 8: Probe.Web reads HEAD, which is the commit before the uncommitted work it tested.
        var run = new Run();
        run.Verified();
        run.UiTested(Clean() with { Evidence = Clean().Evidence with { Commit = "main000" } });

        var evidence = HandoffComposer.Evidence(run.State, new HandoffFacts("factory/x", "handoff1", null, null));

        Assert.Equal("handoff1", evidence.UiTesting!.Commit);
    }

    [Fact]
    public void Stale_browser_evidence_keeps_the_commit_it_was_taken_at()
    {
        var run = new Run();
        run.Verified();
        run.UiTested(Clean() with { Evidence = Clean().Evidence with { Commit = "main000" } });
        var stale = run.State with { UiTestCurrent = false };

        Assert.Equal("main000", HandoffComposer.Evidence(stale, new HandoffFacts("factory/x", "handoff1", null, null)).UiTesting!.Commit);
    }

    [Fact]
    public void Generated_output_on_the_branch_is_repaired_like_a_failure_and_blocks_if_it_stays()
    {
        // Finding 6: the review called 53k lines of coverage output minor.
        var run = new Run();
        run.Report(new RunStarted());
        run.Report(new PreflightCompleted(true));
        run.Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [], [], [], []), FilesChanged: true));
        string[] junk = ["Tests/TestResults/a.trx", "coverage/lcov.info"];

        var repair = Assert.IsType<StartTurnStep>(run.Report(new Verified(Passing()) { GeneratedFiles = junk }));
        Assert.Equal(TurnKind.Repair, repair.Kind);
        var brief = BriefComposer.Compose(repair, new RunContext("SalesApp", "factory/x", "main", "Orders need a CSV export."), run.State, new RunLimits());
        Assert.Contains("## Generated output on the branch (2)", brief);
        Assert.Contains("- `Tests/TestResults/a.trx`", brief);
        Assert.Contains("add their folder or pattern to `.gitignore`", brief);

        run.Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("removed", [], [], [], []), FilesChanged: true));
        var stop = Assert.IsType<StopStep>(run.Report(new Verified(Passing()) { GeneratedFiles = junk }));
        Assert.Equal(StopReason.RepairMadeNoDifference, stop.Reason);
        Assert.Contains("2 generated files on the branch", stop.Message);
    }

    [Fact]
    public void Removing_the_generated_output_goes_on_to_the_browser_step()
    {
        var run = new Run();
        run.Report(new RunStarted());
        run.Report(new PreflightCompleted(true));
        run.Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [], [], [], []), FilesChanged: true));
        run.Report(new Verified(Passing()) { GeneratedFiles = ["bin/Debug/app.dll"] });
        run.Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("removed", [], [], [], []), FilesChanged: true));

        Assert.IsType<UiTestStep>(run.Report(new Verified(Passing())));
        Assert.Empty(run.State.GeneratedFiles);
    }

    private static string ReviewBrief(bool uiTests, ReviewDepth depth = ReviewDepth.Full)
    {
        var run = new Run(uiTests);
        run.Verified();
        if (uiTests)
            run.UiTested(Clean());
        var context = new RunContext("SalesApp", "factory/x", "main", "Orders need a CSV export.") { ReviewDepth = depth };
        return BriefComposer.Compose(new StartTurnStep(TurnKind.Review, BriefKind.Review, SessionScope.Review), context, run.State, new RunLimits());
    }

    [Theory]
    [InlineData(ReviewDepth.Full)]
    [InlineData(ReviewDepth.Light)]
    public void A_browser_tested_review_knows_the_gate_rules_and_that_hiding_a_signal_is_blocking(ReviewDepth depth)
    {
        // Finding 9: the review called the hidden marker a risk, and missed console.error made console.warn.
        var brief = ReviewBrief(uiTests: true, depth);

        Assert.Contains("## The browser checks", brief);
        Assert.Contains("once the readiness marker is *attached*", brief);
        Assert.Contains("A change that silences one of these signals instead of fixing its cause is `blocking`", brief);
        Assert.Contains("Committed generated output (build, test or coverage results, logs) is always `blocking`.", brief);
        Assert.DoesNotContain("{{", brief);
    }

    [Fact]
    public void A_review_without_browser_checks_has_no_gate_section()
    {
        var brief = ReviewBrief(uiTests: false);

        Assert.DoesNotContain("## The browser checks", brief);
        Assert.Contains("Committed generated output", brief);
        Assert.DoesNotContain("{{", brief);
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

    [Fact]
    public void On_a_Make_testable_task_the_repair_may_fix_the_contract_but_not_by_dropping_checks()
    {
        var run = new Run();
        run.Verified();
        run.UiTested(Broken("visit:anonymous:/gone"));
        var context = new RunContext("SalesApp", "factory/make-testable", "main", "@factory Make this app testable.") { TaskType = TaskTypes.Testability };

        var brief = BriefComposer.Compose(new StartTurnStep(TurnKind.Repair, BriefKind.Repair), context, run.State, new RunLimits());

        Assert.Contains("Testability Gate on your draft contract. Fix whichever is wrong, the app or the contract", brief);
        Assert.Contains("Do not remove a route a person would open", brief);
        Assert.DoesNotContain("not the test or the contract", brief);
    }
}

/// <summary>The Make-testable task's brief (testability-architecture.md §6.3).</summary>
public class MakeTestableBriefTests
{
    private static string Brief(string type, IReadOnlyList<string>? gaps = null, string? stack = null) => BriefComposer.Compose(
        new StartTurnStep(TurnKind.Implement, BriefKind.Run),
        new RunContext("SalesApp", "factory/make-testable", "main", "@factory Make this app testable.")
        {
            TaskType = type,
            TestabilityGaps = gaps ?? [],
            DetectedStack = stack,
        },
        RunOrchestrator.NewRun(RunKind.Implement),
        new RunLimits());

    [Fact]
    public void A_Make_testable_task_gets_its_own_brief_with_the_gaps_and_the_stack()
    {
        var brief = Brief(TaskTypes.Testability, ["No reset declared: add one.", "There is no field labelled 'Email' on /login."], "node-express-spa");

        Assert.StartsWith("# Factory run brief: make the app testable", brief);
        Assert.Contains("Detected stack: `node-express-spa`.", brief);
        Assert.Contains("- No reset declared: add one.", brief);
        Assert.Contains("Call `check_testability`", brief);
        Assert.Contains("{secret:NAME}", brief);
        Assert.DoesNotContain("{{", brief);
    }

    [Fact]
    public void It_says_what_to_do_without_tests_to_seed_domain_data_and_that_only_a_passing_gate_hands_off()
    {
        var brief = Brief(TaskTypes.Testability, stack: "aspnet-blazor-server");

        Assert.Contains("If the repository has no unit tests yet, create one test project in the layout your stack's skill names", brief);
        Assert.Contains("Do not try layouts out in scratch folders", brief);
        Assert.Contains("The reset leaves the app with representative data, not just the test users", brief);
        Assert.Contains("The factory hands off only once its own Gate passes", brief);
        // Seed data is inferred and disclosed, not asked about (W3.2, decided 2026-11-20).
        Assert.DoesNotContain("which data to seed", brief);
    }

    [Fact]
    public void Without_a_check_it_says_so_and_points_at_generic_http()
    {
        var brief = Brief(TaskTypes.Testability);
        Assert.Contains("No stack was detected", brief);
        Assert.Contains("The check has not run yet", brief);
    }

    [Theory]
    [InlineData("feature")]
    [InlineData("bug")]
    public void Every_other_task_keeps_the_run_brief(string type)
    {
        var brief = Brief(type);
        Assert.StartsWith("# Factory run brief\n", brief.Replace("\r\n", "\n"));
        Assert.Contains("Do not start the application", brief);
    }
}
