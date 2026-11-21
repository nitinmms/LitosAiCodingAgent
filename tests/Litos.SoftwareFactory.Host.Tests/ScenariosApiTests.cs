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
/// README_Litos.Probe.Web.md W3.6: a project's scenarios, written and approved on the Testability
/// panel, run after the smoke on every task and in main's Gate baseline.
/// </summary>
public sealed class ScenariosApiTests : IAsyncLifetime
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

    private const string AddNote = """
        { "id": "add-a-note", "title": "Add a note", "criterion": "A person can add a note.",
          "steps": [ { "type": "fill", "target": { "label": "New note" }, "value": "x" },
                     { "type": "click", "target": { "role": "button", "name": "Add note" } },
                     { "type": "expectText", "target": { "testId": "notes" }, "text": "x" } ] }
        """;

    private async Task<Guid> TestableProjectAsync()
    {
        var projectId = await _host.RegisterProjectAsync();
        var profile = VerificationProfile.Parse((await _host.Store.GetProjectAsync(projectId, default))!.VerificationProfileJson).WithWebApp(Contract);
        using var response = await _host.Client.PatchAsJsonAsync($"api/projects/{projectId}", new { profileJson = profile.ToJson() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return projectId;
    }

    private Task<JsonElement> SaveAsync(Guid projectId, string json, HttpStatusCode expected = HttpStatusCode.OK, HttpClient? client = null) =>
        PostAsync(client ?? _host.Client, $"api/projects/{projectId}/scenarios", new { json }, expected);

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object? body, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> ScenarioAsync(Guid projectId, string id) =>
        (await _host.GetAsync($"api/projects/{projectId}/scenarios")).GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("scenarioId").GetString() == id);

    private async Task SettledAsync(Guid projectId)
    {
        var checks = _host.App.Services.GetRequiredService<TestabilityChecks>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (checks.IsChecking(projectId))
        {
            Assert.True(DateTime.UtcNow < deadline, "The check was still under way after 20 seconds.");
            await Task.Delay(50);
        }
    }

    // ---- Writing and approving ----

    [Fact]
    public async Task A_saved_scenario_is_a_draft_and_saving_again_makes_the_next_revision()
    {
        var projectId = await TestableProjectAsync();

        var first = await SaveAsync(projectId, AddNote);
        var second = await SaveAsync(projectId, AddNote.Replace("A person can add a note.", "Anyone can add a note.", StringComparison.Ordinal));

        Assert.Equal((1, "Draft"), (first.GetProperty("revision").GetInt32(), first.GetProperty("status").GetString()));
        Assert.Empty(first.GetProperty("problems").EnumerateArray());
        Assert.Equal(2, second.GetProperty("revision").GetInt32());

        var listed = await ScenarioAsync(projectId, "add-a-note");
        Assert.Equal("Anyone can add a note.", listed.GetProperty("criterion").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("approvedRevision").ValueKind);
        var latest = listed.GetProperty("latest");
        Assert.Equal((2, "Draft"), (latest.GetProperty("revision").GetInt32(), latest.GetProperty("status").GetString()));
        // The stored JSON carries the row's revision, whatever the author typed.
        Assert.Contains("\"revision\": 2", latest.GetProperty("json").GetString());
        Assert.Equal(["2:Draft", "1:Draft"], listed.GetProperty("revisions").EnumerateArray().Select(r => $"{r.GetProperty("revision").GetInt32()}:{r.GetProperty("status").GetString()}"));
    }

    [Theory]
    [InlineData("not json", "Not a scenario")]
    [InlineData("[]", "Give one scenario, not a list")]
    [InlineData("""{ "id": "Bad Id", "steps": [ { "type": "expectUrl", "url": "/" } ] }""", "id: \"Bad Id\" must be lower-case")]
    public async Task What_cannot_be_saved_is_refused_with_why(string json, string why)
    {
        var projectId = await TestableProjectAsync();

        var refused = await SaveAsync(projectId, json, HttpStatusCode.BadRequest);

        Assert.Contains(why, refused.GetProperty("error").GetString());
        Assert.Empty((await _host.GetAsync($"api/projects/{projectId}/scenarios")).GetProperty("scenarios").EnumerateArray());
    }

    [Fact]
    public async Task A_scenario_with_problems_is_saved_but_cannot_be_approved()
    {
        var projectId = await TestableProjectAsync();
        const string signedIn = """{ "id": "as-admin", "role": "admin", "steps": [ { "type": "expectUrl", "url": "/" } ] }""";

        var saved = await SaveAsync(projectId, signedIn);
        Assert.Contains(saved.GetProperty("problems").EnumerateArray(), p => p.GetString()!.StartsWith("role: the app has no login", StringComparison.Ordinal));
        var checkedNow = await PostAsync(_host.Client, $"api/projects/{projectId}/scenarios/validate", new { json = signedIn }, HttpStatusCode.OK);
        Assert.Single(checkedNow.GetProperty("problems").EnumerateArray());

        var refused = await PostAsync(_host.Client, $"api/projects/{projectId}/scenarios/as-admin/revisions/1/approve", null, HttpStatusCode.BadRequest);
        Assert.Contains("Fix its problems first", refused.GetProperty("error").GetString());
        Assert.Single(refused.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task Without_an_adopted_contract_nothing_can_be_written()
    {
        var projectId = await _host.RegisterProjectAsync();

        var refused = await SaveAsync(projectId, AddNote, HttpStatusCode.Conflict);

        Assert.Contains("Adopt a testability contract first", refused.GetProperty("error").GetString());
        Assert.False((await _host.GetAsync($"api/projects/{projectId}/scenarios")).GetProperty("adopted").GetBoolean());
    }

    [Fact]
    public async Task A_member_approves_it_audited_and_mains_baseline_runs_it_at_once()
    {
        var projectId = await TestableProjectAsync();
        await SettledAsync(projectId);
        await SaveAsync(projectId, AddNote);
        await _host.CreateMemberAsync("tester", projectId);
        var member = await _host.SignedInAsync("tester");
        var gatesBefore = _engine.Gates.Count;

        var approved = await PostAsync(member, $"api/projects/{projectId}/scenarios/add-a-note/revisions/1/approve", null, HttpStatusCode.OK);
        await SettledAsync(projectId);

        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(1, (await ScenarioAsync(projectId, "add-a-note")).GetProperty("approvedRevision").GetInt32());
        Assert.Equal(gatesBefore + 1, _engine.Gates.Count);
        Assert.Equal("add-a-note", Assert.Single(_engine.ScenarioRuns.Last(r => r.Kind == "gate").Scenarios).Id);
        var audit = await _host.Store.ListAuditAsync(projectId, 100, default);
        Assert.Contains(audit, a => a.Action == AuditActions.ScenarioApprove && a.DetailsJson!.Contains("\"Revision\":1", StringComparison.Ordinal));
        Assert.Contains(audit, a => a.Action == AuditActions.ScenarioRevise);

        // Approving twice is a conflict: only a draft can be approved.
        await PostAsync(_host.Client, $"api/projects/{projectId}/scenarios/add-a-note/revisions/1/approve", null, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_retired_scenario_no_longer_runs()
    {
        var projectId = await TestableProjectAsync();
        await SaveAsync(projectId, AddNote);
        await PostAsync(_host.Client, $"api/projects/{projectId}/scenarios/add-a-note/revisions/1/approve", null, HttpStatusCode.OK);
        await SettledAsync(projectId);

        var retired = await PostAsync(_host.Client, $"api/projects/{projectId}/scenarios/add-a-note/retire", null, HttpStatusCode.OK);
        await SettledAsync(projectId);

        Assert.Equal(1, retired.GetProperty("retired").GetInt32());
        Assert.True((await ScenarioAsync(projectId, "add-a-note")).GetProperty("retired").GetBoolean());
        Assert.Empty(_engine.ScenarioRuns.Last(r => r.Kind == "gate").Scenarios);
        await PostAsync(_host.Client, $"api/projects/{projectId}/scenarios/no-such/retire", null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Run_on_main_tries_a_draft_alone_and_shows_its_steps_and_screenshots()
    {
        var projectId = await TestableProjectAsync();
        await SaveAsync(projectId, AddNote);

        var run = await PostAsync(_host.Client, $"api/projects/{projectId}/scenarios/add-a-note/revisions/1/run", null, HttpStatusCode.OK);

        Assert.Equal("add-a-note", Assert.Single(_engine.ScenarioRuns.Last(r => r.Kind == "scenarios").Scenarios).Id);
        Assert.Equal("Passed", run.GetProperty("outcome").GetString());
        var check = Assert.Single(run.GetProperty("checks").EnumerateArray());
        var step = Assert.Single(check.GetProperty("steps").EnumerateArray());
        Assert.Equal("open /", step.GetProperty("description").GetString());
        Assert.StartsWith("/api/run-artifacts/", step.GetProperty("screenshot").GetString());
        Assert.Equal("Draft", (await ScenarioAsync(projectId, "add-a-note")).GetProperty("latest").GetProperty("status").GetString());
    }

    // ---- Tasks and main's baseline ----

    private async Task<Guid> ApprovedAsync()
    {
        var projectId = await TestableProjectAsync();
        await SaveAsync(projectId, AddNote);
        await PostAsync(_host.Client, $"api/projects/{projectId}/scenarios/add-a-note/revisions/1/approve", null, HttpStatusCode.OK);
        await SettledAsync(projectId);
        return projectId;
    }

    private static ProbeWebReport SmokeWithScenario(CheckResult scenario) => FakeTestabilityEngine.PassingSmoke() with
    {
        Outcome = scenario.Failed ? ProbeWebOutcome.Failed : ProbeWebOutcome.Passed,
        Checks = [.. FakeTestabilityEngine.PassingSmoke().Checks, scenario with { Scenario = new ScenarioInfo("add-a-note", 1, "Add a note", "A person can add a note.") }],
    };

    private static UiTestEvidence UiTestingOf(ThreadDetails details) =>
        JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff!.EvidenceJson, FactoryWire.Json)!.UiTesting!;

    [Fact]
    public async Task A_task_runs_the_approved_scenarios_after_its_smoke_and_the_handoff_carries_their_steps()
    {
        var projectId = await ApprovedAsync();
        _engine.SmokeReports.Enqueue(SmokeWithScenario(CheckResult.Pass("scenario:add-a-note", CheckKind.Scenario) with
        {
            Steps = [new StepResult(0, "open /", CheckStatus.Passed, null, 20), new StepResult(1, "click button \"Add note\"", CheckStatus.Passed, null, 40, "shot-1")],
            Evidence = new Evidence { Screenshots = ["shot-1"] },
        }));
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal("add-a-note", Assert.Single(_engine.ScenarioRuns.Last(r => r.Kind == "smoke").Scenarios).Id);
        Assert.Contains(details.Messages, m => m.Text.Contains("then the project's approved scenario.", StringComparison.Ordinal));
        var check = UiTestingOf(details).Checks.Single(c => c.Id == "scenario:add-a-note");
        Assert.Equal(("A person can add a note.", 1), (check.Scenario!.Criterion, check.Scenario.Revision));
        Assert.Equal(["open /", "click button \"Add note\""], check.Steps!.Select(s => s.Description));
    }

    [Fact]
    public async Task A_scenario_the_task_broke_is_repaired_and_a_renamed_button_is_left_for_a_person()
    {
        var projectId = await ApprovedAsync();
        _engine.SmokeReports.Enqueue(SmokeWithScenario(CheckResult.Fail("scenario:add-a-note", CheckKind.Scenario, Classification.ApplicationDefect,
            "Step 3 of 3, expect [data-testid=notes] to show \"x\": it shows \"\".")));
        _engine.SmokeReports.Enqueue(SmokeWithScenario(CheckResult.Fail("scenario:add-a-note", CheckKind.Scenario, Classification.TestDefect,
            "Step 2 of 3, click button \"Add note\": No button named \"Add note\" on /. Buttons on the page: \"Save note\".")));
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var repair = _host.Workers.Turns.Single(t => t.Kind == TurnKind.Repair);
        Assert.Contains("scenario:add-a-note", repair.Brief);
        Assert.Contains("it shows", repair.Brief);
        var evidence = JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff!.EvidenceJson, FactoryWire.Json)!;
        Assert.Contains(evidence.KnownLimitations, l => l.StartsWith("Browser check scenario:add-a-note needs a person, not a code change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scenario_main_already_fails_is_a_gap_on_main_and_not_the_tasks()
    {
        _engine.GateReport = new ProbeWebReport
        {
            Kind = ReportKind.Gate,
            Outcome = ProbeWebOutcome.Failed,
            Checks =
            [
                CheckResult.Pass("build", CheckKind.Build), CheckResult.Pass("start", CheckKind.Start),
                CheckResult.Fail("scenario:add-a-note", CheckKind.Scenario, Classification.ApplicationDefect, "it shows \"\".") with
                {
                    Scenario = new ScenarioInfo("add-a-note", 1, null, null),
                },
            ],
        };
        var projectId = await ApprovedAsync();

        var view = await _host.GetAsync($"api/projects/{projectId}/testability");
        Assert.Equal("TestReady", view.GetProperty("status").GetString());
        Assert.Contains(view.GetProperty("gaps").EnumerateArray(), g => g.GetString()!.StartsWith("scenario:add-a-note", StringComparison.Ordinal));

        _engine.SmokeReports.Enqueue(SmokeWithScenario(CheckResult.Fail("scenario:add-a-note", CheckKind.Scenario, Classification.ApplicationDefect, "it shows \"\".")));
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.DoesNotContain(_host.Workers.Turns, t => t.Kind == TurnKind.Repair);
        Assert.Equal("scenario:add-a-note", Assert.Single(UiTestingOf(details).PreExisting).Id);
    }
}
