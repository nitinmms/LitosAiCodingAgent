using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;

namespace Litos.Probe.Web;

/// <summary>Resolves <c>{secret:NAME}</c>. The factory reads project secrets; the CLI reads environment variables.</summary>
public interface ISecretResolver
{
    string? Resolve(string name);
}

public enum ArtifactKind { Screenshot, Trace, Log, Report }

/// <summary>Where screenshots, traces and logs go. Returns the id the report refers to the file by.</summary>
public interface IArtifactSink
{
    Task<string> SaveAsync(ArtifactKind kind, string fileName, Stream content, CancellationToken ct);
}

/// <param name="BaseEnvironment">The host's scrubbed toolchain environment. App processes get it plus the contract's declared variables, added after the scrub.</param>
/// <param name="Timeout">The whole operation's hard limit; when it passes, everything is stopped.</param>
/// <param name="FlakyReruns">How many times a failing visit is retried in a fresh context before it is blamed.</param>
public sealed record ProbeWebOptions(
    ISecretResolver Secrets,
    IArtifactSink Artifacts,
    IReadOnlyDictionary<string, string> BaseEnvironment,
    TimeSpan Timeout,
    bool Headless = true,
    int FlakyReruns = 1)
{
    /// <summary>Recorded in the report: <c>profile</c> for the factory's DB copy, <c>file</c> for a yaml.</summary>
    public string ContractSource { get; init; } = "file";
    public int? ContractRevision { get; init; }

    /// <summary>One line per step as it happens, for a terminal. Never receives secret values.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>Where a run keeps its scratch files (the SQLite snapshot, traces before they are saved). The system temp folder when null.</summary>
    public string? TempDirectory { get; init; }

    /// <summary>How long a page may take to load, and its readiness marker to appear.</summary>
    public TimeSpan PageTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Who starts the service containers (<c>factory</c> or <c>cli</c>), recorded as a label so each sweeps only its own.</summary>
    public string ServiceOwner { get; init; } = "cli";

    /// <summary>The Docker CLI. Tests point it elsewhere to see Docker missing.</summary>
    public string DockerExecutable { get; init; } = "docker";
}

/// <summary>Which of the Gate's 8 steps to run (README §6.1). Steps 1 and 2 always run, because nothing else can without them.</summary>
public sealed record GateScope(IReadOnlySet<int> Steps)
{
    public static GateScope All { get; } = new(new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8 });

    /// <summary>What the factory runs on every task: start, reset, sign-in, routes and access control.</summary>
    public static GateScope Smoke { get; } = new(new HashSet<int> { 1, 2, 3, 4, 5, 6 });

    public bool Includes(int step) => step is 1 or 2 || Steps.Contains(step);

    /// <summary>"1-8", "1,2,4" or "1-4,8".</summary>
    public static GateScope Parse(string text)
    {
        var steps = new HashSet<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length > 2 || !int.TryParse(range[0], out var from) || !int.TryParse(range[^1], out var to) || from > to || from < 1 || to > 8)
                throw new FormatException($"'{part}' is not a step or a range of steps between 1 and 8.");
            for (var s = from; s <= to; s++)
                steps.Add(s);
        }

        return steps.Count == 0 ? throw new FormatException("No steps given.") : new GateScope(steps);
    }
}

/// <summary>The engine's entry points (README §8).</summary>
public interface IProbeWeb
{
    /// <summary>Tries a stack profile's defaults on a repository that has no contract, and reports the real gaps.</summary>
    Task<ProbeWebReport> DryRunAsync(string repoRoot, StackProfile profile, ProbeWebOptions o, CancellationToken ct, string? appDirectory = null);

    Task<ProbeWebReport> GateAsync(string workingCopy, WebAppContract c, GateScope scope, ProbeWebOptions o, CancellationToken ct);

    /// <summary>Gate steps 1–6, as the factory runs them on every task.</summary>
    Task<ProbeWebReport> SmokeAsync(string workingCopy, WebAppContract c, ProbeWebOptions o, CancellationToken ct);
}

/// <summary>The service containers Probe.Web starts, from outside a run (README §10).</summary>
public static class ServiceContainers
{
    /// <summary>Removes every container <paramref name="owner"/>'s crashed runs left behind; returns how many.</summary>
    public static Task<int> SweepAsync(string owner, IReadOnlyDictionary<string, string> environment, CancellationToken ct, string docker = "docker") =>
        Running.DockerServices.SweepAsync(new Processes.ProcessLauncher(), environment, owner, ct, docker);
}
