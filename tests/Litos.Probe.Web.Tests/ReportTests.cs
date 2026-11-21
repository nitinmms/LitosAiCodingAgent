using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Reports;

namespace Litos.Probe.Web.Tests;

public class ReportTests
{
    private static CheckResult Visit(string id, bool failed, Classification c = Classification.ApplicationDefect) =>
        failed ? CheckResult.Fail(id, CheckKind.RouteVisit, c, "boom") : CheckResult.Pass(id, CheckKind.RouteVisit);

    [Fact]
    public void Outcome_passes_with_only_passing_and_skipped_checks()
    {
        Assert.Equal(ProbeWebOutcome.Passed, ReportOutcome.Compute([Visit("a", false), CheckResult.Skip("b", CheckKind.SignIn, "no login")]));
    }

    [Fact]
    public void Outcome_is_failed_on_a_defect_and_environment_failure_outranks_it()
    {
        Assert.Equal(ProbeWebOutcome.Failed, ReportOutcome.Compute([Visit("a", true), Visit("b", true, Classification.TestDefect)]));
        Assert.Equal(ProbeWebOutcome.EnvironmentFailure, ReportOutcome.Compute([Visit("a", true), CheckResult.Fail("start", CheckKind.Start, Classification.EnvironmentFailure, "x")]));
    }

    [Fact]
    public void Outcome_counts_flaky_as_passed_and_nothing_run_as_inconclusive()
    {
        Assert.Equal(ProbeWebOutcome.Passed, ReportOutcome.Compute([Visit("a", false) with { Classification = Classification.Flaky }]));
        Assert.Equal(ProbeWebOutcome.Inconclusive, ReportOutcome.Compute([]));
        Assert.Equal(ProbeWebOutcome.Inconclusive, ReportOutcome.Compute([CheckResult.Skip("a", CheckKind.SignIn, "x")]));
    }

    [Fact]
    public void A_failure_that_also_fails_on_main_is_pre_existing_and_not_blamed()
    {
        var baseline = new ProbeWebReport { Checks = [Visit("visit:admin:/a", true), Visit("visit:admin:/b", false)] };
        var now = new ProbeWebReport { Outcome = ProbeWebOutcome.Failed, Checks = [Visit("visit:admin:/a", true), Visit("visit:admin:/b", false)] };

        var compared = SmokeComparison.Compare(now, baseline);

        Assert.Equal(ProbeWebOutcome.Passed, compared.Report.Outcome);
        Assert.Empty(compared.NewFailures);
        Assert.Equal("visit:admin:/a", Assert.Single(compared.PreExisting).Id);
        Assert.True(compared.Report.Checks[0].PreExisting);
    }

    [Fact]
    public void A_new_failure_is_blamed_on_the_change()
    {
        var baseline = new ProbeWebReport { Checks = [Visit("visit:admin:/b", false)] };
        var now = new ProbeWebReport { Checks = [Visit("visit:admin:/b", true)] };

        var compared = SmokeComparison.Compare(now, baseline);

        Assert.Equal(ProbeWebOutcome.Failed, compared.Report.Outcome);
        Assert.Equal("visit:admin:/b", Assert.Single(compared.NewFailures).Id);
        Assert.Empty(compared.PreExisting);
    }

    [Fact]
    public void Scenarios_are_never_baseline_compared()
    {
        var scenario = CheckResult.Fail("scenario:book-appointment", CheckKind.Scenario, Classification.ApplicationDefect, "button missing");
        var compared = SmokeComparison.Compare(new ProbeWebReport { Checks = [scenario] }, new ProbeWebReport { Checks = [scenario] });

        Assert.Single(compared.NewFailures);
        Assert.Empty(compared.PreExisting);
    }

    [Fact]
    public void Without_a_baseline_every_failure_is_new()
    {
        var compared = SmokeComparison.Compare(new ProbeWebReport { Checks = [Visit("a", true)] }, null);
        Assert.Single(compared.NewFailures);
    }

