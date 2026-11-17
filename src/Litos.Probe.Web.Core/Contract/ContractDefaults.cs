using Litos.Probe.Web.Core.Profiles;

namespace Litos.Probe.Web.Core.Contract;

/// <summary>
/// Fills what a contract leaves out from its stack profile (README §4: build and serve commands,
/// port injection, readiness, error signals). The engine only ever runs the result, so a stored
/// contract stays as short as its author wrote it.
/// </summary>
public static class ContractDefaults
{
    public static WebAppContract Apply(WebAppContract contract, StackProfile profile)
    {
        var app = contract.Application;
        var processes = app.Processes.Select((p, i) => ApplyProcess(p, i, profile)).ToList();

        var testability = contract.Testability;
        var ready = testability.Ready;
        if (ready is null && profile.Readiness is { Mode: ReadinessMode.NeedsMarker, DefaultSelector: { } selector })
            ready = new PageReady { Selector = selector };

        return contract with
        {
            Application = app with
            {
                Stack = app.Stack ?? profile.Id,
                Build = app.Build ?? profile.Defaults.Build,
                Processes = processes,
            },
            Testability = testability with
            {
                Ready = ready,
                FailOn = [.. profile.ErrorSignals, .. testability.FailOn],
            },
        };
    }

    /// <summary>
    /// The contract a dry run tries when the repository has none: the profile's own build and
    /// serve commands, no login, no reset, and only <c>/</c> (testability-architecture.md §6.1).
    /// </summary>
    public static WebAppContract FromProfile(StackProfile profile, string? workingDirectory = null)
    {
        var processes = profile.Defaults.Processes.Count > 0
            ? profile.Defaults.Processes
            : [new ProcessDefinition { Name = "web" }];
        var build = profile.Defaults.Build.Select(b => b with { WorkingDirectory = b.WorkingDirectory ?? workingDirectory }).ToList();

        return Apply(new WebAppContract
        {
            Application = new AppDefinition
            {
                Stack = profile.Id,
                Build = build,
                Processes = [.. processes.Select(p => p with { WorkingDirectory = p.WorkingDirectory ?? workingDirectory })],
                BaseUrl = processes[0].Name,
                Reset = new ResetDefinition { Strategy = ResetStrategy.None },
            },
            Testability = new TestabilityDefinition { Routes = new RouteSet { Public = ["/"] } },
        }, profile);
    }

    private static ProcessDefinition ApplyProcess(ProcessDefinition process, int index, StackProfile profile)
    {
        var fallback = profile.Defaults.Processes.FirstOrDefault(d => d.Name == process.Name)
            ?? (index == 0 ? profile.Defaults.Processes.FirstOrDefault() : null);

        var command = process.Command.Count > 0 ? process.Command : fallback?.Command ?? [];
        var environment = new Dictionary<string, string>(profile.TestEnvironment);
        foreach (var (name, value) in fallback?.Environment ?? new Dictionary<string, string>())
            environment[name] = value;
        foreach (var (name, value) in process.Environment)
            environment[name] = value;

        // The profile's port template, unless the contract already says where the port goes.
        var own = $"{{port:{process.Name}}}";
        var placed = command.Any(a => a.Contains(own, StringComparison.Ordinal))
            || environment.Values.Any(v => v.Contains(own, StringComparison.Ordinal));
        if (!placed)
        {
            foreach (var (name, value) in profile.PortTemplate.Environment)
                environment.TryAdd(name, value.Replace("{port}", own, StringComparison.Ordinal));
            if (command.Count > 0)
                command = [.. command, .. profile.PortTemplate.Arguments.Select(a => a.Replace("{port}", own, StringComparison.Ordinal))];
        }

        return process with
        {
            Command = command,
            WorkingDirectory = process.WorkingDirectory ?? fallback?.WorkingDirectory,
            Environment = environment,
            Ready = process.Ready ?? fallback?.Ready,
        };
    }
}
