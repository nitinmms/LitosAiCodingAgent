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
using Litos.SoftwareFactory.Host.Testability;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// README_Litos.Probe.Web.md W3.7: a task's own scenarios. A spec drafts them and they are approved
/// with it; without a spec the work turn drafts them for evidence; on Accept the ones that passed
/// become the project's drafts.
/// </summary>
public sealed class TaskScenariosTests : IAsyncLifetime
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
        Testability = new TestabilityDefinition { Routes = new RouteSet { Public = ["/orders"] } },
    };

    private const string Export = """
        {"id":"export","criterion":"Administrators can export.","start":"/orders",
         "steps":[{"type":"click","target":{"role":"button","name":"Export"}},{"type":"expectText","target":{"testId":"status"},"text":"Exported"},{"type":"screenshot"}]}
        """;

    private async Task<Guid> TestableProjectAsync()
    {
        var projectId = await _host.RegisterProjectAsync();
        var profile = VerificationProfile.Parse((await _host.Store.GetProjectAsync(projectId, default))!.VerificationProfileJson).WithWebApp(Contract);
        using var response = await _host.Client.PatchAsJsonAsync($"api/projects/{projectId}", new { profileJson = profile.ToJson() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return projectId;
    }

    private static ProbeWebReport SmokeWith(CheckResult scenario) => FakeTestabilityEngine.PassingSmoke() with
    {
        Outcome = scenario.Failed ? ProbeWebOutcome.Failed : ProbeWebOutcome.Passed,
        Checks = [.. FakeTestabilityEngine.PassingSmoke().Checks, scenario with { Scenario = new ScenarioInfo("export", 1, null, "Administrators can export.") }],
    };

    private static CheckResult Passed() => CheckResult.Pass("scenario:export", CheckKind.Scenario) with
    {
        Steps = [new StepResult(0, "open /orders", CheckStatus.Passed, null, 10), new StepResult(3, "screenshot", CheckStatus.Passed, null, 10, "shot-export")],
        Evidence = new Evidence { Screenshots = ["shot-export"] },
    };

    private static UiTestEvidence UiTestingOf(ThreadDetails details) =>
        JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff!.EvidenceJson, FactoryWire.Json)!.UiTesting!;

    // ---- With a specification ----

    [Fact]
    public async Task A_spec_drafts_its_scenarios_the_host_refuses_a_bad_one_and_the_approved_ones_run_like_the_projects()
    {
        var projectId = await TestableProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        SubmissionResponse? refused = null, uncovered = null;
        _host.Workers.Script.Enqueue(async call =>
        {
            refused = await call.Worker.SubmitAsync(call.SessionId, TestHost.DefaultSpec with
            {
                Scenarios = ["""{"id":"export","criterion":"Exports work.","role":"admin","steps":[{"type":"expectUrl","url":"/"}]}"""],
            });
            uncovered = await call.Worker.SubmitAsync(call.SessionId, TestHost.DefaultSpec with { Scenarios = [Export] });
            await call.Worker.SubmitAsync(call.SessionId, TestHost.DefaultSpec with
            {
                Scenarios = [Export],
                NotInBrowser = [new NotInBrowser("Others get a 403.", "a refused API call has no page to check")],
            });
            return FakeWorkerLauncher.Done();
        });

        await _host.DelegateAsync(threadId, "@factory spec Export the orders list as CSV.");
        await WaitForSpecAsync(threadId);

        // W3.8 finding 2: every criterion has a scenario or a reason.
        Assert.False(uncovered!.Accepted);
        Assert.Contains("\"Others get a 403.\": no scenario checks it. Add one, or name it in notInBrowser", uncovered.Message);
        Assert.Contains("a refused API call has no page to check", (await _host.ThreadAsync(threadId)).LatestSpec!.NotInBrowserJson);
        Assert.False(refused!.Accepted);
        Assert.Contains("Fix them and call submit_spec again", refused.Message);
        Assert.Contains("role: the app has no login", refused.Message);
        Assert.Contains("criterion: \"Exports work.\" is not one of this task's acceptance criteria", refused.Message);
        var spec = _host.Workers.Turns.Single(t => t.Kind == TurnKind.Spec);
        Assert.Contains("## Browser scenarios", spec.Brief);
        Assert.Contains("The app has no sign-in: leave `role` out.", spec.Brief);

        await _host.PostAsync($"api/threads/{threadId}/spec/1/approve", null, HttpStatusCode.OK);
        _engine.SmokeReports.Enqueue(SmokeWith(CheckResult.Fail("scenario:export", CheckKind.Scenario, Classification.ApplicationDefect, "It shows \"\".")));
        await _host.DelegateAsync(threadId, "@factory Build it.");
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var implement = _host.Workers.Turns.First(t => t.Kind == TurnKind.Implement);
        Assert.Contains("The approved specification comes with browser scenarios", implement.Brief);
        Assert.Contains("- export: signed out, open /orders; click button \"Export\"", implement.Brief);
        Assert.Equal("export", Assert.Single(_engine.ScenarioRuns.First(r => r.Kind == "smoke").Scenarios).Id);
        // Approved with the spec: a failure the task caused goes back for repair.
        Assert.Contains("scenario:export", _host.Workers.Turns.Single(t => t.Kind == TurnKind.Repair).Brief);
        var check = UiTestingOf(details).Checks.Single(c => c.Id == "scenario:export");
        Assert.Equal(ScenarioSources.Spec, check.Source);
        Assert.Equal(ScenarioSources.Spec, Assert.Single(UiTestingOf(details).TaskScenarios).Source);
        // The reason reaches the handoff, for the criteria table.
        var evidence = JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff!.EvidenceJson, FactoryWire.Json)!;
        Assert.Equal([new NotInBrowser("Others get a 403.", "a refused API call has no page to check")], evidence.NotInBrowser);
    }

    [Fact]
    public async Task A_spec_scenario_may_check_several_criteria_and_a_reason_must_name_a_real_one()
    {
        // W3.8 findings 2 and 6.
        var projectId = await TestableProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        const string Both = """
            {"id":"export","criterion":"Administrators can export.","criteria":["Others get a 403."],"start":"/orders",
             "steps":[{"type":"expectText","target":{"testId":"status"},"text":"Exported"}]}
            """;
        const string Wrong = """
            {"id":"export","criterion":"Administrators can export.","criteria":["Nobody else can."],"start":"/orders",
             "steps":[{"type":"expectText","target":{"testId":"status"},"text":"Exported"}]}
            """;
        SubmissionResponse? badReason = null, badCriteria = null, none = null;
        _host.Workers.Script.Enqueue(async call =>
        {
            none = await call.Worker.SubmitAsync(call.SessionId, TestHost.DefaultSpec);
            badReason = await call.Worker.SubmitAsync(call.SessionId, TestHost.DefaultSpec with
            {
                Scenarios = [Export],
                NotInBrowser = [new NotInBrowser("Refused users.", "an API call")],
            });
            badCriteria = await call.Worker.SubmitAsync(call.SessionId, TestHost.DefaultSpec with { Scenarios = [Wrong] });
            await call.Worker.SubmitAsync(call.SessionId, TestHost.DefaultSpec with { Scenarios = [Both] });
            return FakeWorkerLauncher.Done();
        });

        await _host.DelegateAsync(threadId, "@factory spec Export the orders list as CSV.");
        await WaitForSpecAsync(threadId);

        Assert.Contains("\"Administrators can export.\": no scenario checks it", none!.Message);
        Assert.Contains("\"Others get a 403.\": no scenario checks it", none.Message);
        Assert.Contains("notInBrowser: \"Refused users.\" is not one of this task's acceptance criteria", badReason!.Message);
        Assert.Contains("criteria: \"Nobody else can.\" is not one of this task's acceptance criteria", badCriteria!.Message);
        var payload = (await _host.ThreadAsync(threadId)).Messages.Last(m => m.Kind == MessageKind.Spec).PayloadJson!;
        Assert.Contains("\"criteria\":[\"Administrators can export.\",\"Others get a 403.\"]", payload);
    }

    private async Task WaitForSpecAsync(Guid threadId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await _host.ThreadAsync(threadId)).LatestSpec is null || (await _host.ThreadAsync(threadId)).Thread.State != LifecycleState.Draft)
        {
            Assert.True(DateTime.UtcNow < deadline, "No specification was proposed.");
            await Task.Delay(40);
        }
    }

    // ---- Without a specification ----

    private void TheWorkTurnDrafts(params string[] scenarios) => _host.Workers.Script.Enqueue(async call =>
    {
        _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "export\n");
        await call.Worker.SubmitAsync(call.SessionId, FakeWorkerLauncher.Work() with { Scenarios = scenarios });
        return FakeWorkerLauncher.Done();
    });

    [Fact]
    public async Task Without_a_spec_the_work_turn_drafts_them_and_a_failing_draft_is_disclosed_never_repaired()
    {
        var projectId = await TestableProjectAsync();
        TheWorkTurnDrafts(Export);
        _engine.SmokeReports.Enqueue(SmokeWith(CheckResult.Fail("scenario:export", CheckKind.Scenario, Classification.ApplicationDefect, "It shows \"\".")));
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var implement = _host.Workers.Turns.First(t => t.Kind == TurnKind.Implement);
        Assert.Contains("In `submit_work`, add a scenario to `scenarios`", implement.Brief);
        Assert.DoesNotContain(_host.Workers.Turns, t => t.Kind == TurnKind.Repair);
        Assert.Contains(details.Messages, m => m.Text.Contains("then the agent's drafted scenario.", StringComparison.Ordinal));
        var evidence = JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff!.EvidenceJson, FactoryWire.Json)!;
        Assert.Contains(evidence.KnownLimitations, l => l.StartsWith("The agent's drafted scenario export failed (ApplicationDefect)", StringComparison.Ordinal));
        Assert.Equal(ScenarioSources.Drafted, UiTestingOf(details).Checks.Single(c => c.Id == "scenario:export").Source);
    }

    [Fact]
    public async Task A_drafted_scenario_that_cannot_run_is_sent_back_to_the_agent_at_once()
    {
        var projectId = await TestableProjectAsync();
        await PostScenarioAsync(projectId, """{"id":"export","steps":[{"type":"expectUrl","url":"/"}]}""", approve: true);
        SubmissionResponse? refused = null;
        _host.Workers.Script.Enqueue(async call =>
        {
            refused = await call.Worker.SubmitAsync(call.SessionId, FakeWorkerLauncher.Work() with { Scenarios = [Export, """{"id":"Bad","steps":[]}"""] });
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "export\n");
            await call.Worker.SubmitAsync(call.SessionId, FakeWorkerLauncher.Work());
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.False(refused!.Accepted);
        Assert.Contains("Fix them and call submit_work again", refused.Message);
        Assert.Contains("\"export\" is already one of the project's approved scenarios: choose another", refused.Message);
        Assert.Contains("scenarios[2] (Bad): id:", refused.Message);
    }

    [Fact]
    public async Task A_project_without_a_contract_is_not_asked_for_scenarios_and_cannot_send_them()
    {
        var projectId = await _host.RegisterProjectAsync();
        SubmissionResponse? refused = null;
        _host.Workers.Script.Enqueue(async call =>
        {
            refused = await call.Worker.SubmitAsync(call.SessionId, FakeWorkerLauncher.Work() with { Scenarios = [Export] });
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "export\n");
            await call.Worker.SubmitAsync(call.SessionId, FakeWorkerLauncher.Work());
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.DoesNotContain("Browser scenarios", _host.Workers.Turns.First(t => t.Kind == TurnKind.Implement).Brief);
        Assert.Contains("no contract is adopted", refused!.Message);
    }

    // ---- After acceptance ----

    [Fact]
    public async Task Accepting_copies_the_tasks_passing_scenarios_into_the_projects_drafts()
    {
        var projectId = await TestableProjectAsync();
        const string failing = """{"id":"refuses","criterion":"Administrators can export.","steps":[{"type":"expectUrl","url":"/denied"}]}""";
        TheWorkTurnDrafts(Export, failing);
        _engine.SmokeReports.Enqueue(FakeTestabilityEngine.PassingSmoke() with
        {
            Outcome = ProbeWebOutcome.Failed,
            Checks =
            [
                .. FakeTestabilityEngine.PassingSmoke().Checks, Passed(),
                CheckResult.Fail("scenario:refuses", CheckKind.Scenario, Classification.ApplicationDefect, "The page is /orders."),
            ],
        });
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        await _host.PostAsync($"api/threads/{threadId}/accept", null, HttpStatusCode.OK);

        var scenarios = (await _host.Store.ListScenariosAsync(projectId, default)).ToList();
        var copied = Assert.Single(scenarios);
        Assert.Equal(("export", 1, ScenarioStatus.Draft, "Administrators can export."), (copied.ScenarioId, copied.Revision, copied.Status, copied.Criterion));
        var details = await _host.ThreadAsync(threadId);
        Assert.Contains(details.Messages, m => m.Text.StartsWith("Copied 1 browser scenario that passed into the project's scenarios as drafts: export.", StringComparison.Ordinal));
    }

    private async Task PostScenarioAsync(Guid projectId, string json, bool approve)
    {
        using (var saved = await _host.Client.PostAsJsonAsync($"api/projects/{projectId}/scenarios", new { json }))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        if (!approve)
            return;
        var id = JsonDocument.Parse(json).RootElement.GetProperty("id").GetString();
        using var approved = await _host.Client.PostAsync($"api/projects/{projectId}/scenarios/{id}/revisions/1/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }
}
