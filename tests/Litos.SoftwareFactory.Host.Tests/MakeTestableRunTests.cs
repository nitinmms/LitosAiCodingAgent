using Litos.Probe.Web;
using Litos.Probe.Web.Core.Reports;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Host.Testability;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// W2.6 of README_Litos.Probe.Web.md: the Make-testable task (testability-architecture.md §6.3).
/// Its brief, the check_testability tool, the Gate on its draft contract and a full review.
/// </summary>
public sealed class MakeTestableRunTests : IAsyncLifetime
{
    private readonly FakeTestabilityEngine _engine = new();
    private TestHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await TestHost.StartAsync(services: s => s.AddSingleton<ITestabilityEngine>(_engine));

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private const string Draft = """
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
        """;

    /// <summary>A draft whose test user's password is a project secret.</summary>
    private const string DraftWithSecret = """
        version: 1
        application:
          stack: node-express-spa
          processes:
            - name: web
              command: [npm, start]
              environment: { TEST_ADMIN_PASS: "{secret:TEST_ADMIN_PASS}" }
          base_url: web
          reset: { strategy: none }
        testability:
          auth:
            type: form
            login_url: /login
            fields: { user: Email, password: Password }
            submit: Log in
            success_when: { url_not_contains: /login }
            roles:
              admin: { user: admin@example.test, password: "{secret:TEST_ADMIN_PASS}" }
          routes:
            public: [/]
            protected: { admin: [/admin] }
        """;

    private static ProbeWebReport FailedGate(params CheckResult[] checks) => new()
    {
        Kind = ReportKind.Gate,
        Outcome = ProbeWebOutcome.Failed,
        Checks = [CheckResult.Pass("build", CheckKind.Build), CheckResult.Pass("start", CheckKind.Start), .. checks],
    };

    private Task SetSecretAsync(Guid projectId, string name) =>
        _host.App.Services.GetRequiredService<FactorySettings>().SetSecretAsync(SecretNames.Project(projectId, name), "not-a-real-password", null, default);

    private async Task<(Guid ProjectId, Guid ThreadId)> MakeTestableAsync(string? draft)
    {
        var projectId = await _host.RegisterProjectAsync();
        await _host.Store.SaveTestabilityCheckAsync(new TestabilityCheck
        {
            ProjectId = projectId,
            ProfileRevision = 1,
            Commit = "base000",
            Status = TestabilityStatus.Gaps,
            ReportJson = new TestabilityReport
            {
                Status = TestabilityStatus.Gaps,
                Stack = "node-express-spa",
                Gaps = ["Started, but listened on 8787, not the assigned port: the app ignores PORT."],
            }.ToJson(),
            CreatedAt = DateTimeOffset.UtcNow,
        }, default);

        var workspace = _host.Workspaces.Of(projectId);
        Directory.CreateDirectory(workspace.Path);
        if (draft is not null)
            File.WriteAllText(Path.Combine(workspace.Path, ContractYaml.FileName), draft);

        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.Store.EditThreadAsync(threadId, null, TaskTypes.Testability, await _host.AdminIdAsync(), DateTimeOffset.UtcNow, default);
        await _host.DelegateAsync(threadId, "@factory Make this app testable.");
        return (projectId, threadId);
    }

    [Fact]
    public async Task It_gets_the_make_testable_brief_the_tool_the_gate_on_its_draft_and_a_full_review()
    {
        _host.Workers.Script.Enqueue(call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write(ContractYaml.FileName, Draft);
            return _host.Workers.Default(call);
        });
        var (_, threadId) = await MakeTestableAsync(Draft);

        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var implement = _host.Workers.Turns.First(t => t.Kind == TurnKind.Implement);
        Assert.StartsWith("# Factory run brief: make the app testable", implement.Brief);
        Assert.Contains("Detected stack: `node-express-spa`.", implement.Brief);
        Assert.Contains("the app ignores PORT", implement.Brief);
        Assert.True(_host.Workers.Workers.First().Launch.TestabilityCheck);

        var gate = Assert.Single(_engine.Gates);
        Assert.Equal(8, gate.Scope.Steps.Count);
        Assert.Empty(_engine.Smokes);

        // A full review, never planned down: the task changes authentication and start-up.
        Assert.Contains(_host.Workers.Turns, t => t.Kind == TurnKind.Review);
        var details = await _host.ThreadAsync(threadId);
        Assert.DoesNotContain(details.Messages, m => m.Text.Contains("light review", StringComparison.OrdinalIgnoreCase) || m.Text.StartsWith("No review", StringComparison.Ordinal));

