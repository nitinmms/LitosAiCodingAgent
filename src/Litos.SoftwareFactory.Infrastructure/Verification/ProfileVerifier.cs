using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Processes;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Litos.SoftwareFactory.Infrastructure.Verification;

/// <summary>
/// Runs a verification profile and reads its reports (ReadMe_LitosSoftwareFactory_V1.md §10).
/// It knows nothing about any stack: it runs the commands the profile names and reads the report
/// formats the profile declares.
///
/// Counts come only from structured reports. A test command that exits non-zero with no report
/// is a failure, never a pass, and a report left over from an earlier run can never count,
/// because every declared report is deleted before its step runs.
///
/// The commands build and run code the agent wrote, so they get the toolchain allowlist and the
/// profile's own variables — never the host's environment, which holds its keys and credentials.
/// </summary>
/// <param name="hostEnvironment">The host's environment, to take the allowlisted part from;
/// supplied by tests.</param>
public sealed class ProfileVerifier(
    IProcessRunner? processRunner = null, Func<IReadOnlyDictionary<string, string>>? hostEnvironment = null) : IVerifier
{
    private readonly IProcessRunner _processRunner = processRunner ?? new ProcessRunner();
    private readonly Func<IReadOnlyDictionary<string, string>> _hostEnvironment =
        hostEnvironment ?? ToolchainEnvironment.CurrentHostEnvironment;

    public async Task<VerificationOutcome> VerifyAsync(VerificationRequest request, CancellationToken ct)
    {
        var profile = request.Profile;
        var run = new RunRecord();

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in ToolchainEnvironment.Scrub(_hostEnvironment()))
            environment[name] = value;
        foreach (var (name, value) in profile.Environment ?? new Dictionary<string, string>())
            environment[name] = value;
        foreach (var (name, value) in ToolchainEnvironment.TempDirectory(request.TempDirectory))
            environment[name] = value;

        // CI=true always, set last so a profile cannot turn it off: several test runners (Vitest
        // and Jest among them) otherwise start in watch mode and would hang until the timeout.
        environment["CI"] = "true";

        foreach (var step in profile.Steps)
            await RunStepAsync(request, step, environment, run, ct);

        return new VerificationOutcome(
            run.BuildStatus(profile),
            run.UnitTestStatus(),
            run.MeasureCoverage(request, out var changedLines),
            run.Tests,
            changedLines,
            run.Commands,
            run.Problems);
    }

    private async Task RunStepAsync(
        VerificationRequest request, VerificationStep step, IReadOnlyDictionary<string, string?> environment, RunRecord run, CancellationToken ct)
    {
        var directory = Path.GetFullPath(Path.Combine(request.WorkingCopy, step.WorkingDirectory ?? ""));
        if (!Directory.Exists(directory))
        {
            run.BuildUnavailable = true;
            run.Problems.Add($"Step '{step.Name}': working directory '{step.WorkingDirectory}' does not exist.");
            return;
        }

        foreach (var test in step.UnitTests ?? [])
        {
            DeleteMatches(directory, test.TestReport?.Path);
            DeleteMatches(directory, test.CoverageReport?.Path);
        }

        foreach (var (label, command) in new[] { ("restore", step.Restore), ("build", step.Build) })
        {
            if (command is null)
                continue;

            var result = await RunCommandAsync(request, step, label, command, directory, environment, run, ct);
            if (!result.Started)
            {
                run.BuildUnavailable = true;
                return;
            }

            if (!result.Succeeded)
            {
                run.BuildFailed = true;
                run.Problems.Add(result.TimedOut
                    ? $"Step '{step.Name}': {label} timed out after {command.TimeoutSeconds} seconds."
                    : $"Step '{step.Name}': {label} failed with exit code {result.ExitCode}.");
                return; // nothing built, so this step's tests are not run
            }
        }

        foreach (var test in step.UnitTests ?? [])
        {
            var result = await RunCommandAsync(request, step, "unitTests", test, directory, environment, run, ct);
            run.TestCommandsRun++;
            if (!result.Started)
            {
                run.TestsUnavailable = true;
                continue;
            }

            var cases = ReadTestReports(step, test, directory, run);
            run.Tests.AddRange(cases);
            ReadCoverageReports(request, step, test, directory, run);

            if (result.TimedOut)
            {
                // An interrupted test run is incomplete: neither pass nor fail evidence (§16).
                run.TestsFailed = true;
                run.Problems.Add($"Step '{step.Name}': unit tests timed out after {test.TimeoutSeconds} seconds; the run is incomplete.");
            }
            else if (cases.Any(c => c.Outcome == TestOutcome.Failed))
            {
                run.TestsFailed = true;
            }
            else if (result.ExitCode != 0)
            {
                run.TestsFailed = true;
                run.Problems.Add(cases.Count == 0
                    ? $"Step '{step.Name}': unit tests failed with exit code {result.ExitCode} (no report)."
                    : $"Step '{step.Name}': unit tests failed with exit code {result.ExitCode} although the report shows no failed test.");
            }
            else if (cases.Count == 0)
            {
                run.Problems.Add($"Step '{step.Name}': the unit-test command produced no test results.");
            }
        }
    }

    private async Task<ProcessResult> RunCommandAsync(
        VerificationRequest request, VerificationStep step, string label, CommandSpec command, string directory,
        IReadOnlyDictionary<string, string?> environment, RunRecord run, CancellationToken ct)
    {
        var logPath = Path.Combine(request.LogDirectory, $"{run.Commands.Count + 1:00}-{SafeName(step.Name)}-{label}.log");
        var result = await _processRunner.RunAsync(
            new ProcessRequest(command.Executable, command.Arguments, directory)
            {
                Timeout = TimeSpan.FromSeconds(command.TimeoutSeconds),
                Environment = environment,
                ReplaceEnvironment = true,
                LogPath = logPath,
            },
            ct);

        if (!result.Started)
            run.Problems.Add($"Step '{step.Name}': {result.StandardError.Trim()}");

        run.Commands.Add(new CommandRun(
            step.Name, label, command.Display(), result.ExitCode, result.Duration, result.TimedOut, result.Started ? logPath : null));
        return result;
    }

    private static List<TestCaseResult> ReadTestReports(VerificationStep step, UnitTestCommand test, string directory, RunRecord run)
    {
        var cases = new List<TestCaseResult>();
        if (test.TestReport is not { } spec)
            return cases;

        foreach (var file in Matches(directory, spec.Path))
        {
            try
            {
                var text = File.ReadAllText(file);
                cases.AddRange(spec.Format == TestReportFormat.Trx ? TrxReportParser.Parse(text) : JUnitReportParser.Parse(text));
            }
            catch (Exception ex) when (ex is ReportFormatException or IOException)
            {
                run.TestsFailed = true;
                run.Problems.Add($"Step '{step.Name}': test report '{Path.GetFileName(file)}' could not be read: {ex.Message}");
            }
        }

        return cases;
    }

    private static void ReadCoverageReports(VerificationRequest request, VerificationStep step, UnitTestCommand test, string directory, RunRecord run)
    {
        if (test.CoverageReport is not { } spec)
            return;

        run.CoverageDeclared = true;
        foreach (var file in Matches(directory, spec.Path))
        {
            try
            {
                var text = File.ReadAllText(file);
                var report = spec.Format == CoverageReportFormat.Lcov ? LcovParser.Parse(text) : CoberturaParser.Parse(text);
                run.Coverage.AddRange(ChangedLineCoverageCalculator.Normalize(report, request.WorkingCopy, directory));
                run.CoverageReportsRead++;
            }
            catch (Exception ex) when (ex is ReportFormatException or IOException)
            {
                run.Problems.Add($"Step '{step.Name}': coverage report '{Path.GetFileName(file)}' could not be read: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> Matches(string directory, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return [];

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern.Replace('\\', '/'));
        return matcher.GetResultsInFullPath(directory).Order(StringComparer.OrdinalIgnoreCase);
    }

    private static void DeleteMatches(string directory, string? pattern)
    {
        foreach (var file in Matches(directory, pattern).ToList())
            File.Delete(file);
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    /// <summary>What one verification run has established so far.</summary>
    private sealed class RunRecord
    {
        public List<TestCaseResult> Tests { get; } = [];

        public List<CommandRun> Commands { get; } = [];

        public List<string> Problems { get; } = [];

        public List<CoverageFile> Coverage { get; } = [];

        public bool BuildFailed { get; set; }

        public bool BuildUnavailable { get; set; }

        public bool TestsFailed { get; set; }

        public bool TestsUnavailable { get; set; }

        public int TestCommandsRun { get; set; }

        public bool CoverageDeclared { get; set; }

        public int CoverageReportsRead { get; set; }

        public BuildStatus BuildStatus(VerificationProfile profile)
        {
            if (BuildUnavailable)
                return Core.Verification.BuildStatus.Unavailable;
            if (BuildFailed)
                return Core.Verification.BuildStatus.Failed;

            // A profile with nothing to compile reports "not applicable", not a pass it never ran.
            return profile.Steps.Any(s => s.Build is not null)
                ? Core.Verification.BuildStatus.Passed
                : Core.Verification.BuildStatus.NotApplicable;
        }

        public UnitTestStatus UnitTestStatus()
        {
            if (TestsUnavailable)
                return Core.Verification.UnitTestStatus.Unavailable;
            if (TestsFailed)
                return Core.Verification.UnitTestStatus.Failed;
            if (TestCommandsRun == 0)
                return Core.Verification.UnitTestStatus.NotRun;
            return Tests.Count == 0 ? Core.Verification.UnitTestStatus.NoTests : Core.Verification.UnitTestStatus.Passed;
        }

        public CoverageStatus MeasureCoverage(VerificationRequest request, out ChangedLineCoverage? changedLines)
        {
            changedLines = null;

            // A baseline run has no changed lines to measure.
            if (request.ChangedFiles is null || !CoverageDeclared)
                return CoverageStatus.NotMeasured;

            if (CoverageReportsRead == 0)
            {
                if (TestCommandsRun > 0)
                    Problems.Add("No coverage report was produced, so changed-line coverage could not be measured.");
                return TestCommandsRun > 0 ? CoverageStatus.Unavailable : CoverageStatus.NotMeasured;
            }

            changedLines = ChangedLineCoverageCalculator.Calculate(request.ChangedFiles, Coverage);
            if (changedLines.UnmeasuredFiles.Count > 0)
                Problems.Add($"Not in the coverage report, so not measured: {string.Join(", ", changedLines.UnmeasuredFiles)}.");
            return request.Profile.Coverage is { } rule && changedLines.Percent < rule.ChangedLinesThresholdPercent
                ? CoverageStatus.BelowThreshold
                : CoverageStatus.Met;
        }
    }
}
