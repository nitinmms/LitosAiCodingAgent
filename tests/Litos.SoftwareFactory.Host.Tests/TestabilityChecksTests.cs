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

    public Task<ProbeWebReport> GateAsync(string workingCopy, WebAppContract contract, GateScope scope, ProbeWebOptions options, CancellationToken ct) =>
        throw new NotSupportedException("W2.4");

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
        var check = view.GetProperty("dryRun").GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "visit:anonymous:/");
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
    public async Task An_adopted_contract_is_reported_as_adopted_without_a_dry_run()
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

        Assert.Equal("Adopted", view.GetProperty("status").GetString());
        Assert.True(view.GetProperty("adopted").GetBoolean());
        Assert.Empty(_engine.DryRuns);
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
        Assert.Equal(JsonValueKind.Null, view.GetProperty("dryRun").ValueKind);
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
            DryRun = _engine.DryRunReport,
        };

        var back = TestabilityReport.FromJson(report.ToJson());

        Assert.Equal(report.ToJson(), back.ToJson());
        Assert.False(back.Contract!.Valid);
    }
}
