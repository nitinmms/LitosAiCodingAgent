using Litos.Probe.Web;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Core.Scenarios;
using Litos.Probe.Web.Detection;
using Litos.Probe.Web.Processes;
using Litos.Probe.Web.Profiles;
using Litos.Probe.Web.Running;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Infrastructure.Processes;

namespace Litos.SoftwareFactory.Host.Testability;

/// <summary>
/// What the factory asks of Litos.Probe.Web (README_Litos.Probe.Web.md §2), as one seam: the host
/// tests replace it, so they never start a browser or an app.
/// </summary>
public interface ITestabilityEngine
{
    DetectionResult Detect(string repoRoot);

    StackProfile? Profile(string id);

    IReadOnlyList<string> Validate(WebAppContract contract);

    Task<ProbeWebReport> DryRunAsync(string repoRoot, StackProfile profile, string? appDirectory, ProbeWebOptions options, CancellationToken ct);

    /// <param name="scenarios">A project's approved scenarios, run after the Gate's own checks in the same app start (W3.6).</param>
    Task<ProbeWebReport> GateAsync(string workingCopy, WebAppContract contract, GateScope scope, ProbeWebOptions options, CancellationToken ct,
        IReadOnlyList<WebScenario>? scenarios = null);

    Task<ProbeWebReport> SmokeAsync(string workingCopy, WebAppContract contract, ProbeWebOptions options, CancellationToken ct,
        IReadOnlyList<WebScenario>? scenarios = null);

    /// <summary>Scenarios alone, for trying one on the default branch before approving it.</summary>
    Task<ProbeWebReport> ScenariosAsync(string workingCopy, WebAppContract contract, IReadOnlyList<WebScenario> scenarios, ProbeWebOptions options, CancellationToken ct);
}

/// <summary>The real engine: Probe.Web in this process, with its embedded stack profiles.</summary>
public sealed class ProbeWebEngine(StackProfiles profiles) : ITestabilityEngine
{
    private readonly ProbeWeb _engine = new(profiles);

    public DetectionResult Detect(string repoRoot) => new StackDetector().Detect(repoRoot, profiles.All);

    public StackProfile? Profile(string id) => profiles.Find(id);

    public IReadOnlyList<string> Validate(WebAppContract contract) => _engine.Validate(contract);

    public Task<ProbeWebReport> DryRunAsync(string repoRoot, StackProfile profile, string? appDirectory, ProbeWebOptions options, CancellationToken ct) =>
        _engine.DryRunAsync(repoRoot, profile, options, ct, appDirectory);

    public Task<ProbeWebReport> GateAsync(string workingCopy, WebAppContract contract, GateScope scope, ProbeWebOptions options, CancellationToken ct,
        IReadOnlyList<WebScenario>? scenarios = null) =>
        _engine.GateAsync(workingCopy, contract, scope, options, ct, scenarios);

    public Task<ProbeWebReport> SmokeAsync(string workingCopy, WebAppContract contract, ProbeWebOptions options, CancellationToken ct,
        IReadOnlyList<WebScenario>? scenarios = null) =>
        _engine.SmokeAsync(workingCopy, contract, options, ct, scenarios);

    public Task<ProbeWebReport> ScenariosAsync(string workingCopy, WebAppContract contract, IReadOnlyList<WebScenario> scenarios, ProbeWebOptions options, CancellationToken ct) =>
        _engine.ScenariosAsync(workingCopy, contract, scenarios, options, ct);
}

/// <summary>
/// The options every factory run of Probe.Web gets: the project's own secrets, files into
/// <c>run_artifacts</c>, and the scrubbed toolchain environment (README §8: the contract's
/// variables are added after the scrub, by Probe.Web, for the app's processes only).
/// </summary>
public sealed class TestabilityRuntime(IFactoryStore store, RunArtifactFiles files, IClock clock, FactorySettings settings, FactoryOptions options)
{
    /// <summary>The label the factory's service containers carry, so it sweeps only its own.</summary>
    public const string ServiceOwner = "factory";

    public ProbeWebOptions OptionsFor(Project project, Guid? runId, Action<string>? progress = null) =>
        new(new ProjectSecretResolver(settings, project.Id),
            new FactoryArtifactSink(store, files, clock, project.Id, runId),
            ToolchainEnvironment.Scrub(ToolchainEnvironment.CurrentHostEnvironment()),
            options.TestabilityTimeout)
        {
            ContractSource = "profile",
            ContractRevision = project.ProfileRevision,
            ServiceOwner = ServiceOwner,
            TempDirectory = Path.Combine(options.DataDirectory, "probe-web"),
            Progress = progress,
        };
}

/// <summary>
/// On host start, removes the service containers a crashed factory run left behind
/// (README_Litos.Probe.Web.md §10). Only the factory's own: a developer's CLI runs are left alone.
/// </summary>
public sealed class ServiceContainerSweep(FactoryOptions options, ILogger<ServiceContainerSweep> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.SweepServiceContainersOnStart)
            return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                var removed = await ServiceContainers.SweepAsync(TestabilityRuntime.ServiceOwner,
                    ToolchainEnvironment.Scrub(ToolchainEnvironment.CurrentHostEnvironment()), CancellationToken.None);
                if (removed > 0)
                    logger.LogInformation("Removed {Count} service containers a previous run left behind.", removed);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not sweep leftover service containers.");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
