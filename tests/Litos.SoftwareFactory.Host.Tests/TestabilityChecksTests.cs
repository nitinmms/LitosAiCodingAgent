using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.Probe.Web;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Detection;
using Litos.Probe.Web.Profiles;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Testability;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Probe.Web as the host tests see it: scripted detection and dry runs, no app and no browser.</summary>
public sealed class FakeTestabilityEngine : ITestabilityEngine
{
    private readonly ProbeWebEngine _real = new(StackProfiles.Embedded());

    public DetectionResult Detection { get; set; } =
        new(DetectionStatus.Detected, [new DetectionCandidate("node-express-spa", 1, "", ["package.json"])]);

    public ProbeWebReport DryRunReport { get; set; } = new()
    {
        Kind = ReportKind.DryRun,
        Outcome = ProbeWebOutcome.Passed,
        Checks = [CheckResult.Pass("build", CheckKind.Build), CheckResult.Pass("start", CheckKind.Start)
            with { }, CheckResult.Pass("visit:anonymous:/", CheckKind.RouteVisit) with { Route = "/", Evidence = new Evidence { Screenshots = ["3f2c0000-0000-0000-0000-000000000001"] } }],
        Findings = ["No contract: these are the node-express-spa profile's defaults (npm start).", "No reset declared: add one (strategy: none if the app keeps no data)."],
    };

    public List<(string Root, string Profile, string? Directory)> DryRuns { get; } = [];

    public Func<Task>? Hold { get; set; }

    public DetectionResult Detect(string repoRoot) => Detection;

    public StackProfile? Profile(string id) => _real.Profile(id);

    public IReadOnlyList<string> Validate(WebAppContract contract) => _real.Validate(contract);

    public async Task<ProbeWebReport> DryRunAsync(string repoRoot, StackProfile profile, string? appDirectory, ProbeWebOptions options, CancellationToken ct)
    {
        DryRuns.Add((repoRoot, profile.Id, appDirectory));
        if (Hold is { } hold)
            await hold();
        return DryRunReport;
    }

    public ProbeWebReport GateReport { get; set; } = new()
    {
        Kind = ReportKind.Gate,
        Outcome = ProbeWebOutcome.Passed,
        Checks = [CheckResult.Pass("build", CheckKind.Build), CheckResult.Pass("start", CheckKind.Start), CheckResult.Pass("lint", CheckKind.SelectorLint)],
    };

    public List<(string Root, WebAppContract Contract, GateScope Scope)> Gates { get; } = [];

    public Task<ProbeWebReport> GateAsync(string workingCopy, WebAppContract contract, GateScope scope, ProbeWebOptions options, CancellationToken ct)
    {
        Gates.Add((workingCopy, contract, scope));
        return Task.FromResult(GateReport);
    }

    public Task<ProbeWebReport> SmokeAsync(string workingCopy, WebAppContract contract, ProbeWebOptions options, CancellationToken ct) =>
        throw new NotSupportedException("W2.5");
}

/// <summary>
/// W2.3 of README_Litos.Probe.Web.md: the testability check at registration, after a profile
/// change and on "Check again" (testability-architecture.md §6.1).
/// </summary>
public sealed class TestabilityChecksTests : IAsyncLifetime
{
    private readonly FakeTestabilityEngine _engine = new();
    private TestHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await TestHost.StartAsync(
            o => o.TestabilityOnRegistration = true,
            s => s.AddSingleton<ITestabilityEngine>(_engine),
            startCoordinator: false);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private TestabilityChecks Checks => _host.App.Services.GetRequiredService<TestabilityChecks>();