    [Fact]
    public void An_invalid_contract_keeps_its_outcome()
    {
        var compared = SmokeComparison.Compare(new ProbeWebReport { Outcome = ProbeWebOutcome.InvalidContract }, null);
        Assert.Equal(ProbeWebOutcome.InvalidContract, compared.Report.Outcome);
    }

    [Theory]
    [InlineData(FailureCause.BuildFailed, Classification.EnvironmentFailure)]
    [InlineData(FailureCause.ProcessExited, Classification.EnvironmentFailure)]
    [InlineData(FailureCause.ResetFailed, Classification.EnvironmentFailure)]
    [InlineData(FailureCause.Timeout, Classification.EnvironmentFailure)]
    [InlineData(FailureCause.SecretMissing, Classification.AuthenticationFailure)]
    [InlineData(FailureCause.SignInNotReached, Classification.AuthenticationFailure)]
    [InlineData(FailureCause.RouteNotFound, Classification.TestDefect)]
    [InlineData(FailureCause.LocatorNotFound, Classification.TestDefect)]
    [InlineData(FailureCause.LocatorAmbiguous, Classification.TestDefect)]
    [InlineData(FailureCause.ScenarioInvalid, Classification.TestDefect)]
    [InlineData(FailureCause.AssertionFailed, Classification.ApplicationDefect)]
    [InlineData(FailureCause.WrongPort, Classification.ApplicationDefect)]
    [InlineData(FailureCause.HttpsRedirect, Classification.ApplicationDefect)]
    [InlineData(FailureCause.LoginFieldUnlabelled, Classification.ApplicationDefect)]
    [InlineData(FailureCause.PageSignal, Classification.ApplicationDefect)]
    [InlineData(FailureCause.DeniedRouteOpened, Classification.ApplicationDefect)]
    [InlineData(FailureCause.TestEndpointInProduction, Classification.ApplicationDefect)]
    [InlineData(FailureCause.UnnamedElement, Classification.ApplicationDefect)]
    public void The_classifier_maps_each_cause(FailureCause cause, Classification expected) =>
        Assert.Equal(expected, Classifier.Classify(cause));

    [Fact]
    public void The_report_json_has_the_readme_shape_and_round_trips()
    {
        var report = new ProbeWebReport
        {
            Kind = ReportKind.DryRun,
            Outcome = ProbeWebOutcome.Failed,
            Commit = "4e1b9c2",
            Contract = new ContractInfo { Source = "profile", Revision = 4, Stack = "aspnet-blazor-server" },
            App = new AppInfo { BaseUrl = "http://localhost:51873", StartSeconds = 3.8, Processes = [new ProcessInfo("web", 51873)] },
            Checks = [Visit("visit:patient:/my/appointments", true) with { Role = "patient", Evidence = new Evidence { Screenshots = ["a1f0"], ConsoleErrors = 1 } }],
            Lint = new LintSummary { Pages = 5, Interactive = 41 },
        };

        var json = report.ToJson();

        Assert.Contains("\"kind\": \"dry-run\"", json);
        Assert.Contains("\"outcome\": \"Failed\"", json);
        Assert.Contains("\"kind\": \"RouteVisit\"", json);
        Assert.Contains("\"classification\": \"ApplicationDefect\"", json);
        Assert.Contains("\"modelCalls\": 0", json);
        Assert.Equal(json, ProbeWebReport.FromJson(json).ToJson());
    }

    [Fact]
    public void The_redactor_masks_every_secret_longest_first()
    {
        var redactor = new SecretRedactor();
        redactor.Add("hunter2");
        redactor.Add("hunter2-and-more");
        redactor.Add("ab");
        redactor.Add(null);

        Assert.Equal($"pw={SecretRedactor.Mask} other={SecretRedactor.Mask} ab", redactor.Redact("pw=hunter2-and-more other=hunter2 ab"));
    }
}
