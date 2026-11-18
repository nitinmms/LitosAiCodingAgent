using System.Text.Json;
using Litos.Probe.Web.Core.Reports;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Core.Testability;

/// <summary>Where a project stands on browser testing (testability-architecture.md §6.1).</summary>
public enum TestabilityStatus
{
    NotChecked,
    Checking,

    /// <summary>No stack profile's markers matched: nothing to test in a browser.</summary>
    NotAWebApp,

    /// <summary>A web framework with no factory toolchain yet (Rails, Laravel, Phoenix).</summary>
    StackNotSupported,

    /// <summary>The dry run of the stack's defaults found what to fix: a Make-testable task's work.</summary>
    Gaps,

    /// <summary>The default branch has a <c>litos.test.yaml</c> no one has adopted yet.</summary>
    ContractFound,

    /// <summary>The profile carries an adopted contract.</summary>
    Adopted,

    /// <summary>The check itself could not run: the repository could not be read, or the browser is missing.</summary>
    CouldNotCheck,
}

/// <summary>One web app the detector found, with the files that said so.</summary>
public sealed record StackCandidate(string Profile, int Tier, string Directory, IReadOnlyList<string> Evidence);

/// <summary>The <c>litos.test.yaml</c> on the default branch, and whether it would run as written.</summary>
public sealed record ContractOnMain(string Path, string Commit, IReadOnlyList<string> Problems)
{
    public bool Valid => Problems.Count == 0;
}

/// <summary>
/// The result of a testability check: detection, the contract on the default branch, and the dry
/// run's findings. Advice only: just the Gate makes a project test-ready (§6.1). Stored per check,
/// so the Projects page shows the latest.
/// </summary>
public sealed record TestabilityReport
{
    public TestabilityStatus Status { get; init; }
    public string? Commit { get; init; }

    /// <summary>The stack profile the check used, when one was detected or adopted.</summary>
    public string? Stack { get; init; }

    public IReadOnlyList<StackCandidate> Candidates { get; init; } = [];

    /// <summary>What to fix, in plain words: the dry run's findings and failed checks.</summary>
    public IReadOnlyList<string> Gaps { get; init; } = [];

    public ContractOnMain? Contract { get; init; }

    /// <summary>The dry run, with its screenshots as run-artifact ids; null when none ran.</summary>
    public ProbeWebReport? DryRun { get; init; }

    /// <summary>Why the check could not run, for <see cref="TestabilityStatus.CouldNotCheck"/>.</summary>
    public string? Problem { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, FactoryWire.Json);

    public static TestabilityReport FromJson(string json) =>
        JsonSerializer.Deserialize<TestabilityReport>(json, FactoryWire.Json) ?? new TestabilityReport();
}
