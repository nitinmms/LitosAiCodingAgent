using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.Probe.Web.Core.Reports;

public enum ReportKind
{
    [JsonStringEnumMemberName("gate")] Gate,
    [JsonStringEnumMemberName("smoke")] Smoke,
    [JsonStringEnumMemberName("scenarios")] Scenarios,
    [JsonStringEnumMemberName("dry-run")] DryRun,
}

public enum ProbeWebOutcome
{
    Passed,
    Failed,
    EnvironmentFailure,
    Inconclusive,

    /// <summary>The contract did not validate, so nothing was started.</summary>
    InvalidContract,
}

public enum CheckKind { Build, Start, Reset, SignIn, RouteVisit, LoggedOutRedirect, Denied, ProductionTestEndpoints, SelectorLint, Scenario, Services }

public enum CheckStatus { Passed, Failed, Skipped }

/// <summary>What a failure means for the factory (README §7): who acts on it, and how.</summary>
public enum Classification
{
    /// <summary>The app is wrong: the factory starts a repair turn.</summary>
    ApplicationDefect,

    /// <summary>A scenario or contract entry is wrong: a person fixes it, with no code change.</summary>
    TestDefect,

    /// <summary>The app could not be built, started or reset: Blocked, with diagnostics.</summary>
    EnvironmentFailure,

    /// <summary>Test credentials are wrong or a secret is missing: a person fixes it.</summary>
    AuthenticationFailure,

    /// <summary>Failed, then passed on its automatic rerun in a fresh context. Reported, not repaired.</summary>
    Flaky,
}

public sealed record ProbeWebReport
{
    public ReportKind Kind { get; init; }
    public ProbeWebOutcome Outcome { get; init; }
    public string? Commit { get; init; }
    public ContractInfo? Contract { get; init; }
    public AppInfo? App { get; init; }
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];
    public LintSummary? Lint { get; init; }

    /// <summary>What the run found that a person should act on, in plain words: the dry run's gaps, a contract's errors.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>The emails the app sent during the run, caught by a Mailpit service.</summary>
    public IReadOnlyList<CapturedEmail> Emails { get; init; } = [];

    public Usage Usage { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, ProbeWebJson.Indented);

    public static ProbeWebReport FromJson(string json) =>
        JsonSerializer.Deserialize<ProbeWebReport>(json, ProbeWebJson.Options) ?? throw new JsonException("Empty report");
}

public sealed record ContractInfo
{
    /// <summary><c>profile</c> (the factory's DB copy), <c>file</c> (a yaml the CLI read) or <c>defaults</c> (a dry run).</summary>
    public string Source { get; init; } = "";
    public int? Revision { get; init; }
    public string? Stack { get; init; }
}

public sealed record AppInfo
{
    public string BaseUrl { get; init; } = "";
    public double StartSeconds { get; init; }
    public IReadOnlyList<ProcessInfo> Processes { get; init; } = [];
    public IReadOnlyList<ServiceInfo> Services { get; init; } = [];
}

public sealed record ProcessInfo(string Name, int Port);

/// <param name="Ports">Host ports by the image's port names, e.g. <c>port</c>, or <c>smtp</c> and <c>http</c>.</param>
public sealed record ServiceInfo(string Name, string Image, IReadOnlyDictionary<string, int> Ports);

public sealed record CheckResult
{
    /// <summary>Stable across runs, so a smoke check can be matched to its baseline: <c>visit:patient:/my/appointments</c>.</summary>
    public string Id { get; init; } = "";
    public CheckKind Kind { get; init; }
    public string? Role { get; init; }
    public string? Route { get; init; }
    public CheckStatus Status { get; init; }
    public Classification? Classification { get; init; }
    public string? Message { get; init; }
    public Evidence Evidence { get; init; } = new();
    public long DurationMs { get; init; }

    /// <summary>Set by <see cref="SmokeComparison"/>: it also fails on the default branch, so it is disclosed, not blamed.</summary>
    public bool PreExisting { get; init; }

    public static CheckResult Pass(string id, CheckKind kind) => new() { Id = id, Kind = kind, Status = CheckStatus.Passed };

    public static CheckResult Fail(string id, CheckKind kind, Classification classification, string message) =>
        new() { Id = id, Kind = kind, Status = CheckStatus.Failed, Classification = classification, Message = message };

    public static CheckResult Skip(string id, CheckKind kind, string reason) =>
        new() { Id = id, Kind = kind, Status = CheckStatus.Skipped, Message = reason };

    [JsonIgnore]
    public bool Failed => Status == CheckStatus.Failed;
}

public sealed record Evidence
{
    /// <summary>Artifact ids from the sink.</summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];
    public string? Trace { get; init; }
    public int ConsoleErrors { get; init; }

    /// <summary>The page-side signals that fired: page errors, failed requests, error markers.</summary>
    public IReadOnlyList<string> Signals { get; init; } = [];

    /// <summary>The app's output lines that counted as errors during the check.</summary>
    public IReadOnlyList<string> Log { get; init; } = [];
}

/// <param name="Artifact">The raw message, as an <c>.eml</c> artifact id.</param>
public sealed record CapturedEmail(string From, IReadOnlyList<string> To, string Subject, DateTimeOffset? Sent, string? Artifact);

public sealed record LintSummary
{
    public int Pages { get; init; }
    public int Interactive { get; init; }
    public int Unnamed { get; init; }
    public IReadOnlyList<LintFinding> Findings { get; init; } = [];
}

/// <summary>An interactive element with no accessible name and no <c>data-testid</c>.</summary>
public sealed record LintFinding(string Route, string? Role, string Element);

public sealed record Usage
{
    /// <summary>Always 0 in Probe.Web: it is the part of Probe that uses no model.</summary>
    public int ModelCalls { get; init; }
    public double WallSeconds { get; init; }
}
