using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Core.Profiles;

/// <summary>
/// Everything that differs between frameworks, as data (testability-architecture.md §6.6). The
/// engine has no stack-specific code: adding a stack means adding one of these files.
/// </summary>
public sealed record StackProfile
{
    public string Id { get; init; } = "";

    /// <summary>The factory's verification preset this builds on: dotnet, node, python, java, go.</summary>
    public string Toolchain { get; init; } = "";

    /// <summary>1 = built with a test app and live-checked, 2 = shipped as data, 3 = generic.</summary>
    public int Tier { get; init; } = 2;

    public string Description { get; init; } = "";
    public DetectRules Detect { get; init; } = new();
    public AppDefaults Defaults { get; init; } = new();

    /// <summary>How the framework is told its port. <c>{port}</c> stands for the process's own port.</summary>
    public PortTemplate PortTemplate { get; init; } = new();

    /// <summary>Set for every app process, under the contract's own variables.</summary>
    public IReadOnlyDictionary<string, string> TestEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>What Gate step 7 sets, over everything else, to start the app as Production.</summary>
    public IReadOnlyDictionary<string, string> ProductionEnvironment { get; init; } = new Dictionary<string, string>();

    public Readiness Readiness { get; init; } = new();

    /// <summary>Page-side failure signals on top of the universal ones.</summary>
    public IReadOnlyList<FailOnSignal> ErrorSignals { get; init; } = [];

    /// <summary>A regex for error lines in the app's standard output.</summary>
    public string? LogErrorPattern { get; init; }

    /// <summary>Whether any standard-error line during a step is an error. Off for stacks that log everything there.</summary>
    public bool StderrIsError { get; init; } = true;

    /// <summary>Ports the framework binds when it ignores the one it was given: Gate step 1 says which one it chose.</summary>
    public IReadOnlyList<int> DefaultPorts { get; init; } = [];

    /// <summary>
    /// Framework scripts whose <c>console.error</c> is known noise in a headless browser, so the
    /// app need not be changed to hide it (README_Litos.Probe.Web.md finding 9). The report counts
    /// what was ignored.
    /// </summary>
    public IReadOnlyList<IgnoredConsole> IgnoreConsole { get; init; } = [];

    public IReadOnlyList<string> Quirks { get; init; } = [];
    public string? RetrofitSkill { get; init; }

    public static StackProfile Parse(string json) =>
        JsonSerializer.Deserialize<StackProfile>(json, ProbeWebJson.Options) ?? throw new JsonException("Empty stack profile");
}

/// <summary>Console errors from a script whose URL path ends with <see cref="Source"/> (<c>*</c> matches within a name, for a fingerprint), and why they are ignored.</summary>
public sealed record IgnoredConsole(string Source, string Reason);

/// <summary>Markers matched against the repository. <see cref="All"/> must all match, one of <see cref="Any"/> (when given), and none of <see cref="None"/>.</summary>
public sealed record DetectRules
{
    public IReadOnlyList<DetectMarker> All { get; init; } = [];
    public IReadOnlyList<DetectMarker> Any { get; init; } = [];
    public IReadOnlyList<DetectMarker> None { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => All.Count == 0 && Any.Count == 0;
}

/// <summary>A file glob, optionally with a regular expression the file's text must match.</summary>
public sealed record DetectMarker
{
    public string Glob { get; init; } = "";
    public string? Pattern { get; init; }
}

public sealed record AppDefaults
{
    public IReadOnlyList<CommandStep> Build { get; init; } = [];

    /// <summary>The serve command. Its name is the default process name.</summary>
    public IReadOnlyList<ProcessDefinition> Processes { get; init; } = [];
}

public sealed record PortTemplate
{
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

public enum ReadinessMode
{
    /// <summary>The page is ready at its load event.</summary>
    [JsonStringEnumMemberName("load")] Load,

    /// <summary>A websocket-driven UI: ready only when its marker shows, never at "network idle".</summary>
    [JsonStringEnumMemberName("needsMarker")] NeedsMarker,
}

public sealed record Readiness
{
    public ReadinessMode Mode { get; init; }
    public string? DefaultSelector { get; init; }
}
