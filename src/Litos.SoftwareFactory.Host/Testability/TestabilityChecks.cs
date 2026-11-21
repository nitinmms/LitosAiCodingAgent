using Litos.Probe.Web;
using Litos.Probe.Web.Detection;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Runs;

namespace Litos.SoftwareFactory.Host.Testability;

/// <summary>
/// The testability check (testability-architecture.md §6.1): detect the stack, read the default
/// branch's <c>litos.test.yaml</c>, and, when there is no contract to go on, dry-run the stack's
/// defaults to find the real gaps. It runs on the project's reading copy at the default branch's
/// head, at registration, after a profile change and on "Check again", like the baseline, and
/// takes a verification permit for the dry run, which builds and runs the project's code. No
/// model is called.
/// </summary>
public sealed class TestabilityChecks(
    IFactoryStore store, FactoryOptions options, ReadingCopies readingCopies, VerificationGate gate, ITestabilityEngine engine,
    TestabilityRuntime runtime, IVerifier verifier, IClock clock, IHostApplicationLifetime lifetime, ILogger<TestabilityChecks> logger)
{
    private readonly Lock _lock = new();

    /// <summary>The projects being checked, each with whether another check was asked for meanwhile.</summary>
    private readonly Dictionary<Guid, bool> _checking = [];

    /// <summary>The task of the latest check, for tests to wait on.</summary>
    internal Task? Latest { get; private set; }

    /// <summary>At registration and after a profile change, when the host is set to check then.</summary>
    public void Start(Guid projectId)
    {
        if (options.TestabilityOnRegistration)
            CheckNow(projectId);
    }

    /// <summary>"Check again": always, or once more after the check under way.</summary>
    public void CheckNow(Guid projectId)
    {
        lock (_lock)
        {
            if (_checking.ContainsKey(projectId))
            {
                _checking[projectId] = true;
                return;
            }

            _checking[projectId] = false;
            Latest = Task.Run(() => CheckUntilCurrentAsync(projectId, lifetime.ApplicationStopping));
        }
    }

    public bool IsChecking(Guid projectId)
    {
        lock (_lock)
            return _checking.ContainsKey(projectId);
    }

    public IReadOnlySet<Guid> Checking()
    {
        lock (_lock)
            return _checking.Keys.ToHashSet();
    }

    private async Task CheckUntilCurrentAsync(Guid projectId, CancellationToken ct)
    {
        while (true)
        {
            if (await store.GetProjectAsync(projectId, ct) is { } project)
            {
                TestabilityReport report;
                try
                {
                    report = await CheckAsync(project, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    lock (_lock)
                        _checking.Remove(projectId);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "The testability check of project {ProjectId} could not run.", projectId);
                    report = new TestabilityReport { Status = TestabilityStatus.CouldNotCheck, Problem = ex.Message };
                }

                await SaveAsync(project, report, ct);
            }

            lock (_lock)
            {
                if (!_checking[projectId] || ct.IsCancellationRequested)
                {
                    _checking.Remove(projectId);
                    return;
                }

                _checking[projectId] = false;
            }
        }
    }

    private async Task SaveAsync(Project project, TestabilityReport report, CancellationToken ct)
    {
        try
        {
            await store.SaveTestabilityCheckAsync(new TestabilityCheck
            {
                ProjectId = project.Id,
                ProfileRevision = project.ProfileRevision,
                Commit = report.Commit,
                Status = report.Status,
                ReportJson = report.ToJson(),
                CreatedAt = clock.UtcNow,
            }, ct);
        }
        catch (StoreNotFoundException)
        {
            // The project was removed while it was being checked.
        }
    }

    /// <summary>
    /// Installs the project's dependencies in the reading copy with the verification profile's own
    /// prepare and restore commands (npm ci, dotnet restore, a venv), as a task's Verify does before
    /// its browser step: the contract's build assumes them (npm run build fails without
    /// node_modules). Run under the verification permit, since it runs the project's install
    /// scripts. Answers why it failed, or null.
    /// </summary>
    private async Task<string?> RestoreAsync(Project project, string root, string? commit, CancellationToken ct)
    {
        if (VerificationProfile.Parse(project.VerificationProfileJson).RestoreOnly() is not { } restore)
            return null;

        var directory = Path.Combine(options.DataDirectory, "projects", project.Id.ToString("N"),
            $"testability-restore-{(commit is { Length: > 0 } c ? c[..Math.Min(12, c.Length)] : "head")}");
        var outcome = await verifier.VerifyAsync(new VerificationRequest(root, restore, ChangedFiles: null, directory)
        {
            TempDirectory = Path.Combine(directory, "tmp"),
            Variables = ProfileVariables.For(options.DataDirectory, project.Id),
        }, ct);
        if (outcome.Build is not (BuildStatus.Failed or BuildStatus.Unavailable))
            return null;

        var why = outcome.Problems.Count > 0 ? string.Join(" ", outcome.Problems) : $"Build {outcome.Build}.";
        return $"The project's dependencies could not be installed on the default branch, so the app was not built: {why}";
    }

    internal async Task<TestabilityReport> CheckAsync(Project project, CancellationToken ct)
    {
        using var held = await readingCopies.HoldAsync(project.Id, ct);
        var workspace = readingCopies.For(project);
        await workspace.EnsureClonedAsync(ct);
        await workspace.FetchAsync(ct);
        await workspace.CheckoutForReadingAsync(project.DefaultBranch, ct);
        var commit = (await workspace.GetStatusAsync(ct)).HeadCommit;
        var root = workspace.Path;

        var detection = engine.Detect(root);
        var candidates = detection.Candidates.Select(c => new StackCandidate(c.ProfileId, c.Tier, c.Directory, c.Evidence)).ToList();
        var contract = ReadContract(root, commit);
        var report = new TestabilityReport { Commit = commit, Candidates = candidates, Contract = contract, Stack = detection.Best?.ProfileId };

        // An adopted contract is the project's: the Gate, not a dry run, says whether it holds.
        if (VerificationProfile.Parse(project.VerificationProfileJson).WebApp() is { } adopted)
            return await GateBaselineAsync(project, root, adopted, report, contract, ct);
        if (contract is { Valid: true })
            return report with { Status = TestabilityStatus.ContractFound, Gaps = ["litos.test.yaml on the default branch is ready to adopt."] };
        if (detection.Status == DetectionStatus.NotAWebApp)
            return report with { Status = TestabilityStatus.NotAWebApp };
        if (detection.Status == DetectionStatus.StackNotSupported || detection.Best is not { } best || engine.Profile(best.ProfileId) is not { } profile)
            return report with { Status = TestabilityStatus.StackNotSupported };

        Probe.Web.Core.Reports.ProbeWebReport dryRun;
        using (await gate.EnterAsync(null, ct))
        {
            if (await RestoreAsync(project, root, report.Commit, ct) is { } restoreFailed)
                return report with { Status = TestabilityStatus.CouldNotCheck, Problem = restoreFailed };
            dryRun = await engine.DryRunAsync(root, profile, best.Directory, runtime.OptionsFor(project, runId: null), ct);
        }

        var gaps = new List<string>();
        if (contract is { Valid: false })
            gaps.Add($"litos.test.yaml on the default branch does not run as written: {string.Join(" ", contract.Problems)}");
        if (detection.Status == DetectionStatus.Several)
            gaps.Add($"Several web apps: {string.Join(", ", candidates.Select(c => $"{c.Profile} in {(c.Directory.Length == 0 ? "." : c.Directory)}"))}. The check tried the first; the contract picks one.");
        gaps.AddRange(dryRun.Findings);

        // A dry run that could not start the browser or the services says nothing about the app.
        var environment = dryRun.Checks.FirstOrDefault(c => c is { Failed: true, Id: "browser" or "services" or "timeout" });
        return report with
        {
            Status = environment is null ? TestabilityStatus.Gaps : TestabilityStatus.CouldNotCheck,
            Problem = environment?.Message,
            Gaps = gaps,
            Run = dryRun,
        };
    }

    /// <summary>
    /// The Gate baseline on the default branch (testability-architecture.md §6.2): all 8 steps of
    /// the adopted contract, which marks the project test-ready or broken on main. A task's smoke
    /// is compared with this report, so a check that already fails here is not blamed on a task.
    /// </summary>
    private async Task<TestabilityReport> GateBaselineAsync(Project project, string root, Probe.Web.Core.Contract.WebAppContract adopted, TestabilityReport report,
        ContractOnMain? onMain, CancellationToken ct)
    {
        var stack = adopted.Application.Stack ?? report.Stack;
        // The project's approved scenarios run here too, so a task is not blamed for one main already fails (W3.6).
        var scenarios = await ProjectScenarios.ApprovedAsync(store, project.Id, ct);
        Probe.Web.Core.Reports.ProbeWebReport gateRun;
        using (await gate.EnterAsync(null, ct))
        {
            if (await RestoreAsync(project, root, report.Commit, ct) is { } restoreFailed)
                return report with { Status = TestabilityStatus.CouldNotCheck, Problem = restoreFailed };
            gateRun = await engine.GateAsync(root, adopted, GateScope.All, runtime.OptionsFor(project, runId: null), ct, scenarios);
        }

        var gaps = new List<string>();
        if (onMain is { Valid: true } && ContractAdoption.Differs(root, adopted))
            gaps.Add($"litos.test.yaml on the default branch differs from the adopted contract (revision {project.ProfileRevision}): adopt it to use it.");
        gaps.AddRange(gateRun.Checks.Where(c => c.Failed).Select(c => $"{c.Id}: {c.Message}"));
        gaps.AddRange(gateRun.Findings);

        // A failing scenario is a gap, not a broken main: the Gate's own checks say whether the app is test-ready.
        var outcome = gateRun.Outcome == Probe.Web.Core.Reports.ProbeWebOutcome.InvalidContract
            ? gateRun.Outcome
            : Probe.Web.Core.Reports.ReportOutcome.Compute(gateRun.Checks.Where(c => c.Kind != Probe.Web.Core.Reports.CheckKind.Scenario));
        var status = outcome switch
        {
            Probe.Web.Core.Reports.ProbeWebOutcome.Passed => TestabilityStatus.TestReady,
            Probe.Web.Core.Reports.ProbeWebOutcome.EnvironmentFailure or Probe.Web.Core.Reports.ProbeWebOutcome.Inconclusive
                or Probe.Web.Core.Reports.ProbeWebOutcome.InvalidContract => TestabilityStatus.CouldNotCheck,
            _ => TestabilityStatus.BrokenOnMain,
        };
        var problem = status == TestabilityStatus.CouldNotCheck
            ? gateRun.Checks.FirstOrDefault(c => c.Failed)?.Message ?? gateRun.Findings.FirstOrDefault() ?? "The Gate could not run."
            : null;
        return report with { Status = status, Stack = stack, Gaps = gaps, Run = gateRun, Problem = problem };
    }

    /// <summary>
    /// "Run on main" (W3.6): one scenario on the default branch's head, alone, so its author can
    /// try a draft before approving it. Same reading copy, install and permit as the check.
    /// </summary>
    public async Task<Probe.Web.Core.Reports.ProbeWebReport> TryScenarioAsync(
        Project project, Probe.Web.Core.Contract.WebAppContract adopted, Probe.Web.Core.Scenarios.WebScenario scenario, CancellationToken ct)
    {
        using var held = await readingCopies.HoldAsync(project.Id, ct);
        var workspace = readingCopies.For(project);
        await workspace.EnsureClonedAsync(ct);
        await workspace.FetchAsync(ct);
        await workspace.CheckoutForReadingAsync(project.DefaultBranch, ct);
        var commit = (await workspace.GetStatusAsync(ct)).HeadCommit;

        using (await gate.EnterAsync(null, ct))
        {
            if (await RestoreAsync(project, workspace.Path, commit, ct) is { } restoreFailed)
            {
                return new Probe.Web.Core.Reports.ProbeWebReport
                {
                    Kind = Probe.Web.Core.Reports.ReportKind.Scenarios,
                    Outcome = Probe.Web.Core.Reports.ProbeWebOutcome.EnvironmentFailure,
                    Commit = commit,
                    Findings = [restoreFailed],
                };
            }

            return await engine.ScenariosAsync(workspace.Path, adopted, [scenario], runtime.OptionsFor(project, runId: null), ct);
        }
    }

    /// <summary>The default branch's litos.test.yaml, parsed and validated; null when there is none.</summary>
    private ContractOnMain? ReadContract(string root, string commit)
    {
        var path = Path.Combine(root, ContractYaml.FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return new ContractOnMain(ContractYaml.FileName, commit, engine.Validate(ContractYaml.Load(path)));
        }
        catch (ContractFormatException ex)
        {
            return new ContractOnMain(ContractYaml.FileName, commit, [ex.Message]);
        }
    }
}
