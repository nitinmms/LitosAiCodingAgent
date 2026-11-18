using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Reports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Host.Runs;

namespace Litos.SoftwareFactory.Host.Testability;

/// <summary>
/// The UI test step of a task (testability-architecture.md §6.4): the browser smoke (Gate steps
/// 1–6) on the task's working copy under the adopted contract, compared with the Gate baseline on
/// the default branch, turned into what the orchestrator acts on. No model is called.
/// </summary>
public sealed class RunUiTests(IFactoryStore store, ITestabilityEngine engine, TestabilityRuntime runtime, VerificationGate gate)
{
    /// <param name="note">Posts a line to the task's thread.</param>
    public async Task<UiTestResult> RunAsync(Project project, Guid runId, string workingCopy, WebAppContract contract, Func<string, Task> note, CancellationToken ct)
    {
        var baseline = await BaselineAsync(project, ct);
        ProbeWebReport report;
        using (await gate.EnterAsync(() => note("Waiting for another task's verification to finish before the browser checks."), ct))
        {
            await note("Starting the app for the browser checks: start, reset, sign in, routes and access control.");
            report = await engine.SmokeAsync(workingCopy, contract, runtime.OptionsFor(project, runId), ct);
        }

        var result = Interpret(report, baseline.Report, baseline.Healthy, project.ProfileRevision, RunCommands(contract));
        await note(Summary(result));
        return result;
    }

    /// <summary>
    /// What the smoke means for the run. Only a new application defect is repaired. A failure to
    /// bring the app up is the environment's, unless the default branch comes up fine: then the
    /// task broke its build or start-up, and that is repaired too (§7.2).
    /// </summary>
    internal static UiTestResult Interpret(ProbeWebReport report, ProbeWebReport? baseline, bool baselineHealthy, int revision, IReadOnlyList<string> runCommands)
    {
        var compared = SmokeComparison.Compare(report, baseline);
        var fresh = compared.NewFailures;

        bool IsBringUp(CheckResult c) => c.Kind is CheckKind.Build or CheckKind.Start or CheckKind.Reset;
        var environment = fresh.Where(c => c.Classification == Classification.EnvironmentFailure).ToList();
        var theTasks = baselineHealthy ? environment.Where(c => IsBringUp(c) && c.Id is not ("browser" or "timeout")).ToList() : [];
        var environmentProblem = environment.Except(theTasks).FirstOrDefault() is { } blocked
            ? blocked.Message ?? blocked.Id
            : report.Outcome == ProbeWebOutcome.InvalidContract
                ? "The adopted contract no longer validates: " + string.Join(" ", report.Findings)
                : null;

        var defects = fresh.Where(c => c.Classification == Classification.ApplicationDefect).Concat(theTasks)
            .Select(c => new UiDefect(c.Id, c.Message ?? c.Id, c.Evidence.Log))
            .ToList();

        var notes = new List<string>();
        notes.AddRange(fresh.Where(c => c.Classification is Classification.TestDefect or Classification.AuthenticationFailure)
            .Select(c => $"Browser check {c.Id} needs a person, not a code change: {c.Message}"));
        notes.AddRange(compared.PreExisting.Select(c => $"Browser check {c.Id} already fails on the default branch, so it is not blamed on this task."));
        notes.AddRange(compared.Report.Checks.Where(c => c.Classification == Classification.Flaky)
            .Select(c => $"Browser check {c.Id} was flaky: it failed once and passed on its rerun."));

        var status = environmentProblem is not null ? UiTestingStatus.Inconclusive
            : defects.Count > 0 || fresh.Count > 0 ? UiTestingStatus.Failed
            : UiTestingStatus.Passed;
        return new UiTestResult
        {
            Defects = environmentProblem is null ? defects : [],
            Notes = notes,
            EnvironmentProblem = environmentProblem,
            Evidence = new UiTestEvidence
            {
                Status = status,
                ContractRevision = revision,
                Commit = report.Commit,
                Checks = [.. compared.Report.Checks.Select(UiTestEvidence.CheckOf)],
                PreExisting = [.. compared.PreExisting.Select(UiTestEvidence.CheckOf)],
                Lint = report.Lint,
                Emails = report.Emails,
                RunCommands = runCommands,
                Problem = environmentProblem,
                WallSeconds = report.Usage.WallSeconds,
            },
        };
    }

    /// <summary>The Gate baseline on the default branch at the project's current revision, and whether it passed.</summary>
    private async Task<(ProbeWebReport? Report, bool Healthy)> BaselineAsync(Project project, CancellationToken ct)
    {
        if (await store.LatestTestabilityCheckAsync(project.Id, ct) is not { } check || check.ProfileRevision != project.ProfileRevision)
            return (null, false);
        var report = TestabilityReport.FromJson(check.ReportJson);
        return report.Run is { Kind: ReportKind.Gate } run
            ? (run, check.Status == TestabilityStatus.TestReady)
            : (null, false);
    }

    /// <summary>How the factory built and started the app, as the "Try it yourself" card shows it.</summary>
    private IReadOnlyList<string> RunCommands(WebAppContract contract)
    {
        var profile = contract.Application.Stack is { } stack ? engine.Profile(stack) : null;
        var effective = profile is null ? contract : ContractDefaults.Apply(contract, profile);
        return
        [
            .. (effective.Application.Build ?? []).Select(b => string.Join(' ', b.Command)),
            .. effective.Application.Processes.Select(p => string.Join(' ', p.Command)),
        ];
    }

    private static string Summary(UiTestResult result)
    {
        var checks = result.Evidence.Checks;
        var passed = checks.Count(c => c.Status == "Passed");
        var failed = checks.Count(c => c.Status == "Failed");
        return result.EnvironmentProblem is { } problem
            ? $"Browser checks could not run: {problem}"
            : $"Browser checks: {passed} passed, {failed} failed" +
              (result.Defects.Count > 0 ? $" ({string.Join(", ", result.Defects.Take(3).Select(d => d.Id))})" : "") +
              (result.Evidence.PreExisting.Count > 0 ? $", {result.Evidence.PreExisting.Count} already failing on the default branch" : "") +
              ". Screenshots are on the handoff.";
    }
}
