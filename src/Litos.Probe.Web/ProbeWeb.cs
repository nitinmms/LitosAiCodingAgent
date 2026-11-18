using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Processes;
using Litos.Probe.Web.Profiles;
using Litos.Probe.Web.Running;

namespace Litos.Probe.Web;

/// <summary>
/// The engine (README §8). Every entry point validates first, so a bad contract costs nothing and
/// comes back as <see cref="ProbeWebOutcome.InvalidContract"/> with its errors as findings.
/// </summary>
/// <param name="services">The service images contracts may use; the embedded list when null.</param>
public sealed class ProbeWeb(StackProfiles profiles, IProcessLauncher? launcher = null, ServiceCatalog? services = null) : IProbeWeb
{
    private readonly IProcessLauncher _launcher = launcher ?? new ProcessLauncher();

    public ProbeWeb() : this(StackProfiles.Embedded())
    {
    }

    public Task<ProbeWebReport> DryRunAsync(string repoRoot, StackProfile profile, ProbeWebOptions o, CancellationToken ct, string? appDirectory = null)
    {
        if (profile.Tier == StackProfiles.NotSupportedTier || profile.Defaults.Processes.Count == 0)
        {
            return Task.FromResult(new ProbeWebReport
            {
                Kind = ReportKind.DryRun,
                Outcome = ProbeWebOutcome.Inconclusive,
                Contract = new ContractInfo { Source = "defaults", Stack = profile.Id },
                Findings = [$"The {profile.Id} profile has no default serve command to try: write a contract with the commands by hand."],
            });
        }

        var contract = ContractDefaults.FromProfile(profile, string.IsNullOrEmpty(appDirectory) ? null : appDirectory);
        return new GateRun(repoRoot, contract, profile, new GateScope(new HashSet<int> { 1, 2, 4, 8 }), ReportKind.DryRun, o, _launcher, services).RunAsync(ct);
    }

    public Task<ProbeWebReport> GateAsync(string workingCopy, WebAppContract c, GateScope scope, ProbeWebOptions o, CancellationToken ct) =>
        RunAsync(workingCopy, c, scope, ReportKind.Gate, o, ct);

    public Task<ProbeWebReport> SmokeAsync(string workingCopy, WebAppContract c, ProbeWebOptions o, CancellationToken ct) =>
        RunAsync(workingCopy, c, GateScope.Smoke, ReportKind.Smoke, o, ct);

    /// <summary>The contract's problems against its stack profile; empty when it can run.</summary>
    public IReadOnlyList<string> Validate(WebAppContract c)
    {
        var profile = profiles.Find(c.Application.Stack ?? StackProfiles.Generic);
        return profile is null
            ? [$"application.stack: there is no stack profile '{c.Application.Stack}' (known: {string.Join(", ", profiles.All.Select(p => p.Id).Order())})"]
            : ContractValidator.Validate(c, profile, services);
    }

    private Task<ProbeWebReport> RunAsync(string workingCopy, WebAppContract c, GateScope scope, ReportKind kind, ProbeWebOptions o, CancellationToken ct)
    {
        var errors = Validate(c);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ProbeWebReport
            {
                Kind = kind,
                Outcome = ProbeWebOutcome.InvalidContract,
                Contract = new ContractInfo { Source = o.ContractSource, Revision = o.ContractRevision, Stack = c.Application.Stack },
                Findings = errors,
            });
        }

        var profile = profiles.ForContract(c.Application.Stack);
        var effective = ContractDefaults.Apply(c, profile);
        return new GateRun(workingCopy, effective, profile, scope, kind, o, _launcher, services).RunAsync(ct);
    }
}
