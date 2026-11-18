using System.Text;
using Litos.Probe.Web;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Reports;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;

namespace Litos.SoftwareFactory.Host.Testability;

/// <summary>
/// A Make-testable run's draft contract (testability-architecture.md §6.3): the <c>litos.test.yaml</c>
/// in its working copy, read and checked by the host, never by the agent. It backs the
/// <c>check_testability</c> tool and the run's own Gate step.
/// </summary>
public sealed class DraftChecks(
    IFactoryStore store, IWorkspaceProvider workspaces, ITestabilityEngine engine, TestabilityRuntime runtime, VerificationGate gate,
    FactorySettings settings)
{
    /// <summary>The draft in the working copy, or why there is none to run.</summary>
    public static (WebAppContract? Contract, string? Problem) Read(string workingCopy, ITestabilityEngine engine)
    {
        var path = Path.Combine(workingCopy, ContractYaml.FileName);
        if (!File.Exists(path))
            return (null, $"There is no {ContractYaml.FileName} at the repository root yet. Write it first: the brief shows its shape.");
        try
        {
            var contract = ContractYaml.Load(path);
            var problems = engine.Validate(contract);
            return problems.Count == 0
                ? (contract, null)
                : (null, $"{ContractYaml.FileName} does not run as written:\n" + string.Join("\n", problems.Select(p => $"- {p}")));
        }
        catch (ContractFormatException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>check_testability: the Gate on the draft, as the agent reads it.</summary>
    public async Task<CheckTestabilityResponse> CheckAsync(ActiveRun active, CheckTestabilityRequest request, CancellationToken ct)
    {
        if (await store.GetThreadAsync(active.ThreadId, ct) is not { } details || await store.GetProjectAsync(details.Thread.ProjectId, ct) is not { } project)
            return new CheckTestabilityResponse(true, "The run's project could not be found.");

        GateScope scope;
        try
        {
            scope = string.IsNullOrWhiteSpace(request.Steps) ? GateScope.All : GateScope.Parse(request.Steps);
        }
        catch (FormatException ex)
        {
            return new CheckTestabilityResponse(true, $"steps: {ex.Message}");
        }

        var workingCopy = workspaces.For(project).Path;
        var (contract, problem) = Read(workingCopy, engine);
        if (contract is null)
            return new CheckTestabilityResponse(true, problem!);

        ProbeWebReport report;
        using (await gate.EnterAsync(null, ct))
            report = await engine.GateAsync(workingCopy, contract, scope, runtime.OptionsFor(project, active.RunId), ct);

        return new CheckTestabilityResponse(false, Describe(report, TestabilityApi.MissingSecrets(project.Id, contract, settings)));
    }

    /// <summary>The Gate's result in a few lines: each check, what failed and the server's error lines.</summary>
    internal static string Describe(ProbeWebReport report, IReadOnlyList<string> missingSecrets)
    {
        var text = new StringBuilder();
        text.AppendLine($"Gate: {report.Outcome} ({report.Usage.WallSeconds:0}s, no model calls).");
        foreach (var check in report.Checks)
        {
            text.Append($"- {check.Status} {check.Id}");
            if (check.Classification is { } c && check.Status != CheckStatus.Passed)
                text.Append($" [{c}]");
            if (check.Message is { } message && (check.Status != CheckStatus.Passed || check.Classification == Classification.Flaky))
                text.Append($": {message}");
            text.AppendLine();
            foreach (var line in check.Failed ? check.Evidence.Log.Take(8) : [])
                text.AppendLine($"    | {line}");
        }

        if (report.Lint is { Unnamed: > 0 } lint)
            text.AppendLine($"Lint: {lint.Unnamed} of {lint.Interactive} controls have no accessible name: {string.Join(", ", lint.Findings.Take(5).Select(f => $"{f.Element} on {f.Route}"))}.");
        foreach (var finding in report.Findings)
            text.AppendLine($"Note: {finding}");
        if (missingSecrets.Count > 0)
            text.AppendLine($"Not set yet: {string.Join(", ", missingSecrets)}. Sign-in cannot pass until a person sets them on the project's Testability panel; name them in knownLimitations.");
        return text.ToString().TrimEnd();
    }

    /// <summary>The run's own Gate step: the whole Gate on the draft, with no baseline (there is none yet).</summary>
    public async Task<UiTestResult> GateDraftAsync(Project project, Guid runId, string workingCopy, Func<string, Task> note, CancellationToken ct)
    {
        var (contract, problem) = Read(workingCopy, engine);
        if (contract is null)
        {
            // The draft is the task's own work: a missing or broken one is repaired, not blocked.
            return new UiTestResult
            {
                Defects = [new UiDefect("litos.test.yaml", problem!, [])],
                Evidence = new UiTestEvidence { Status = UiTestingStatus.Failed, Problem = problem },
            };
        }

        ProbeWebReport report;
        using (await gate.EnterAsync(() => note("Waiting for another task's verification to finish before the Gate."), ct))
        {
            await note("Running the Testability Gate on the draft contract: build, start, reset, sign in, routes, access control, Production, lint.");
            report = await engine.GateAsync(workingCopy, contract, GateScope.All, runtime.OptionsFor(project, runId), ct);
        }

        // There is no baseline: everything the Gate finds is the retrofit's, and a start-up that
        // fails is the retrofit's to fix too, unless the browser or Docker itself is missing.
        var result = RunUiTests.Interpret(report, baseline: null, baselineHealthy: true, project.ProfileRevision, []);
        var missing = TestabilityApi.MissingSecrets(project.Id, contract, settings);
        if (missing.Count > 0)
            result = result with { Notes = [.. result.Notes, $"Set {string.Join(", ", missing)} on the project's Testability panel, then adopt the contract after merging."] };
        await note($"Testability Gate: {report.Outcome}.");
        return result;
    }
}
