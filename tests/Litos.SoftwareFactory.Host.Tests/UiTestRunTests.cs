using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Reports;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Testability;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// W2.5 of README_Litos.Probe.Web.md: a task on a project with an adopted contract is checked in a
/// browser after verification and before review, and the handoff carries the evidence.
/// </summary>
public sealed class UiTestRunTests : IAsyncLifetime
{
    private readonly FakeTestabilityEngine _engine = new();
    private TestHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await TestHost.StartAsync(services: s => s.AddSingleton<ITestabilityEngine>(_engine));

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static readonly WebAppContract Contract = new()
    {
        Application = new AppDefinition
        {
            Stack = "node-express-spa",
            Processes = [new ProcessDefinition { Name = "web", Command = ["npm", "start"] }],
            BaseUrl = "web",
            Reset = new ResetDefinition { Strategy = ResetStrategy.None },
        },
        Testability = new TestabilityDefinition { Routes = new RouteSet { Public = ["/"] } },
    };

    /// <summary>A registered project whose profile carries the contract, as an adoption leaves it.</summary>
    private async Task<Guid> TestableProjectAsync()
    {
        var projectId = await _host.RegisterProjectAsync();
        var profile = VerificationProfile.Parse((await _host.Store.GetProjectAsync(projectId, default))!.VerificationProfileJson).WithWebApp(Contract);
        using var response = await _host.Client.PatchAsJsonAsync($"api/projects/{projectId}", new { profileJson = profile.ToJson() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return projectId;
    }

    private async Task<Guid> DelegatedAsync(Guid projectId)
    {
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        return threadId;
    }

    private static UiTestEvidence UiTestingOf(ThreadDetails details) =>
        JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff!.EvidenceJson, FactoryWire.Json)!.UiTesting!;

    private static ProbeWebReport Failing(string id, Classification classification, string message) => FakeTestabilityEngine.PassingSmoke() with
    {
        Outcome = classification == Classification.EnvironmentFailure ? ProbeWebOutcome.EnvironmentFailure : ProbeWebOutcome.Failed,
        Checks =
        [
            CheckResult.Pass("build", CheckKind.Build),
            CheckResult.Fail(id, CheckKind.RouteVisit, classification, message) with { Evidence = new Evidence { Log = ["web: fail: NullReferenceException"], Screenshots = ["shot-1"] } },
        ],
    };

    [Fact]
    public async Task A_testable_project_is_checked_in_the_browser_before_review_and_the_handoff_shows_it()
    {
        var projectId = await TestableProjectAsync();
        var threadId = await DelegatedAsync(projectId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review], _host.Workers.Turns.Select(t => t.Kind));
        var smoke = Assert.Single(_engine.Smokes);
        Assert.Equal(_host.Workspaces.Of(projectId).Path, smoke.WorkingCopy);
        Assert.Equal(("profile", 2, "factory"), (smoke.Options.ContractSource, smoke.Options.ContractRevision, smoke.Options.ServiceOwner));

        var ui = UiTestingOf(details);
        Assert.Equal(UiTestingStatus.Passed, ui.Status);
        Assert.Equal(2, ui.ContractRevision);
        Assert.Equal(["shot-admin"], ui.Checks.Single(c => c.Id == "visit:admin:/admin").Screenshots);
        Assert.Equal(["npm run build", "npm start"], ui.RunCommands);
        Assert.Contains(details.Messages, m => m.Text.StartsWith("Browser checks: 3 passed, 0 failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_an_adopted_contract_no_browser_check_runs_and_the_handoff_says_it_is_not_set_up()
    {
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await DelegatedAsync(projectId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Empty(_engine.Smokes);
        Assert.Equal(UiTestingStatus.NotSetUp, UiTestingOf(details).Status);
    }

    [Fact]
    public async Task An_application_defect_is_repaired_with_the_failure_in_the_brief_and_checked_again()
    {
        var projectId = await TestableProjectAsync();
        _engine.SmokeReports.Enqueue(Failing("visit:admin:/admin", Classification.ApplicationDefect, "/admin answered HTTP 500."));
        var threadId = await DelegatedAsync(projectId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Repair, TurnKind.Review], _host.Workers.Turns.Select(t => t.Kind));
        var repair = _host.Workers.Turns.Single(t => t.Kind == TurnKind.Repair).Brief;
        Assert.Contains("Failing in the browser (1)", repair);
        Assert.Contains("/admin answered HTTP 500.", repair);
        Assert.Contains("NullReferenceException", repair);
        Assert.Equal(2, _engine.Smokes.Count);
        Assert.Equal(UiTestingStatus.Passed, UiTestingOf(details).Status);
    }

    [Fact]
    public async Task An_app_that_will_not_start_blocks_the_task_with_the_reason()
    {
        var projectId = await TestableProjectAsync();
        _engine.SmokeReports.Enqueue(Failing("services", Classification.EnvironmentFailure, "Docker is not running, and the contract's services need it."));
        var threadId = await DelegatedAsync(projectId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal(StopReason.UiTestUnavailable, details.LatestRun!.StopReason);
        Assert.Contains(details.Messages, m => m.Text.Contains("The browser checks could not run: Docker is not running"));
        Assert.Equal([TurnKind.Implement], _host.Workers.Turns.Select(t => t.Kind));
    }

    [Fact]
    public async Task A_failure_that_also_fails_on_main_is_disclosed_not_repaired()
    {
        var projectId = await TestableProjectAsync();
        var project = (await _host.Store.GetProjectAsync(projectId, default))!;
        var baseline = Failing("visit:admin:/admin", Classification.ApplicationDefect, "/admin answered HTTP 500.") with { Kind = ReportKind.Gate };
        await _host.Store.SaveTestabilityCheckAsync(new TestabilityCheck
        {
            ProjectId = projectId,
            ProfileRevision = project.ProfileRevision,
            Commit = "base000",
            Status = TestabilityStatus.BrokenOnMain,
            ReportJson = new TestabilityReport { Status = TestabilityStatus.BrokenOnMain, Run = baseline }.ToJson(),
            CreatedAt = DateTimeOffset.UtcNow,
        }, default);
        _engine.SmokeReports.Enqueue(Failing("visit:admin:/admin", Classification.ApplicationDefect, "/admin answered HTTP 500."));
        var threadId = await DelegatedAsync(projectId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review], _host.Workers.Turns.Select(t => t.Kind));
        var ui = UiTestingOf(details);
        Assert.Equal(UiTestingStatus.Passed, ui.Status);
        Assert.Equal("visit:admin:/admin", Assert.Single(ui.PreExisting).Id);
        var evidence = JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff!.EvidenceJson, FactoryWire.Json)!;
        Assert.Contains(evidence.KnownLimitations, l => l.Contains("already fails on the default branch"));
    }

    // ---- Interpreting a smoke ----

    private static UiTestResult Interpret(ProbeWebReport report, ProbeWebReport? baseline = null, bool healthy = false) =>
        RunUiTests.Interpret(report, baseline, healthy, 3, ["npm start"]);

    [Fact]
    public void A_test_defect_or_a_wrong_password_needs_a_person_not_a_repair()
    {
        var report = FakeTestabilityEngine.PassingSmoke() with
        {
            Outcome = ProbeWebOutcome.Failed,
            Checks =
            [
                CheckResult.Fail("visit:anonymous:/gone", CheckKind.RouteVisit, Classification.TestDefect, "/gone answered 404."),
                CheckResult.Fail("signin:admin", CheckKind.SignIn, Classification.AuthenticationFailure, "Signed in as admin, but it was not reached."),
            ],
        };

        var result = Interpret(report);

        Assert.Empty(result.Defects);
        Assert.Null(result.EnvironmentProblem);
        Assert.Equal(2, result.Notes.Count);
        Assert.All(result.Notes, n => Assert.Contains("needs a person", n));
        Assert.Equal(UiTestingStatus.Failed, result.Evidence.Status);
    }

    [Fact]
    public void On_a_Make_testable_draft_the_same_failures_are_the_tasks_to_repair()
    {
        // Finding 4: the contract and the test users are the retrofit's own work.
        var report = FakeTestabilityEngine.PassingSmoke() with
        {
            Kind = ReportKind.Gate,
            Outcome = ProbeWebOutcome.Failed,
            Checks =
            [
                CheckResult.Fail("visit:anonymous:/gone", CheckKind.RouteVisit, Classification.TestDefect, "/gone answered 404."),
                CheckResult.Fail("signin:admin", CheckKind.SignIn, Classification.AuthenticationFailure, "Signed in as admin, but it was not reached."),
            ],
        };

        var result = RunUiTests.Interpret(report, baseline: null, baselineHealthy: true, 1, [], draft: true);

        Assert.Equal(["visit:anonymous:/gone", "signin:admin"], result.Defects.Select(d => d.Id));
        Assert.DoesNotContain(result.Notes, n => n.Contains("needs a person"));
        Assert.Equal(UiTestingStatus.Failed, result.Evidence.Status);
    }

    [Fact]
    public void On_a_draft_a_missing_browser_still_blocks()
    {
        var report = FakeTestabilityEngine.PassingSmoke() with
        {
            Kind = ReportKind.Gate,
            Outcome = ProbeWebOutcome.EnvironmentFailure,
            Checks = [CheckResult.Pass("start", CheckKind.Start), CheckResult.Fail("browser", CheckKind.Start, Classification.EnvironmentFailure, "Chromium for Playwright is not installed.")],
        };

        var result = RunUiTests.Interpret(report, baseline: null, baselineHealthy: true, 1, [], draft: true);

        Assert.Empty(result.Defects);
        Assert.Contains("Chromium", result.EnvironmentProblem);
    }

    [Fact]
    public void A_start_failure_is_the_tasks_when_main_starts_fine_and_the_environments_otherwise()
    {
        var report = FakeTestabilityEngine.PassingSmoke() with
        {
            Outcome = ProbeWebOutcome.EnvironmentFailure,
            Checks = [CheckResult.Pass("build", CheckKind.Build), CheckResult.Fail("start", CheckKind.Start, Classification.EnvironmentFailure, "web exited with code 1 before it was ready.")],
        };
        var healthyMain = FakeTestabilityEngine.PassingSmoke() with { Kind = ReportKind.Gate };

        var broke = Interpret(report, healthyMain, healthy: true);
        var blocked = Interpret(report);

        Assert.Equal("start", Assert.Single(broke.Defects).Id);
        Assert.Null(broke.EnvironmentProblem);
        Assert.Empty(blocked.Defects);
        Assert.Contains("exited with code 1", blocked.EnvironmentProblem);
        Assert.Equal(UiTestingStatus.Inconclusive, blocked.Evidence.Status);
    }

    [Fact]
    public void A_missing_browser_is_never_the_tasks()
    {
        var report = FakeTestabilityEngine.PassingSmoke() with
        {
            Outcome = ProbeWebOutcome.EnvironmentFailure,
            Checks = [CheckResult.Pass("start", CheckKind.Start), CheckResult.Fail("browser", CheckKind.Start, Classification.EnvironmentFailure, "Chromium for Playwright is not installed.")],
        };

        var result = Interpret(report, FakeTestabilityEngine.PassingSmoke() with { Kind = ReportKind.Gate }, healthy: true);

        Assert.Empty(result.Defects);
        Assert.Contains("Chromium", result.EnvironmentProblem);
    }

    [Fact]
    public void A_flaky_check_is_noted_and_passes()
    {
        var report = FakeTestabilityEngine.PassingSmoke() with
        {
            Checks = [CheckResult.Pass("visit:admin:/a", CheckKind.RouteVisit) with { Classification = Classification.Flaky }],
        };

        var result = Interpret(report);

        Assert.Equal(UiTestingStatus.Passed, result.Evidence.Status);
        Assert.Contains("flaky", Assert.Single(result.Notes));
    }
}
