using System.Text.Json.Serialization;

namespace Litos.Probe.Web.Core.Contract;

/// <summary>
/// How to build, start, reset, sign into and walk a web app (testability-architecture.md §5).
/// The factory embeds it in verification profile v3; <c>litos.test.yaml</c> is the same model in
/// snake_case. Anything a stack profile defaults may be left out here.
/// </summary>
public sealed record WebAppContract
{
    public int Version { get; init; } = 1;
    public AppDefinition Application { get; init; } = new();
    public TestabilityDefinition Testability { get; init; } = new();
}

/// <summary>The <c>application</c> block: how to bring the app up.</summary>
public sealed record AppDefinition
{
    /// <summary>The stack profile whose defaults fill what is left out, e.g. <c>aspnet-blazor-server</c>.</summary>
    public string? Stack { get; init; }

    /// <summary>Run once before the processes start. Null means the stack profile's default; an empty list means no build.</summary>
    public IReadOnlyList<CommandStep>? Build { get; init; }

    /// <summary>Throwaway containers. Part of the model so profile v3 can carry them; the engine starts them from W2.</summary>
    public IReadOnlyList<ServiceDefinition> Services { get; init; } = [];

    /// <summary>Started in the order declared and stopped in reverse.</summary>
    public IReadOnlyList<ProcessDefinition> Processes { get; init; } = [];

    /// <summary>The name of the process the browser opens.</summary>
    public string BaseUrl { get; init; } = "";

    public ResetDefinition? Reset { get; init; }

    /// <summary>Origins the browser may reach besides the app's own (e.g. a CDN the pages load from).</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}

/// <summary>A command run to completion: an executable and its arguments, never a shell string.</summary>
public sealed record CommandStep
{
    public IReadOnlyList<string> Command { get; init; } = [];

    /// <summary>Relative to the working copy; the working copy itself when null.</summary>
    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public int TimeoutSeconds { get; init; } = 600;
}

public sealed record ProcessDefinition
{
    public string Name { get; init; } = "";

    /// <summary>Empty means the stack profile's default serve command.</summary>
    public IReadOnlyList<string> Command { get; init; } = [];

    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public ProcessReady? Ready { get; init; }
    public int StartTimeoutSeconds { get; init; } = 120;
}

/// <summary>Server readiness. Without it, a process is ready when its port accepts and <c>GET /</c> answers.</summary>
public sealed record ProcessReady
{
    /// <summary>A path such as <c>/health</c> that must answer below 400.</summary>
    public string? Http { get; init; }
}

public sealed record ServiceDefinition
{
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

public enum ResetStrategy
{
    /// <summary>The app keeps no data, so there is nothing to reset. Declared, never assumed.</summary>
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("command")] Command,
    [JsonStringEnumMemberName("endpoint")] Endpoint,
    [JsonStringEnumMemberName("sqlite-snapshot")] SqliteSnapshot,
}

public sealed record ResetDefinition
{
    public ResetStrategy Strategy { get; init; }

    /// <summary><see cref="ResetStrategy.Command"/>: the command to run after start.</summary>
    public IReadOnlyList<string> Command { get; init; } = [];

    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary><see cref="ResetStrategy.Endpoint"/>: e.g. <c>POST /__test/reset</c>, called on the base process.</summary>
    public string? Endpoint { get; init; }

    /// <summary><see cref="ResetStrategy.SqliteSnapshot"/>: the app's database file, relative to the working copy.</summary>
    public string? File { get; init; }

    /// <summary><see cref="ResetStrategy.SqliteSnapshot"/>: the command that writes a seeded <see cref="File"/>, run once before start.</summary>
    public IReadOnlyList<string> Seed { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>The <c>testability</c> block: how to use the app once it is up.</summary>
public sealed record TestabilityDefinition
{
    /// <summary>Page readiness after each navigation. Required only by profiles that need a marker.</summary>
    public PageReady? Ready { get; init; }

    public AuthDefinition Auth { get; init; } = new();

    /// <summary>Named test-only endpoints, e.g. <c>clock: "POST /__test/clock"</c>. Gate step 7 checks each is absent in Production.</summary>
    public IReadOnlyDictionary<string, string> TestEndpoints { get; init; } = new Dictionary<string, string>();

    /// <summary>Signals added to the stack profile's own.</summary>
    public IReadOnlyList<FailOnSignal> FailOn { get; init; } = [];

    public RouteSet Routes { get; init; } = new();

    /// <summary>Whether a <c>console.error</c> fails a page. Some libraries are noisy.</summary>
    public bool ConsoleErrors { get; init; } = true;

    /// <summary>Where an adopted contract came from. Set by the factory on adoption.</summary>
    public ContractOrigin? AdoptedFrom { get; init; }
}

public sealed record PageReady
{
    /// <summary>A selector that must be visible, e.g. <c>[data-testid=app-interactive]</c>.</summary>
    public string? Selector { get; init; }

    /// <summary>A JavaScript expression that must become truthy.</summary>
    public string? Js { get; init; }
}

public enum AuthType
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("form")] Form,
}

public sealed record AuthDefinition
{
    public AuthType Type { get; init; }
    public string? LoginUrl { get; init; }
    public LoginFields? Fields { get; init; }

    /// <summary>The accessible name of the submit button.</summary>
    public string? Submit { get; init; }

    public SuccessCondition? SuccessWhen { get; init; }
    public IReadOnlyDictionary<string, RoleCredentials> Roles { get; init; } = new Dictionary<string, RoleCredentials>();
}

/// <summary>The accessible labels of the two login fields.</summary>
public sealed record LoginFields
{
    public string User { get; init; } = "";
    public string Password { get; init; } = "";
}

/// <summary>When a sign-in counts as done. Every condition given must hold.</summary>
public sealed record SuccessCondition
{
    public string? UrlNotContains { get; init; }
    public string? UrlContains { get; init; }
    public string? SelectorVisible { get; init; }

    public bool IsEmpty => UrlNotContains is null && UrlContains is null && SelectorVisible is null;
}

public sealed record RoleCredentials
{
    public string User { get; init; } = "";
    public string Password { get; init; } = "";
}

/// <summary>One extra failure signal. Exactly one field is set.</summary>
public sealed record FailOnSignal
{
    public string? SelectorVisible { get; init; }
    public string? TextVisible { get; init; }

    /// <summary>A regular expression matched against the app's output lines during a step.</summary>
    public string? LogPattern { get; init; }
}

public sealed record RouteSet
{
    public IReadOnlyList<string> Public { get; init; } = [];

    /// <summary>Per role, the routes that role can open.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Protected { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Per role, the routes that role must not be able to open.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Denied { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

public sealed record ContractOrigin
{
    public string? Commit { get; init; }
    public string? Path { get; init; }
}
