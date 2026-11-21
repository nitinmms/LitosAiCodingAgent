using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Core.Scenarios;
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

    public Task<ProbeWebReport> GateAsync(
        string workingCopy, WebAppContract c, GateScope scope, ProbeWebOptions o, CancellationToken ct, IReadOnlyList<WebScenario>? scenarios = null) =>
        WithScenariosAsync(workingCopy, c, scope, ReportKind.Gate, o, ct, scenarios);

    public Task<ProbeWebReport> SmokeAsync(
        string workingCopy, WebAppContract c, ProbeWebOptions o, CancellationToken ct, IReadOnlyList<WebScenario>? scenarios = null) =>
        WithScenariosAsync(workingCopy, c, GateScope.Smoke, ReportKind.Smoke, o, ct, scenarios);

    /// <summary>
    /// The Gate or the smoke, then the scenarios in the same app start (W3.6): the factory runs a
    /// project's approved scenarios this way on every task and in main's baseline.
    /// </summary>
    private async Task<ProbeWebReport> WithScenariosAsync(
        string workingCopy, WebAppContract c, GateScope scope, ReportKind kind, ProbeWebOptions o, CancellationToken ct, IReadOnlyList<WebScenario>? scenarios)
    {
        if (scenarios is not { Count: > 0 })
            return await RunAsync(workingCopy, c, scope, kind, o, ct);
        var (valid, invalid) = Sort(scenarios, c);
        return Merge(await RunAsync(workingCopy, c, scope, kind, o, ct, valid), invalid);
    }

    public async Task<ProbeWebReport> ScenariosAsync(
        string workingCopy, WebAppContract c, IReadOnlyList<WebScenario> scenarios, ProbeWebOptions o, CancellationToken ct)
    {
        var (valid, invalid) = Sort(scenarios, c);
        if (valid.Count == 0)
        {
            return new ProbeWebReport
            {
                Kind = ReportKind.Scenarios,
                Outcome = invalid.Count > 0 ? ProbeWebOutcome.Failed : ProbeWebOutcome.Inconclusive,
                Contract = new ContractInfo { Source = o.ContractSource, Revision = o.ContractRevision, Stack = c.Application.Stack },
                Checks = invalid,
                Findings = invalid.Count > 0 ? [] : ["No scenarios to run."],
            };
        }

        return Merge(await RunAsync(workingCopy, c, new GateScope(new HashSet<int> { 1, 2 }), ReportKind.Scenarios, o, ct, valid), invalid);
    }

    /// <summary>The scenarios that can run, and a TestDefect check for each one that cannot.</summary>
    private static (List<WebScenario> Valid, List<CheckResult> Invalid) Sort(IReadOnlyList<WebScenario> scenarios, WebAppContract c)
    {
        var invalid = new List<CheckResult>();
        var valid = new List<WebScenario>();
        foreach (var group in scenarios.GroupBy(s => s.Id, StringComparer.Ordinal))
        {
            foreach (var (scenario, index) in group.Select((s, i) => (s, i)))
            {
                var problems = ScenarioValidator.Validate(scenario, c).ToList();
                if (index > 0)
                    problems.Insert(0, $"id: \"{scenario.Id}\" is used by more than one scenario in this run");
                if (problems.Count == 0)
                    valid.Add(scenario);
                else
                    invalid.Add(Invalid(scenario, problems));
            }
        }

        return (valid, invalid);
    }

    private static ProbeWebReport Merge(ProbeWebReport report, IReadOnlyList<CheckResult> invalid)
    {
        if (invalid.Count == 0)
            return report;
        var checks = report.Checks.Concat(invalid).ToList();
        return report with { Checks = checks, Outcome = report.Outcome == ProbeWebOutcome.InvalidContract ? report.Outcome : ReportOutcome.Compute(checks) };
    }

    private static CheckResult Invalid(WebScenario s, IReadOnlyList<string> problems) =>
        Classifier.Fail($"scenario:{s.Id}", CheckKind.Scenario, FailureCause.ScenarioInvalid,
            $"The scenario does not run as written: {string.Join("; ", problems)}.") with
        {
            Role = s.Role,
            Route = s.Start,
            Scenario = new ScenarioInfo(s.Id, s.Revision, s.Title, s.Criterion),
        };

    /// <summary>The contract's problems against its stack profile; empty when it can run.</summary>
    public IReadOnlyList<string> Validate(WebAppContract c)
    {
        var profile = profiles.Find(c.Application.Stack ?? StackProfiles.Generic);
        return profile is null
            ? [$"application.stack: there is no stack profile '{c.Application.Stack}' (known: {string.Join(", ", profiles.All.Select(p => p.Id).Order())})"]
            : ContractValidator.Validate(c, profile, services);
    }

    private Task<ProbeWebReport> RunAsync(
        string workingCopy, WebAppContract c, GateScope scope, ReportKind kind, ProbeWebOptions o, CancellationToken ct, IReadOnlyList<WebScenario>? scenarios = null)
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
        return new GateRun(workingCopy, effective, profile, scope, kind, o, _launcher, services, scenarios).RunAsync(ct);
    }
}
