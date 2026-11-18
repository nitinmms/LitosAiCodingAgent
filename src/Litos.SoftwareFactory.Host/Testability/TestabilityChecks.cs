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
    TestabilityRuntime runtime, IClock clock, IHostApplicationLifetime lifetime, ILogger<TestabilityChecks> logger)
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
            return report with { Status = TestabilityStatus.Adopted, Stack = adopted.Application.Stack ?? report.Stack };
        if (contract is { Valid: true })
            return report with { Status = TestabilityStatus.ContractFound, Gaps = ["litos.test.yaml on the default branch is ready to adopt."] };
        if (detection.Status == DetectionStatus.NotAWebApp)
            return report with { Status = TestabilityStatus.NotAWebApp };
        if (detection.Status == DetectionStatus.StackNotSupported || detection.Best is not { } best || engine.Profile(best.ProfileId) is not { } profile)
            return report with { Status = TestabilityStatus.StackNotSupported };

        Probe.Web.Core.Reports.ProbeWebReport dryRun;
        using (await gate.EnterAsync(null, ct))
            dryRun = await engine.DryRunAsync(root, profile, best.Directory, runtime.OptionsFor(project, runId: null), ct);

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
            DryRun = dryRun,
        };
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
