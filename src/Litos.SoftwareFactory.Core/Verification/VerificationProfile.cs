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
                    if (report is not null && !StaysInside(report))
                        problems.Add($"Step '{name}': report path '{report}' must stay inside the step's working directory.");
            }

            foreach (var (label, command) in step.Commands())
            {
                if (command.StandardOutput is { } output && (string.IsNullOrWhiteSpace(output) || !StaysInside(output) || output.IndexOfAny(['*', '?']) >= 0))
                    problems.Add($"Step '{name}': the {label} command's standardOutput must be a file path inside the step's working directory.");
            }
        }

        return problems;
    }

    private static bool StaysInside(string path) => !Path.IsPathRooted(path) && !path.Split('/', '\\').Contains("..");

    /// <summary>Whether any command names the project's virtual environment, which the host then keeps for it.</summary>
    public bool UsesVirtualEnvironment() =>
        Steps.SelectMany(s => s.Commands()).Any(c => ProfileVariables.Mentions(c.Command.Executable) || c.Command.Arguments.Any(ProfileVariables.Mentions))
        || (Environment?.Values.Any(ProfileVariables.Mentions) ?? false);
}

/// <summary>
/// What a profile may write in its commands for a place only the host knows (m3-architecture.md §8):
/// <c>{venv}</c>, the project's Python virtual environment in the factory's data folder, and
/// <c>{venvBin}</c>, the folder of its programs, which is <c>Scripts</c> on Windows and <c>bin</c>
/// elsewhere, so one profile works on both.
/// </summary>
public static class ProfileVariables
{
    public const string Venv = "{venv}";
    public const string VenvBin = "{venvBin}";

    /// <summary>The values for one project, whose virtual environment is <c>&lt;data&gt;/venvs/&lt;projectId&gt;</c>.</summary>
    public static IReadOnlyDictionary<string, string> For(string dataDirectory, Guid projectId)
    {
        var venv = VirtualEnvironment(dataDirectory, projectId);
        return new Dictionary<string, string> { [Venv] = venv, [VenvBin] = BinOf(venv) };
    }

    public static string VirtualEnvironment(string dataDirectory, Guid projectId) =>
        Path.Combine(Path.GetFullPath(dataDirectory), "venvs", projectId.ToString("N"));

    /// <summary>Where a virtual environment keeps its programs on this platform.</summary>
    public static string BinOf(string venv) => Path.Combine(venv, OperatingSystem.IsWindows() ? "Scripts" : "bin");

    public static bool Mentions(string? text) => text is not null && text.Contains("{venv", StringComparison.Ordinal);

    /// <summary>The text with each variable replaced; one with no value is left as written.</summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || !text.Contains('{'))
            return text;
        // {venvBin} first: {venv} is a prefix of it.
        foreach (var name in values.Keys.OrderByDescending(k => k.Length))
            text = text.Replace(name, values[name], StringComparison.Ordinal);
        return text;
    }
}

/// <summary>One part of a repository — a multi-stack repository lists several, each with its
/// own subdirectory, commands and reports.</summary>
/// <param name="Prepare">Commands run before restore, in order, such as creating a virtual
/// environment; one that fails stops the step as a failed restore or build would.</param>
public sealed record VerificationStep(
    string Name,
    string? WorkingDirectory = null,
    CommandSpec? Restore = null,
    CommandSpec? Build = null,
    IReadOnlyList<UnitTestCommand>? UnitTests = null,
    IReadOnlyList<CommandSpec>? Prepare = null)
{
    /// <summary>Every command in this step, in run order, with a label for messages.</summary>
    public IEnumerable<(string Label, CommandSpec Command)> Commands()
    {
        foreach (var command in Prepare ?? [])
            yield return ("prepare", command);
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
    /// <summary>
    /// A file, relative to the step's working directory, that the command's standard output is
    /// written to: for a tool that prints its report rather than writing it, such as
    /// <c>go test -json</c>. The log still gets all of it.
    /// </summary>
    public string? StandardOutput { get; init; }

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

    /// <summary>The JSON lines <c>go test -json</c> prints.</summary>
    GoTest,
}

public enum CoverageReportFormat
{
    Cobertura,
    Lcov,

    /// <summary>JaCoCo's XML report, as Maven's and Gradle's JaCoCo plugins write it.</summary>
    Jacoco,

    /// <summary>The profile <c>go test -coverprofile</c> writes.</summary>
    GoCover,
}

/// <summary>Path is a glob relative to the step's working directory.</summary>
public sealed record TestReportSpec(TestReportFormat Format, string Path);

/// <summary>Path is a glob relative to the step's working directory.</summary>
public sealed record CoverageReportSpec(CoverageReportFormat Format, string Path);

public sealed record CoverageRule(double ChangedLinesThresholdPercent);