        // Finding 17: writing the contract is this task's job, and it ran on its own draft.
        var evidence = System.Text.Json.JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff!.EvidenceJson, FactoryWire.Json)!;
        Assert.DoesNotContain(evidence.KnownLimitations, l => l.Contains("still used the adopted contract", StringComparison.Ordinal));
        Assert.Contains(evidence.KnownLimitations, l => l.Contains("adopt it on the project's Testability panel", StringComparison.Ordinal));
        Assert.Contains("## The browser checks", _host.Workers.Turns.First(t => t.Kind == TurnKind.Review).Brief);
    }

    [Fact]
    public async Task A_missing_draft_is_the_tasks_to_repair_and_still_missing_it_blocks()
    {
        var (_, threadId) = await MakeTestableAsync(draft: null);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        var repair = _host.Workers.Turns.Single(t => t.Kind == TurnKind.Repair);
        Assert.Contains("There is no litos.test.yaml at the repository root yet", repair.Brief);
        Assert.Equal(StopReason.RepairMadeNoDifference, details.LatestRun!.StopReason);
        Assert.Empty(_engine.Gates);
    }

    [Fact]
    public async Task A_sign_in_failure_on_the_draft_is_repaired_and_it_hands_off_only_once_the_gate_passes()
    {
        // Finding 4: W2.7's first handoff went out with the Gate failing at sign-in.
        _engine.GateReports.Enqueue(FailedGate(
            CheckResult.Fail("signin:admin", CheckKind.SignIn, Classification.AuthenticationFailure, "Signed in as admin, but /login was still shown.")));
        var (_, threadId) = await MakeTestableAsync(Draft);

        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var repair = _host.Workers.Turns.Single(t => t.Kind == TurnKind.Repair);
        Assert.Contains("signin:admin", repair.Brief);
        Assert.Equal(2, _engine.Gates.Count);
    }

    [Fact]
    public async Task A_draft_gate_that_keeps_failing_blocks_and_is_never_handed_off()
    {
        var failing = FailedGate(CheckResult.Fail("visit:anonymous:/gone", CheckKind.RouteVisit, Classification.TestDefect, "/gone answered 404."));
        _engine.GateReport = failing;
        var (_, threadId) = await MakeTestableAsync(Draft);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal(StopReason.RepairMadeNoDifference, details.LatestRun!.StopReason);
        Assert.Null(details.LatestHandoff);
    }

    [Fact]
    public async Task Unset_secrets_block_before_the_gate_and_resume_runs_it_once_they_are_set()
    {
        // Finding 7: only a person can set a secret.
        var (projectId, threadId) = await MakeTestableAsync(DraftWithSecret);

        var blocked = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal(StopReason.SecretsNotSet, blocked.LatestRun!.StopReason);
        Assert.Contains(blocked.Messages, m => m.Text.Contains("needs TEST_ADMIN_PASS, which is not set", StringComparison.Ordinal));
        Assert.Empty(_engine.Gates);
        Assert.DoesNotContain(_host.Workers.Turns, t => t.Kind == TurnKind.Repair);

        await SetSecretAsync(projectId, "TEST_ADMIN_PASS");
        await _host.PostAsync($"api/threads/{threadId}/resume", null, System.Net.HttpStatusCode.OK);

        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        Assert.Single(_engine.Gates);
    }

    [Fact]
    public async Task Check_testability_asks_the_thread_for_unset_secrets_once_per_run()
    {
        var (_, threadId) = await MakeTestableAsync(DraftWithSecret);
        await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);
        var checks = _host.App.Services.GetRequiredService<DraftChecks>();
        var active = new Runs.ActiveRun(Guid.NewGuid(), threadId, await _host.AdminIdAsync(), "fake", "fake-model");

        var first = await checks.CheckAsync(active, new CheckTestabilityRequest("s", "1-2"), default);
        await checks.CheckAsync(active, new CheckTestabilityRequest("s", "1-2"), default);

        Assert.Contains("Not set yet: TEST_ADMIN_PASS", first.Text);
        Assert.Contains("Do not work around it", first.Text);
        var details = await _host.ThreadAsync(threadId);
        Assert.Single(details.Messages, m => m.Text.StartsWith("The draft contract needs TEST_ADMIN_PASS, not set yet. Set it on the project's Testability panel", StringComparison.Ordinal));
        Assert.NotEmpty(_engine.Gates);
    }

    [Fact]
    public async Task A_feature_task_on_the_same_project_has_no_tool_and_no_gate()
    {
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);

        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.False(_host.Workers.Workers.First().Launch.TestabilityCheck);
        Assert.Empty(_engine.Gates);
        Assert.StartsWith("# Factory run brief\n", _host.Workers.Turns.First(t => t.Kind == TurnKind.Implement).Brief.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_tool_reads_the_gate_back_in_a_few_lines()
    {
        var report = new ProbeWebReport
        {
            Kind = ReportKind.Gate,
            Outcome = ProbeWebOutcome.Failed,
            Checks =
            [
                CheckResult.Pass("build", CheckKind.Build),
                CheckResult.Fail("start", CheckKind.Start, Classification.ApplicationDefect, "web did not answer on its assigned port 51873, but something new listens on 8787.")
                    with { Evidence = new Evidence { Log = ["Story server on http://127.0.0.1:8787"] } },
            ],
            Lint = new LintSummary { Pages = 1, Interactive = 4, Unnamed = 1, Findings = [new LintFinding("/", null, "button.icon")] },
            Usage = new Usage { WallSeconds = 12 },
        };

        var text = DraftChecks.Describe(report, ["TEST_ADMIN_PASS"]);

        Assert.StartsWith("Gate: Failed (12s, no model calls).", text);
        Assert.Contains("- Failed start [ApplicationDefect]: web did not answer on its assigned port 51873", text);
        Assert.Contains("    | Story server on http://127.0.0.1:8787", text);
        Assert.Contains("1 of 4 controls have no accessible name: button.icon on /", text);
        Assert.Contains("Not set yet: TEST_ADMIN_PASS", text);
    }

    [Fact]
    public void A_draft_that_does_not_run_says_why()
    {
        var folder = Directory.CreateTempSubdirectory("probe-draft-").FullName;
        try
        {
            Assert.Contains("There is no litos.test.yaml", DraftChecks.Read(folder, _engine).Problem);
            File.WriteAllText(Path.Combine(folder, ContractYaml.FileName), "version: 1\napplication:\n  processes: []\n");
            Assert.Contains("at least one process is required", DraftChecks.Read(folder, _engine).Problem);
            File.WriteAllText(Path.Combine(folder, ContractYaml.FileName), Draft);
            Assert.NotNull(DraftChecks.Read(folder, _engine).Contract);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
