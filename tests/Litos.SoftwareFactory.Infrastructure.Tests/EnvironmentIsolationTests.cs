using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Processes;
using Litos.SoftwareFactory.Infrastructure.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Tests;

/// <summary>
/// Verification commands build and run code the agent wrote, so they must not see the host's
/// environment — the same rule the worker's own launch follows.
/// </summary>
public class EnvironmentIsolationTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _logs = new();
    private readonly ProcessRunner _runner = new();

    public void Dispose()
    {
        _repo.Dispose();
        _logs.Dispose();
    }

    private static string PrintEnvironment => OperatingSystem.IsWindows() ? "set" : "env";

    // ---- ProcessRunner ----

    [Fact]
    public async Task ReplaceEnvironment_TheCommandSeesOnlyWhatItWasGiven()
    {
        var name = "FACTORY_HOST_ONLY_" + Guid.NewGuid().ToString("n")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(name, "host-secret-value"); // this process only
        try
        {
            var environment = ToolchainEnvironment.Scrub(ToolchainEnvironment.CurrentHostEnvironment())
                .ToDictionary(e => e.Key, e => (string?)e.Value);
            environment["GIVEN_VALUE"] = "given";

            var inherited = await _runner.RunAsync(Shell.Request(PrintEnvironment, _repo.Path), default);
            var replaced = await _runner.RunAsync(
                Shell.Request(PrintEnvironment, _repo.Path) with { Environment = environment, ReplaceEnvironment = true }, default);

            Assert.Contains("host-secret-value", inherited.StandardOutput); // the default still inherits
            Assert.True(replaced.Succeeded, replaced.StandardError);
            Assert.DoesNotContain("host-secret-value", replaced.StandardOutput);
            Assert.DoesNotContain(name, replaced.StandardOutput);
            Assert.Contains("GIVEN_VALUE=given", replaced.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>A command killed at its timeout can still deliver output afterwards. With a log
    /// file that used to write to a closed file on the reader thread, which nothing catches.</summary>
    [Fact]
    public async Task TimeoutWithALogFile_WhileTheCommandIsStillWriting_DoesNotFault()
    {
        var script = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,200000) do @echo line-%i"
            : "i=0; while true; do echo line-$i; i=$((i+1)); done";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await _runner.RunAsync(
                Shell.Request(script, _repo.Path) with { Timeout = TimeSpan.FromMilliseconds(300), LogPath = _logs.Combine($"noisy-{attempt}.log") },
                default);

            Assert.True(result.TimedOut);
        }

        await Task.Delay(500); // late callbacks, if any, have had time to arrive
    }

    // ---- ToolchainEnvironment ----

    [Fact]
    public void Scrub_KeepsToolchainVariables_AndDropsEverythingElse()
    {
        var scrubbed = ToolchainEnvironment.Scrub(new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            ["DOTNET_ROOT"] = "/dotnet",
            ["ANTHROPIC_API_KEY"] = "sk-ant",
            ["ConnectionStrings__FactoryState"] = "Password=x",
            ["FACTORY_GITHUB_TOKEN"] = "ghp_x",
            ["FACTORY_WORKER_SECRET"] = "secret",
            ["NUGET_API_KEY"] = "nuget",
        });

        Assert.Equal(new[] { "DOTNET_ROOT", "PATH" }, scrubbed.Keys.Order(StringComparer.Ordinal));
    }

    // ---- ProfileVerifier ----

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero, TimedOut: false, Started: true));
        }
    }

    private static readonly Dictionary<string, string> HostWithSecrets = new()
    {
        ["PATH"] = "/usr/bin",
        ["DOTNET_ROOT"] = "/dotnet",
        ["ANTHROPIC_API_KEY"] = "sk-ant-must-not-leak",
        ["OPENROUTER_API_KEY"] = "sk-or-must-not-leak",
        ["ConnectionStrings__FactoryState"] = "Password=db-must-not-leak",
        ["FACTORY_GITHUB_TOKEN"] = "ghp_must_not_leak",
        ["FACTORY_ADMIN_PASSWORD"] = "admin-must-not-leak",
    };

    private static VerificationProfile Profile(string executable, IReadOnlyList<string> arguments) => new(
        2,
        [
            new VerificationStep("step",
                Restore: new CommandSpec(executable, arguments),
                Build: new CommandSpec(executable, arguments),
                UnitTests: [new UnitTestCommand(executable, arguments, TestReport: new TestReportSpec(TestReportFormat.Junit, "junit.xml"))]),
        ],
        Environment: new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1" });

    [Fact]
    public async Task EveryVerificationCommand_GetsAReplacedEnvironment_WithoutTheHostsSecrets()
    {
        var runner = new RecordingRunner();
        var verifier = new ProfileVerifier(runner, () => HostWithSecrets);

        await verifier.VerifyAsync(new VerificationRequest(_repo.Path, Profile("tool", ["x"]), null, _logs.Path), default);

        Assert.Equal(3, runner.Requests.Count); // restore, build, unit tests
        Assert.All(runner.Requests, request =>
        {
            Assert.True(request.ReplaceEnvironment);
            Assert.Equal(
                new[] { "CI", "DOTNET_NOLOGO", "DOTNET_ROOT", "PATH" },
                request.Environment.Keys.Order(StringComparer.Ordinal));
            Assert.DoesNotContain(request.Environment.Values, v => v!.Contains("must-not-leak") || v.Contains("must_not_leak"));
        });
    }

    /// <summary>Real processes: what a test the agent wrote would actually see.</summary>
    [Fact]
    public async Task RealVerificationCommand_CannotReadTheHostsSecrets()
    {
        var name = "FACTORY_GITHUB_TOKEN_" + Guid.NewGuid().ToString("n")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(name, "ghp_real_process_secret"); // this process only
        try
        {
            var script = Shell.WriteScript(_repo.Path, "dump-env", "set > seen-env.txt", "env > seen-env.txt");
            var host = new Dictionary<string, string>(ToolchainEnvironment.CurrentHostEnvironment())
            {
                ["ANTHROPIC_API_KEY"] = "sk-ant-must-not-leak",
            };

            await new ProfileVerifier(hostEnvironment: () => host).VerifyAsync(
                new VerificationRequest(_repo.Path, Profile(script, []), null, _logs.Path), default);

            var seen = File.ReadAllText(_repo.Combine("seen-env.txt"));
            Assert.Contains("CI=true", seen);
            Assert.Contains("DOTNET_NOLOGO=1", seen);
            Assert.DoesNotContain("ghp_real_process_secret", seen);
            Assert.DoesNotContain(name, seen);
            Assert.DoesNotContain("sk-ant-must-not-leak", seen);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // ---- The run's own temporary directory ----

    /// <summary>Concurrent runs must not share temporary files, and a profile cannot point a run
    /// at another run's directory.</summary>
    [Fact]
    public async Task VerificationCommands_UseTheRunsOwnTempDirectory_OverTheHostsAndTheProfiles()
    {
        var runner = new RecordingRunner();
        var host = new Dictionary<string, string>(HostWithSecrets) { ["TEMP"] = @"C:\host-temp", ["TMP"] = @"C:\host-temp" };
        var profile = Profile("tool", ["x"]) with { Environment = new Dictionary<string, string> { ["TEMP"] = "profile-temp" } };
        var temp = _logs.Combine("run-tmp");

        await new ProfileVerifier(runner, () => host).VerifyAsync(
            new VerificationRequest(_repo.Path, profile, null, _logs.Path) { TempDirectory = temp }, default);

        Assert.True(Directory.Exists(temp));
        Assert.All(runner.Requests, request =>
        {
            Assert.Equal(temp, request.Environment["TEMP"]);
            Assert.Equal(temp, request.Environment["TMP"]);
            Assert.Equal(temp, request.Environment["TMPDIR"]);
        });
    }

    [Fact]
    public async Task VerificationCommands_WithNoRunTempDirectory_KeepTheHostsTemp()
    {
        var runner = new RecordingRunner();
        var host = new Dictionary<string, string>(HostWithSecrets) { ["TEMP"] = @"C:\host-temp" };

        await new ProfileVerifier(runner, () => host).VerifyAsync(new VerificationRequest(_repo.Path, Profile("tool", ["x"]), null, _logs.Path), default);

        Assert.All(runner.Requests, request =>
        {
            Assert.Equal(@"C:\host-temp", request.Environment["TEMP"]);
            Assert.False(request.Environment.ContainsKey("TMPDIR"));
        });
    }

    /// <summary>Real processes: what a test the agent wrote would actually see.</summary>
    [Fact]
    public async Task RealVerificationCommand_SeesTheRunsTempDirectory()
    {
        var script = Shell.WriteScript(_repo.Path, "dump-temp", "set > seen-env.txt", "env > seen-env.txt");
        var temp = _logs.Combine("real-run-tmp");

        await new ProfileVerifier().VerifyAsync(
            new VerificationRequest(_repo.Path, Profile(script, []), null, _logs.Path) { TempDirectory = temp }, default);

        var seen = File.ReadAllText(_repo.Combine("seen-env.txt"));
        Assert.Contains($"TEMP={temp}", seen);
    }

    // ---- Changed-line coverage: nothing measured must not read as "met" without saying so ----

    private static CoverageFile Coverage(string path, bool inRepository, params (int Line, int Hits)[] lines) =>
        new(path, lines.ToDictionary(l => l.Line, l => l.Hits), inRepository);

    private static FileChange Change(string path, int start, int end) => new(path, [new LineRange(start, end)]);

    [Fact]
    public void ChangedFileAbsentFromTheReport_IsListedAsUnmeasured()
    {
        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("src/Orders.cs", 1, 2), Change("src/NewModule.cs", 1, 40)],
            [Coverage("src/Orders.cs", true, (1, 1), (2, 1))]);

        Assert.Equal(["src/NewModule.cs"], result.UnmeasuredFiles);
        Assert.Equal(2, result.Measurable);
    }

    [Fact]
    public void FileWithOnlyDeletions_IsNotListedAsUnmeasured()
    {
        var result = ChangedLineCoverageCalculator.Calculate([new FileChange("src/Trimmed.cs", [])], []);

        Assert.Empty(result.UnmeasuredFiles);
    }

    /// <summary>A report entry already placed at an exact repository path is not lent, by
    /// suffix, to a different file that shares its name.</summary>
    [Fact]
    public void EntryPlacedInTheRepository_IsMatchedExactly_NotBySuffix()
    {
        var coverage = Coverage("src/Program.cs", inRepository: true, (1, 5));

        var rootFile = ChangedLineCoverageCalculator.Calculate([Change("Program.cs", 1, 1)], [coverage]);
        var deeperFile = ChangedLineCoverageCalculator.Calculate([Change("tools/src/Program.cs", 1, 1)], [coverage]);
        var theFileItself = ChangedLineCoverageCalculator.Calculate([Change("src/Program.cs", 1, 1)], [coverage]);

        Assert.Equal(0, rootFile.Measurable);
        Assert.Equal(["Program.cs"], rootFile.UnmeasuredFiles);
        Assert.Equal(0, deeperFile.Measurable);
        Assert.Equal(1, theFileItself.Covered);
    }

    [Fact]
    public void EntryThatCouldNotBePlaced_IsStillMatchedBySuffix()
    {
        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("src/FileDbSharp/Collection.cs", 28, 28)], [Coverage("Collection.cs", inRepository: false, (28, 3))]);

        Assert.Equal(1, result.Covered);
        Assert.Empty(result.UnmeasuredFiles);
    }

    [Fact]
    public void Normalize_MarksEntriesItPlacedInTheRepository()
    {
        _repo.Write("src/A.cs", "");
        var outside = OperatingSystem.IsWindows() ? @"Z:\elsewhere\B.cs" : "/elsewhere/B.cs";
        var report = new CoverageReport([], [Coverage("src/A.cs", false, (1, 1)), Coverage(outside, false, (1, 1))]);

        var files = ChangedLineCoverageCalculator.Normalize(report, _repo.Path, _repo.Path);

        Assert.True(files[0].InRepository);
        Assert.False(files[1].InRepository);
    }

    [Fact]
    public async Task Verifier_NamesChangedFilesTheCoverageReportDoesNotCover()
    {
        var script = Shell.WriteScript(
            _repo.Path, "tests",
            "copy /y canned-junit.xml junit.xml >nul\ncopy /y canned-coverage.xml coverage.xml >nul",
            "cp canned-junit.xml junit.xml\ncp canned-coverage.xml coverage.xml");
        _repo.Write("canned-junit.xml", """<testsuite><testcase classname="c" name="passes" /></testsuite>""");
        _repo.Write("canned-coverage.xml", """<coverage><packages><package><classes><class filename="src/Orders.cs"><lines><line number="1" hits="1" /></lines></class></classes></package></packages></coverage>""");
        var profile = new VerificationProfile(2,
        [
            new VerificationStep("web", UnitTests:
            [
                new UnitTestCommand(script, [], 30, new TestReportSpec(TestReportFormat.Junit, "junit.xml"),
                    new CoverageReportSpec(CoverageReportFormat.Cobertura, "coverage.xml")),
            ]),
        ], new CoverageRule(80));
        IReadOnlyList<FileChange> changes = [Change("src/Orders.cs", 1, 1), Change("src/Untested.cs", 1, 50)];

        var outcome = await new ProfileVerifier().VerifyAsync(new VerificationRequest(_repo.Path, profile, changes, _logs.Path), default);

        Assert.Equal(CoverageStatus.Met, outcome.Coverage);
        Assert.Equal(["src/Untested.cs"], outcome.ChangedLines!.UnmeasuredFiles);
        Assert.Contains(outcome.Problems, p => p.Contains("not measured") && p.Contains("src/Untested.cs"));
    }
}