    /// <summary>The project's testability once no check is under way.</summary>
    private async Task<JsonElement> SettledAsync(Guid projectId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var view = await _host.GetAsync($"api/projects/{projectId}/testability");
            if (view.GetProperty("status").GetString() is not ("Checking" or "NotChecked"))
                return view;
            Assert.True(DateTime.UtcNow < deadline, "The check was still under way after 20 seconds.");
            await Task.Delay(50);
        }
    }

    private void WriteOnMain(Guid projectId, string file, string text)
    {
        var reading = _host.Workspaces.ReadingOf(projectId);
        Directory.CreateDirectory(reading.Path);
        File.WriteAllText(Path.Combine(reading.Path, file), text);
    }

    [Fact]
    public async Task Registering_a_project_checks_it_and_the_dry_run_lists_the_gaps()
    {
        var projectId = await _host.RegisterProjectAsync();

        var view = await SettledAsync(projectId);

        Assert.Equal("Gaps", view.GetProperty("status").GetString());
        Assert.Equal("node-express-spa", view.GetProperty("stack").GetString());
        Assert.Contains("No reset declared", view.GetProperty("gaps").ToString());
        Assert.False(view.GetProperty("adopted").GetBoolean());
        var check = view.GetProperty("run").GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "visit:anonymous:/");
        Assert.Equal("/api/run-artifacts/3f2c0000-0000-0000-0000-000000000001", check.GetProperty("screenshots")[0].GetString());
        Assert.Equal(("node-express-spa", (string?)""), (_engine.DryRuns.Single().Profile, _engine.DryRuns.Single().Directory));
    }

    [Fact]
    public async Task The_projects_list_shows_each_projects_status_for_its_column()
    {
        var projectId = await _host.RegisterProjectAsync();
        await SettledAsync(projectId);

        var listed = (await _host.GetAsync("api/projects")).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == projectId);

        var testability = listed.GetProperty("testability");
        Assert.Equal("Gaps", testability.GetProperty("status").GetString());
        Assert.Equal("node-express-spa", testability.GetProperty("stack").GetString());
        Assert.False(testability.TryGetProperty("gaps", out _));
    }

    [Fact]
    public async Task A_repository_with_no_web_app_is_said_so_without_a_dry_run()
    {
        _engine.Detection = new DetectionResult(DetectionStatus.NotAWebApp, []);
        var projectId = await _host.RegisterProjectAsync();

        Assert.Equal("NotAWebApp", (await SettledAsync(projectId)).GetProperty("status").GetString());
        Assert.Empty(_engine.DryRuns);
    }

    [Fact]
    public async Task A_web_stack_without_a_toolchain_is_not_supported_yet()
    {
        _engine.Detection = new DetectionResult(DetectionStatus.StackNotSupported, [new DetectionCandidate("unsupported-web", 0, "", ["Gemfile"])]);
        var projectId = await _host.RegisterProjectAsync();

        Assert.Equal("StackNotSupported", (await SettledAsync(projectId)).GetProperty("status").GetString());
        Assert.Empty(_engine.DryRuns);
    }

    [Fact]
    public async Task A_valid_contract_on_the_default_branch_is_found_and_waits_to_be_adopted()
    {
        _host.Options.TestabilityOnRegistration = false;
        var projectId = await _host.RegisterProjectAsync();
        WriteOnMain(projectId, "litos.test.yaml", """
            version: 1
            application:
              stack: node-express-spa
              processes:
                - name: web
                  command: [npm, start]
              base_url: web
              reset: { strategy: none }
            testability:
              routes: { public: [/] }
            """);

        await _host.PostAsync($"api/projects/{projectId}/testability/check", null, HttpStatusCode.Accepted);
        var view = await SettledAsync(projectId);

        Assert.Equal("ContractFound", view.GetProperty("status").GetString());
        Assert.True(view.GetProperty("contract").GetProperty("valid").GetBoolean());
        Assert.Empty(_engine.DryRuns);
    }

    [Fact]
    public async Task A_contract_that_does_not_validate_is_a_gap_and_the_dry_run_still_runs()
    {
        _host.Options.TestabilityOnRegistration = false;
        var projectId = await _host.RegisterProjectAsync();
        WriteOnMain(projectId, "litos.test.yaml", "version: 1\napplication:\n  processes: []\n");

        await _host.PostAsync($"api/projects/{projectId}/testability/check", null, HttpStatusCode.Accepted);
        var view = await SettledAsync(projectId);

        Assert.Equal("Gaps", view.GetProperty("status").GetString());
        Assert.False(view.GetProperty("contract").GetProperty("valid").GetBoolean());
        Assert.Contains("does not run as written", view.GetProperty("gaps").ToString());
        Assert.Single(_engine.DryRuns);
    }

    [Fact]
    public async Task A_dry_run_that_could_not_start_the_browser_says_the_check_could_not_run()
    {
        _engine.DryRunReport = _engine.DryRunReport with
        {
            Outcome = ProbeWebOutcome.EnvironmentFailure,
            Checks = [CheckResult.Pass("build", CheckKind.Build), CheckResult.Fail("browser", CheckKind.Start, Classification.EnvironmentFailure, "Chromium for Playwright is not installed.")],
        };
        var projectId = await _host.RegisterProjectAsync();

        var view = await SettledAsync(projectId);

        Assert.Equal("CouldNotCheck", view.GetProperty("status").GetString());
        Assert.Contains("Chromium", view.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task A_repository_that_cannot_be_fetched_could_not_be_checked()
    {
        _host.Options.TestabilityOnRegistration = false;
        var projectId = await _host.RegisterProjectAsync();
        _host.Workspaces.ReadingOf(projectId).FailFetch = new IOException("fatal: repository not found");

        await _host.PostAsync($"api/projects/{projectId}/testability/check", null, HttpStatusCode.Accepted);
        var view = await SettledAsync(projectId);

        Assert.Equal("CouldNotCheck", view.GetProperty("status").GetString());
        Assert.Contains("repository not found", view.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task An_adopted_contract_runs_the_whole_gate_on_main_and_is_test_ready()
    {
        _host.Options.TestabilityOnRegistration = false;
        var projectId = await _host.RegisterProjectAsync();
        var profile = VerificationProfile.Parse((await _host.Store.GetProjectAsync(projectId, default))!.VerificationProfileJson).WithWebApp(new WebAppContract
        {
            Application = new AppDefinition
            {
                Stack = "node-express-spa",
                Processes = [new ProcessDefinition { Name = "web", Command = ["npm", "start"] }],
                BaseUrl = "web",
                Reset = new ResetDefinition { Strategy = ResetStrategy.None },
            },
        });
        using (var patched = await _host.Client.PatchAsJsonAsync($"api/projects/{projectId}", new { profileJson = profile.ToJson() }))
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        await _host.PostAsync($"api/projects/{projectId}/testability/check", null, HttpStatusCode.Accepted);
        var view = await SettledAsync(projectId);

        Assert.Equal("TestReady", view.GetProperty("status").GetString());
        Assert.True(view.GetProperty("adopted").GetBoolean());
        Assert.Equal("gate", view.GetProperty("run").GetProperty("kind").GetString());
        Assert.Empty(_engine.DryRuns);
        Assert.Equal(8, _engine.Gates.Single().Scope.Steps.Count);
    }

    [Fact]
    public async Task A_check_asked_for_while_one_runs_follows_it_and_the_list_shows_checking_meanwhile()
    {
        var release = new TaskCompletionSource();
        _engine.Hold = () => release.Task;
        var projectId = await _host.RegisterProjectAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_engine.DryRuns.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var during = (await _host.GetAsync($"api/projects/{projectId}/testability")).GetProperty("status").GetString();
        Checks.CheckNow(projectId);
        _engine.Hold = null;
        release.SetResult();
        await SettledAsync(projectId);
        await Checks.Latest!;

        Assert.Equal("Checking", during);
        Assert.Equal(2, _engine.DryRuns.Count);
    }

    [Fact]
    public async Task A_member_can_check_their_project_but_not_anothers()
    {
        _host.Options.TestabilityOnRegistration = false;
        var mine = await _host.RegisterProjectAsync();
        var other = await _host.RegisterProjectAsync("other");
        await _host.CreateMemberAsync("max", mine);
        var max = await _host.SignedInAsync("max");

        using var allowed = await max.PostAsync($"api/projects/{mine}/testability/check", null);
        using var refused = await max.PostAsync($"api/projects/{other}/testability/check", null);
        using var hidden = await max.GetAsync($"api/projects/{other}/testability");

        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task A_project_never_checked_says_so()
    {
        _host.Options.TestabilityOnRegistration = false;
        var projectId = await _host.RegisterProjectAsync();

        var view = await _host.GetAsync($"api/projects/{projectId}/testability");

        Assert.Equal("NotChecked", view.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("run").ValueKind);
    }

    // ---- Adoption and the Gate baseline (W2.4) ----

    private const string Contract = """
        version: 1
        application:
          stack: node-express-spa
          processes:
            - name: web
              command: [npm, start]
              environment:
                PORT: "{port:web}"
                ADMIN_PASSWORD: "{secret:TEST_ADMIN_PASS}"
          base_url: web
          reset: { strategy: none }
        testability:
          routes: { public: [/] }
        """;

    private async Task<Guid> ProjectWithContractOnMainAsync(string contract = Contract)
    {
        _host.Options.TestabilityOnRegistration = false;
        var projectId = await _host.RegisterProjectAsync();
        WriteOnMain(projectId, "litos.test.yaml", contract);
        return projectId;
    }

    private async Task SetSecretAsync(Guid projectId, string name = "TEST_ADMIN_PASS")
    {
        using var response = await _host.Client.PutAsJsonAsync($"api/projects/{projectId}/secrets/{name}", new { value = "hunter2" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<HttpResponseMessage> AdoptAsync(Guid projectId, string commit = "base000", HttpClient? client = null) =>
        await (client ?? _host.Client).PostAsJsonAsync($"api/projects/{projectId}/testability/adopt", new { commit });

    [Fact]
    public async Task The_preview_shows_the_diff_and_waits_for_the_contracts_secrets()
    {
        var projectId = await ProjectWithContractOnMainAsync();

        var preview = await _host.GetAsync($"api/projects/{projectId}/testability/adopt");

        Assert.Equal("base000", preview.GetProperty("commit").GetString());
        Assert.True(preview.GetProperty("found").GetBoolean());
        Assert.Equal(["TEST_ADMIN_PASS"], preview.GetProperty("missingSecrets").EnumerateArray().Select(e => e.GetString()));
        Assert.False(preview.GetProperty("canAdopt").GetBoolean());
        var diff = preview.GetProperty("diff").EnumerateArray().ToList();
        Assert.All(diff, l => Assert.Equal("Added", l.GetProperty("kind").GetString()));
        Assert.Contains(diff, l => l.GetProperty("text").GetString()!.Contains("node-express-spa"));

        await SetSecretAsync(projectId);
        Assert.True((await _host.GetAsync($"api/projects/{projectId}/testability/adopt")).GetProperty("canAdopt").GetBoolean());
    }

    [Fact]
    public async Task Adopting_saves_the_contract_with_where_it_came_from_audits_it_and_runs_the_gate()
    {
        var projectId = await ProjectWithContractOnMainAsync();
        await SetSecretAsync(projectId);
        await _host.CreateMemberAsync("max", projectId);
        var max = await _host.SignedInAsync("max");

        using var response = await AdoptAsync(projectId, client: max);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var project = (await _host.Store.GetProjectAsync(projectId, default))!;
        Assert.Equal(2, project.ProfileRevision);
        var adopted = VerificationProfile.Parse(project.VerificationProfileJson).WebApp()!;
        Assert.Equal(("base000", "litos.test.yaml"), (adopted.Testability.AdoptedFrom!.Commit, adopted.Testability.AdoptedFrom.Path));
        Assert.Contains(await _host.Store.ListAuditAsync(projectId, 50, default), a => a.Action == Core.Store.AuditActions.TestabilityAdopt);
        Assert.Equal("TestReady", (await SettledAsync(projectId)).GetProperty("status").GetString());
        Assert.Single(_engine.Gates);
        Assert.False((await _host.GetAsync($"api/projects/{projectId}/testability/adopt")).GetProperty("changes").GetBoolean());
    }

    [Fact]
    public async Task A_contract_whose_secrets_are_not_set_is_not_adopted()
    {
        var projectId = await ProjectWithContractOnMainAsync();

        using var response = await AdoptAsync(projectId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("TEST_ADMIN_PASS", await response.Content.ReadAsStringAsync());
        Assert.Null(VerificationProfile.Parse((await _host.Store.GetProjectAsync(projectId, default))!.VerificationProfileJson).WebApp());
    }

    [Fact]
    public async Task A_push_after_the_preview_stops_the_adoption()
    {
        var projectId = await ProjectWithContractOnMainAsync();
        await SetSecretAsync(projectId);
        _host.Workspaces.ReadingOf(projectId).SetHead("new1234");

        using var response = await AdoptAsync(projectId, commit: "base000");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("moved to new1234", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, "There is no litos.test.yaml")]
    [InlineData("version: 1\napplication:\n  processes: []\n", "does not run as written")]
    [InlineData("application: [broken\n", "does not run as written")]
    public async Task A_missing_or_broken_contract_is_not_adopted(string? contract, string error)
    {
        _host.Options.TestabilityOnRegistration = false;
        var projectId = await _host.RegisterProjectAsync();
        if (contract is not null)
            WriteOnMain(projectId, "litos.test.yaml", contract);

        using var response = await AdoptAsync(projectId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Adopting_needs_the_reviewed_commit()
    {
        var projectId = await ProjectWithContractOnMainAsync();
        using var response = await _host.Client.PostAsJsonAsync($"api/projects/{projectId}/testability/adopt", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_gate_that_fails_on_main_marks_the_project_broken_on_main_with_what_failed()
    {
        _engine.GateReport = _engine.GateReport with
        {
            Outcome = ProbeWebOutcome.Failed,
            Checks = [CheckResult.Pass("start", CheckKind.Start), CheckResult.Fail("denied:patient:/admin", CheckKind.Denied, Classification.ApplicationDefect, "patient opened /admin (HTTP 200).")],
        };
        var projectId = await ProjectWithContractOnMainAsync();
        await SetSecretAsync(projectId);

        using (var response = await AdoptAsync(projectId))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await SettledAsync(projectId);

        Assert.Equal("BrokenOnMain", view.GetProperty("status").GetString());
        Assert.Contains("denied:patient:/admin: patient opened /admin", view.GetProperty("gaps").ToString());
    }

    [Fact]
    public async Task A_gate_that_could_not_run_says_the_check_could_not_run()
    {
        _engine.GateReport = _engine.GateReport with
        {
            Outcome = ProbeWebOutcome.EnvironmentFailure,
            Checks = [CheckResult.Fail("services", CheckKind.Services, Classification.EnvironmentFailure, "Docker is not running, and the contract's services need it.")],
        };
        var projectId = await ProjectWithContractOnMainAsync();
        await SetSecretAsync(projectId);

        using (await AdoptAsync(projectId)) { }
        var view = await SettledAsync(projectId);

        Assert.Equal("CouldNotCheck", view.GetProperty("status").GetString());
        Assert.Contains("Docker is not running", view.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task A_newer_contract_on_main_than_the_adopted_one_is_a_gap()
    {
        var projectId = await ProjectWithContractOnMainAsync();
        await SetSecretAsync(projectId);
        using (await AdoptAsync(projectId)) { }
        await SettledAsync(projectId);
        WriteOnMain(projectId, "litos.test.yaml", Contract.Replace("public: [/]", "public: [/, /about]"));

        await _host.PostAsync($"api/projects/{projectId}/testability/check", null, HttpStatusCode.Accepted);
        await Checks.Latest!;
        var view = await SettledAsync(projectId);

        Assert.Equal("TestReady", view.GetProperty("status").GetString());
        Assert.Contains("differs from the adopted contract", view.GetProperty("gaps").ToString());
        var preview = await _host.GetAsync($"api/projects/{projectId}/testability/adopt");
        Assert.Contains(preview.GetProperty("diff").EnumerateArray(), l => l.GetProperty("kind").GetString() == "Added" && l.GetProperty("text").GetString()!.Contains("/about"));
    }

    [Fact]
    public async Task Another_projects_contract_cannot_be_adopted()
    {
        var projectId = await ProjectWithContractOnMainAsync();
        var other = await _host.RegisterProjectAsync("other");
        await _host.CreateMemberAsync("mia", other);
        var mia = await _host.SignedInAsync("mia");

        using var preview = await mia.GetAsync($"api/projects/{projectId}/testability/adopt");
        using var adopt = await AdoptAsync(projectId, client: mia);

        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, adopt.StatusCode);
    }

    [Fact]
    public void A_contract_diff_shows_what_changed_line_by_line()
    {
        var lines = Core.Testability.ContractDiff.Lines("a\nb\nc\n", "a\nc\nd");

        Assert.Equal(
            [(Core.Testability.DiffKind.Same, "a"), (Core.Testability.DiffKind.Removed, "b"), (Core.Testability.DiffKind.Same, "c"), (Core.Testability.DiffKind.Added, "d")],
            lines.Select(l => (l.Kind, l.Text)));
        Assert.True(Core.Testability.ContractDiff.Changes(lines));
        Assert.False(Core.Testability.ContractDiff.Changes(Core.Testability.ContractDiff.Lines("x", "x\n")));
        Assert.Equal(["y"], Core.Testability.ContractDiff.Lines("", "y").Select(l => l.Text));
    }

    [Fact]
    public void The_report_round_trips_as_stored()
    {
        var report = new TestabilityReport
        {
            Status = TestabilityStatus.Gaps,
            Commit = "abc",
            Stack = "node-express-spa",
            Candidates = [new StackCandidate("node-express-spa", 1, "web", ["web/package.json"])],
            Gaps = ["No reset declared."],
            Contract = new ContractOnMain("litos.test.yaml", "abc", ["x"]),
            Run = _engine.DryRunReport,
        };

        var back = TestabilityReport.FromJson(report.ToJson());

        Assert.Equal(report.ToJson(), back.ToJson());
        Assert.False(back.Contract!.Valid);
    }
}
