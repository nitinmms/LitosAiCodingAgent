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
    /// <param name="task">The task's own scenarios (W3.7): its approved spec's, and the ones its work turn drafted.</param>
    public async Task<UiTestResult> RunAsync(Project project, Guid runId, string workingCopy, WebAppContract contract, Func<string, Task> note, CancellationToken ct,
        TaskScenarios? task = null)
    {
        var baseline = await BaselineAsync(project, ct);
        task ??= TaskScenarios.None;
        var projectScenarios = await ProjectScenarios.ApprovedAsync(store, project.Id, ct);
        // An id is run once: a project's approved scenario first, then the spec's, then a draft.
        var taken = projectScenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var spec = task.Spec.Where(s => taken.Add(s.Id)).ToList();
        var drafted = task.Drafted.Where(s => taken.Add(s.Id)).ToList();
        IReadOnlyList<Probe.Web.Core.Scenarios.WebScenario> scenarios = [.. projectScenarios, .. spec, .. drafted];
        var sources = projectScenarios.Select(s => (s.Id, ScenarioSources.Project))
            .Concat(spec.Select(s => (s.Id, ScenarioSources.Spec)))
            .Concat(drafted.Select(s => (s.Id, ScenarioSources.Drafted)))
            .ToDictionary(p => $"scenario:{p.Id}", p => p.Item2, StringComparer.Ordinal);
        ProbeWebReport report;
        using (await gate.EnterAsync(() => note("Waiting for another task's verification to finish before the browser checks."), ct))
        {
            await note("Starting the app for the browser checks: start, reset, sign in, routes and access control" + ScenarioCounts(projectScenarios.Count, spec.Count, drafted.Count));
            report = await engine.SmokeAsync(workingCopy, contract, runtime.OptionsFor(project, runId), ct, scenarios);
        }

        var evidenceOnly = drafted.Select(s => $"scenario:{s.Id}").ToHashSet(StringComparer.Ordinal);
        var result = Interpret(report, baseline.Report, baseline.Healthy, project.ProfileRevision, RunCommands(contract), evidenceOnly: evidenceOnly);
        UiCheck Tag(UiCheck c) => sources.TryGetValue(c.Id, out var source) ? c with { Source = source } : c;
        result = result with
        {
            Evidence = result.Evidence with
            {
                Checks = [.. result.Evidence.Checks.Select(Tag)],
                PreExisting = [.. result.Evidence.PreExisting.Select(Tag)],
                TaskScenarios =
                [
                    .. spec.Select(s => new TaskScenario(ScenarioSources.Spec, s.ToJson())),
                    .. drafted.Select(s => new TaskScenario(ScenarioSources.Drafted, s.ToJson())),
                ],
            },
        };
        await note(Summary(result));
        return result;
    }

    /// <summary>
    /// What the smoke means for the run. Only a new application defect is repaired. A failure to
    /// bring the app up is the environment's, unless the default branch comes up fine: then the
    /// task broke its build or start-up, and that is repaired too (§7.2).
    /// </summary>
    /// <param name="draft">
    /// A Make-testable task's own draft contract (finding 4): the contract and the test users are
    /// the task's work, so a test or sign-in failure is repaired too, never handed off.
    /// </param>
    /// <param name="evidenceOnly">Check ids of scenarios the task's work turn drafted (W3.7): not approved, so a failure is disclosed, never repaired.</param>
    internal static UiTestResult Interpret(
        ProbeWebReport report, ProbeWebReport? baseline, bool baselineHealthy, int revision, IReadOnlyList<string> runCommands, bool draft = false,
        IReadOnlySet<string>? evidenceOnly = null)
    {
        evidenceOnly ??= new HashSet<string>();
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

        bool Blamed(CheckResult c) => !evidenceOnly.Contains(c.Id)
            && (draft ? c.Classification != Classification.EnvironmentFailure : c.Classification == Classification.ApplicationDefect);
        var defects = fresh.Where(Blamed).Concat(theTasks)
            .Select(c => new UiDefect(c.Id, c.Message ?? c.Id, c.Evidence.Log))
            .ToList();

        var notes = new List<string>();
        notes.AddRange(fresh.Where(c => !draft && !evidenceOnly.Contains(c.Id) && c.Classification is Classification.TestDefect or Classification.AuthenticationFailure)
            .Select(c => $"Browser check {c.Id} needs a person, not a code change: {c.Message}"));
        notes.AddRange(fresh.Where(c => evidenceOnly.Contains(c.Id))
            .Select(c => $"The agent's drafted scenario {c.Id["scenario:".Length..]} failed ({c.Classification}): {c.Message} It is not approved, so it was not sent back for repair."));
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

    private static string ScenarioCounts(int project, int spec, int drafted)
    {
        var parts = new List<string>();
        if (project > 0)
            parts.Add(project == 1 ? "the project's approved scenario" : $"the project's {project} approved scenarios");
        if (spec > 0)
            parts.Add(spec == 1 ? "the specification's scenario" : $"the specification's {spec} scenarios");
        if (drafted > 0)
            parts.Add(drafted == 1 ? "the agent's drafted scenario" : $"the agent's {drafted} drafted scenarios");
        return parts.Count == 0 ? "." : $", then {string.Join(" and ", parts)}.";
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
