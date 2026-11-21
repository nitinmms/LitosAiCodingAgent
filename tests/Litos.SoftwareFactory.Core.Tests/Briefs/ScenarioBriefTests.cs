using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Briefs;

/// <summary>README_Litos.Probe.Web.md W3.7: what the spec and work briefs say about browser scenarios.</summary>
public class ScenarioBriefTests
{
    private static readonly ScenarioGuide Clinic = new(["admin", "staff", "patient"], ["/my/appointments", "/appointments"]);

    private static string Spec(ScenarioGuide? guide) =>
        BriefComposer.Spec(new SpecContext("Clinic", "main", "Cancel", "Patients can cancel.", null, []) { Scenarios = guide });

    private static string Run(ScenarioGuide? guide, BriefKind kind = BriefKind.Run, TurnKind turn = TurnKind.Implement) => BriefComposer.Compose(
        new StartTurnStep(turn, kind),
        new RunContext("Clinic", "factory/x", "main", "Patients can cancel.") { Scenarios = guide, TesterFeedback = "Say Cancelled." },
        RunOrchestrator.NewRun(kind == BriefKind.Rework ? RunKind.Rework : RunKind.Implement),
        new RunLimits());

    [Fact]
    public void A_spec_with_a_contract_asks_for_one_scenario_per_checkable_criterion_in_the_format()
    {
        var brief = Spec(Clinic);

        Assert.Contains("## Browser scenarios", brief);
        Assert.Contains("For each acceptance criterion a person could check by using the app, add one scenario to `scenarios`", brief);
        Assert.Contains("`role`: one of `admin`, `staff`, `patient`; leave it out for a signed-out visitor.", brief);
        Assert.Contains("such as `/my/appointments`", brief);
        Assert.Contains("Take labels, names and test ids from the code, never guess them", brief);
        Assert.Contains("`openQuestions` (a list of strings) and `scenarios` (a list of scenario objects).", brief);
        Assert.DoesNotContain("{{", brief);
    }

    [Fact]
    public void Without_a_contract_neither_brief_mentions_scenarios()
    {
        Assert.DoesNotContain("scenario", Spec(null), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Browser scenarios", Run(null));
        Assert.DoesNotContain("{{", Spec(null) + Run(null));
    }

    [Fact]
    public void A_task_without_a_spec_drafts_them_in_submit_work_for_evidence_only()
    {
        var brief = Run(Clinic);

        Assert.Contains("In `submit_work`, add a scenario to `scenarios`", brief);
        Assert.Contains("They are not approved, so one that fails is reported, not sent back to you.", brief);
        Assert.Contains("fix the app, not the scenario", brief);
    }

    [Fact]
    public void A_task_building_an_approved_spec_is_told_what_its_scenarios_expect()
    {
        var brief = Run(Clinic with { Approved = ["cancel-upcoming: as patient, open /my/appointments; click button \"Cancel\"; expect [data-testid=status] to show \"Cancelled\""] });

        Assert.Contains("The approved specification comes with browser scenarios", brief);
        Assert.Contains("one that fails because of your change comes back to you", brief);
        Assert.Contains("- cancel-upcoming: as patient, open /my/appointments; click button \"Cancel\"", brief);
        Assert.DoesNotContain("In `submit_work`, add a scenario", brief);
    }

    [Fact]
    public void A_rework_gets_the_section_too() =>
        Assert.Contains("## Browser scenarios", Run(Clinic, BriefKind.Rework, TurnKind.Rework));

    [Fact]
    public void An_app_without_sign_in_says_to_leave_role_out() =>
        Assert.Contains("The app has no sign-in: leave `role` out.", Spec(new ScenarioGuide([], ["/"])));

    [Fact]
    public void A_repair_that_names_no_scenarios_keeps_the_ones_drafted_before()
    {
        var orchestrator = new RunOrchestrator(new RunLimits());
        var state = RunOrchestrator.NewRun(RunKind.Implement);
        var t0 = DateTimeOffset.UnixEpoch;
        state = orchestrator.Next(state, new RunStarted(), t0).State;
        state = orchestrator.Next(state, new PreflightCompleted(true), t0).State;
        string[] drafted = ["""{"id":"a","steps":[]}"""];
        state = orchestrator.Next(state, new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [], [], [], [], Scenarios: drafted), FilesChanged: true), t0).State;
        state = orchestrator.Next(state, new Verified(new VerificationOutcome(BuildStatus.Failed, UnitTestStatus.NotRun, CoverageStatus.NotMeasured, [], null, [], [])), t0).State;

        state = orchestrator.Next(state, new TurnEnded(TurnEndReason.Completed, new WorkSubmission("fixed", [], [], [], []), FilesChanged: true), t0).State;

        Assert.Equal(drafted, state.LastSubmission!.Scenarios);
        Assert.Equal("fixed", state.LastSubmission.Summary);
    }
}
