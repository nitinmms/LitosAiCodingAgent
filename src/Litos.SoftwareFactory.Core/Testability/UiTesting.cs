using Litos.Probe.Web.Core.Reports;

namespace Litos.SoftwareFactory.Core.Testability;

/// <summary>How a task's browser checks came out, for the handoff (testability-architecture.md §6.5).</summary>
public enum UiTestingStatus
{
    /// <summary>The project has no adopted contract: application testing is the tester's.</summary>
    NotSetUp,

    Passed,
    Failed,

    /// <summary>The app or the browser could not be brought up: nothing was learned about the change.</summary>
    Inconclusive,
}

/// <summary>
/// One browser check, as the handoff shows it: its screenshots and trace are run-artifact ids. A
/// scenario's check also carries the scenario (revision, criterion) and its steps (W3.6).
/// </summary>
public sealed record UiCheck(
    string Id,
    string Kind,
    string? Role,
    string? Route,
    string Status,
    string? Classification,
    string? Message,
    IReadOnlyList<string> Screenshots,
    string? Trace,
    IReadOnlyList<string> Log,
    ScenarioInfo? Scenario = null,
    IReadOnlyList<StepResult>? Steps = null);

/// <summary>
/// The browser evidence a task's handoff carries (HandoffEvidence.UiTesting): what was checked,
/// on which commit and contract revision, each check with its screenshots, what already failed on
/// the default branch, the lint summary, the emails the app sent, and the commands the factory ran,
/// which the "Try it yourself" card shows as run by the factory.
/// </summary>
public sealed record UiTestEvidence
{
    public UiTestingStatus Status { get; init; }
    public int? ContractRevision { get; init; }
    public string? Commit { get; init; }
    public IReadOnlyList<UiCheck> Checks { get; init; } = [];

    /// <summary>Checks that fail on the default branch too: disclosed, not blamed on the task.</summary>
    public IReadOnlyList<UiCheck> PreExisting { get; init; } = [];

    public LintSummary? Lint { get; init; }
    public IReadOnlyList<CapturedEmail> Emails { get; init; } = [];

    /// <summary>How the factory built and started the app, from the adopted contract.</summary>
    public IReadOnlyList<string> RunCommands { get; init; } = [];

    /// <summary>Why the checks could not run, for <see cref="UiTestingStatus.Inconclusive"/>.</summary>
    public string? Problem { get; init; }

    public double WallSeconds { get; init; }

    public static UiTestEvidence NotSetUp { get; } = new() { Status = UiTestingStatus.NotSetUp };

    /// <summary>The handoff's view of one Probe.Web check.</summary>
    public static UiCheck CheckOf(CheckResult c) => new(
        c.Id, c.Kind.ToString(), c.Role, c.Route, c.Status.ToString(), c.Classification?.ToString(), c.Message,
        c.Evidence.Screenshots, c.Evidence.Trace, c.Evidence.Log, c.Scenario, c.Steps);
}

/// <summary>A failure the task's change caused, which a repair turn fixes in the application.</summary>
public sealed record UiDefect(string Id, string Message, IReadOnlyList<string> Log);

/// <summary>
/// What the UI test step reports to the orchestrator: the application defects to repair (new ones
/// only, after the baseline comparison), what a person should know (test or credential defects,
/// pre-existing and flaky checks), or why it could not run at all.
/// </summary>
public sealed record UiTestResult
{
    public IReadOnlyList<UiDefect> Defects { get; init; } = [];

    /// <summary>For the handoff's limitations: these need a person, not a code change.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Set when the app, its services or the browser could not be brought up: the run is Blocked.</summary>
    public string? EnvironmentProblem { get; init; }

    /// <summary>
    /// A Make-testable draft's <c>{secret:}</c> names nobody has set (README_Litos.Probe.Web.md
    /// finding 7): the Gate did not run, and the run is Blocked until a person sets them.
    /// </summary>
    public IReadOnlyList<string> MissingSecrets { get; init; } = [];

    public UiTestEvidence Evidence { get; init; } = new();
}
