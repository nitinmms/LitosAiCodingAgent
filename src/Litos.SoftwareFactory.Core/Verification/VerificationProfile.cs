using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.SoftwareFactory.Core.Verification;

/// <summary>
/// A project's verification profile (ReadMe_LitosSoftwareFactory_V1.md §10.1): the commands to
/// run and the report formats they produce. It is data — the factory knows nothing about any
/// language or build tool, so supporting a new stack means writing a profile.
/// </summary>
public sealed record VerificationProfile(
    int ProfileVersion,
    IReadOnlyList<VerificationStep> Steps,
    CoverageRule? Coverage = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    int MaximumRepairCycles = 2)
{
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static VerificationProfile Parse(string json) =>
        JsonSerializer.Deserialize<VerificationProfile>(json, JsonOptions)
        ?? throw new JsonException("The verification profile is empty.");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Every reason this profile cannot be run as written. Empty means it is usable. Checked at
    /// registration, so a broken profile is rejected then rather than discovered mid-run.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (ProfileVersion != CurrentVersion)
            problems.Add($"profileVersion must be {CurrentVersion}, but is {ProfileVersion}.");
        if (Steps is null || Steps.Count == 0)
            problems.Add("A profile needs at least one step.");
        if (Coverage is { ChangedLinesThresholdPercent: < 0 or > 100 })
            problems.Add("coverage.changedLinesThresholdPercent must be between 0 and 100.");
        if (MaximumRepairCycles < 0)
            problems.Add("maximumRepairCycles cannot be negative.");

        foreach (var step in Steps ?? [])
        {
            var name = string.IsNullOrWhiteSpace(step.Name) ? "(unnamed step)" : step.Name;
            if (string.IsNullOrWhiteSpace(step.Name))
                problems.Add("Every step needs a name.");
            if (step.WorkingDirectory is { } directory && (Path.IsPathRooted(directory) || directory.Split('/', '\\').Contains("..")))
                problems.Add($"Step '{name}': workingDirectory must stay inside the repository.");

            // Every command is optional except unit tests.
            if (step.UnitTests is null || step.UnitTests.Count == 0)
                problems.Add($"Step '{name}' has no unit-test command.");

            foreach (var (label, command) in step.Commands())
            {
                if (string.IsNullOrWhiteSpace(command.Executable))
                    problems.Add($"Step '{name}': the {label} command has no executable.");
                if (command.TimeoutSeconds <= 0)
                    problems.Add($"Step '{name}': the {label} command's timeoutSeconds must be positive.");
            }

            foreach (var test in step.UnitTests ?? [])
            {
                if (test.TestReport is null || string.IsNullOrWhiteSpace(test.TestReport.Path))
                    problems.Add($"Step '{name}': a unit-test command must declare a testReport, because counts come only from reports.");
                foreach (var report in new[] { test.TestReport?.Path, test.CoverageReport?.Path })
                    if (report is not null && (Path.IsPathRooted(report) || report.Split('/', '\\').Contains("..")))
                        problems.Add($"Step '{name}': report path '{report}' must stay inside the step's working directory.");
            }
        }

        return problems;
    }
}

/// <summary>One part of a repository — a multi-stack repository lists several, each with its
/// own subdirectory, commands and reports.</summary>
public sealed record VerificationStep(
    string Name,
    string? WorkingDirectory = null,
    CommandSpec? Restore = null,
    CommandSpec? Build = null,
    IReadOnlyList<UnitTestCommand>? UnitTests = null)
{
    /// <summary>Every command in this step, in run order, with a label for messages.</summary>
    public IEnumerable<(string Label, CommandSpec Command)> Commands()
    {
        if (Restore is not null)
            yield return ("restore", Restore);
        if (Build is not null)
            yield return ("build", Build);
        foreach (var test in UnitTests ?? [])
            yield return ("unitTests", test);
    }
}

/// <summary>A command as an argument array — never a concatenated shell string.</summary>
public record CommandSpec(string Executable, IReadOnlyList<string> Arguments, int TimeoutSeconds = 600)
{
    /// <summary>For logs and the handoff. Not for execution.</summary>
    public string Display() => Arguments.Count == 0 ? Executable : $"{Executable} {string.Join(' ', Arguments)}";
}

public sealed record UnitTestCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds = 300,
    TestReportSpec? TestReport = null,
    CoverageReportSpec? CoverageReport = null) : CommandSpec(Executable, Arguments, TimeoutSeconds);

public enum TestReportFormat
{
    Junit,
    Trx,
}

public enum CoverageReportFormat
{
    Cobertura,
    Lcov,
}

/// <summary>Path is a glob relative to the step's working directory.</summary>
public sealed record TestReportSpec(TestReportFormat Format, string Path);

/// <summary>Path is a glob relative to the step's working directory.</summary>
public sealed record CoverageReportSpec(CoverageReportFormat Format, string Path);

public sealed record CoverageRule(double ChangedLinesThresholdPercent);
