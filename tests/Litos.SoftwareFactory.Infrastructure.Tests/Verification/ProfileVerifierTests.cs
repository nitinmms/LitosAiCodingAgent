using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Processes;
using Litos.SoftwareFactory.Infrastructure.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Verification;

public class ProfileVerifierTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _logs = new();

    public void Dispose()
    {
        _repo.Dispose();
        _logs.Dispose();
    }

    /// <summary>
    /// Stands in for the toolchain: each command is recognised by its first argument and can
    /// write files into the working directory, the way a real test runner writes its reports.
    /// </summary>
    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly Dictionary<string, Func<ProcessRequest, ProcessResult>> _handlers = [];

        public List<ProcessRequest> Requests { get; } = [];

        public ScriptedRunner On(string firstArgument, Func<ProcessRequest, ProcessResult> handler)
        {
            _handlers[firstArgument] = handler;
            return this;
        }

        public ScriptedRunner On(string firstArgument, int exitCode = 0, Action<ProcessRequest>? effect = null) =>
            On(firstArgument, request =>
            {
                effect?.Invoke(request);
                return Exit(exitCode);
            });

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_handlers.TryGetValue(request.Arguments[0], out var handler) ? handler(request) : Exit(0));
        }

        public static ProcessResult Exit(int code) => new(code, "", "", TimeSpan.FromSeconds(1), TimedOut: false, Started: true);

        public static ProcessResult TimedOut() => new(null, "", "", TimeSpan.FromSeconds(300), TimedOut: true, Started: true);
    }

    private const string PassingTrx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
          <UnitTestResult testName="T.One" outcome="Passed" />
          <UnitTestResult testName="T.Two" outcome="Passed" />
        </Results></TestRun>
        """;

    private const string FailingTrx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
          <UnitTestResult testName="T.One" outcome="Passed" />
          <UnitTestResult testName="T.Two" outcome="Failed"><Output><ErrorInfo><Message>expected 1 but was 2</Message></ErrorInfo></Output></UnitTestResult>
        </Results></TestRun>
        """;

    private static string Cobertura(params (int Line, int Hits)[] lines) =>
        "<coverage><packages><package><classes><class filename=\"src/Orders.cs\"><lines>"
        + string.Concat(lines.Select(l => $"<line number=\"{l.Line}\" hits=\"{l.Hits}\" />"))
        + "</lines></class></classes></package></packages></coverage>";

    private static VerificationProfile Profile(
        bool restore = true, bool build = true, bool coverage = true, double? threshold = 80, string? workingDirectory = null) => new(
        2,
        [
            new VerificationStep(
                "api", workingDirectory,
                restore ? new CommandSpec("tool", ["restore"]) : null,
                build ? new CommandSpec("tool", ["build"]) : null,
                [
                    new UnitTestCommand("tool", ["test"], 300,
                        new TestReportSpec(TestReportFormat.Trx, "results/*.trx"),
                        coverage ? new CoverageReportSpec(CoverageReportFormat.Cobertura, "results/**/coverage.xml") : null),
                ]),
        ],
        threshold is null ? null : new CoverageRule(threshold.Value));

    private static readonly IReadOnlyList<FileChange> ChangedOrders = [new FileChange("src/Orders.cs", [new LineRange(10, 13)])];

    private Task<VerificationOutcome> VerifyAsync(
        ScriptedRunner runner, VerificationProfile? profile = null, IReadOnlyList<FileChange>? changes = null, bool baseline = false) =>
        new ProfileVerifier(runner).VerifyAsync(
            new VerificationRequest(_repo.Path, profile ?? Profile(), baseline ? null : changes ?? ChangedOrders, _logs.Path), default);

    private void WriteReports(ProcessRequest request, string trx, string? cobertura)
    {
        Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, "results", "guid"));
        File.WriteAllText(Path.Combine(request.WorkingDirectory, "results", "run.trx"), trx);
        if (cobertura is not null)
            File.WriteAllText(Path.Combine(request.WorkingDirectory, "results", "guid", "coverage.xml"), cobertura);
    }

    private ScriptedRunner PassingRunner(string? cobertura = null) =>
        new ScriptedRunner().On("test", effect: r => WriteReports(r, PassingTrx, cobertura ?? Cobertura((10, 1), (11, 1), (12, 1), (13, 1))));

    // ---- The happy path ----

    [Fact]
    public async Task EverythingPasses_ReportsCountsAndCoverageFromTheReports()
    {
        var outcome = await VerifyAsync(PassingRunner());

        Assert.Equal(BuildStatus.Passed, outcome.Build);
        Assert.Equal(UnitTestStatus.Passed, outcome.UnitTests);
        Assert.Equal(CoverageStatus.Met, outcome.Coverage);
        Assert.Equal(2, outcome.PassedCount);
        Assert.Equal(0, outcome.FailedCount);
        Assert.Equal(100, outcome.ChangedLines!.Percent);
        Assert.Empty(outcome.Problems);
    }

    [Fact]
    public async Task Commands_RunInOrder_InTheStepDirectory_AndAreRecordedAsEvidence()
    {
        Directory.CreateDirectory(_repo.Combine("src", "api"));
        var runner = PassingRunner();

        var outcome = await VerifyAsync(runner, Profile(workingDirectory: "src/api"));

        Assert.Equal(["restore", "build", "test"], runner.Requests.Select(r => r.Arguments[0]));
        Assert.All(runner.Requests, r => Assert.Equal(Path.GetFullPath(_repo.Combine("src", "api")), r.WorkingDirectory));
        Assert.Equal(["restore", "build", "unitTests"], outcome.Commands.Select(c => c.Label));
        Assert.Equal(["tool restore", "tool build", "tool test"], outcome.Commands.Select(c => c.Command));
        Assert.All(outcome.Commands, c => Assert.True(c.Succeeded));
        Assert.All(outcome.Commands, c => Assert.Equal("api", c.Step));
    }

    [Fact]
    public async Task Commands_GetCiTrue_TheProfilesEnvironment_TheirTimeout_AndALogFile()
    {
        var runner = PassingRunner();
        var profile = Profile() with { Environment = new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1" } };

        var outcome = await VerifyAsync(runner, profile);

        Assert.All(runner.Requests, r => Assert.Equal("true", r.Environment["CI"]));
        Assert.All(runner.Requests, r => Assert.Equal("1", r.Environment["DOTNET_NOLOGO"]));
        Assert.Equal(TimeSpan.FromSeconds(600), runner.Requests[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(300), runner.Requests[2].Timeout);
        Assert.All(runner.Requests, r => Assert.StartsWith(_logs.Path, r.LogPath));
        Assert.Equal(3, runner.Requests.Select(r => r.LogPath).Distinct().Count());
        Assert.Equal(runner.Requests.Select(r => r.LogPath), outcome.Commands.Select(c => c.LogPath));
    }

    [Fact]
    public async Task ProfileCannotTurnCiOff()
    {
        var runner = PassingRunner();
        var profile = Profile() with { Environment = new Dictionary<string, string> { ["ci"] = "false" } };

        await VerifyAsync(runner, profile);

        Assert.All(runner.Requests, r => Assert.Equal("true", r.Environment["CI"]));
        Assert.All(runner.Requests, r => Assert.Single(r.Environment, e => e.Key.Equals("CI", StringComparison.OrdinalIgnoreCase)));
    }

    // ---- Stale reports ----

    /// <summary>Old results can never count as new evidence: a report left by an earlier run is
    /// deleted before the commands run, so a test command that writes nothing has nothing.</summary>
    [Fact]
    public async Task StaleReports_AreDeletedBeforeTheRun_SoTheyCannotCountAsEvidence()
    {
        _repo.Write("results/old.trx", PassingTrx);
        _repo.Write("results/guid/coverage.xml", Cobertura((10, 1)));
        var runner = new ScriptedRunner().On("test", exitCode: 1); // crashes without writing a report

        var outcome = await VerifyAsync(runner);

        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
        Assert.Empty(outcome.Tests);
        Assert.False(File.Exists(_repo.Combine("results", "old.trx")));
        Assert.False(File.Exists(_repo.Combine("results", "guid", "coverage.xml")));
    }

    [Fact]
    public async Task StaleReports_AreDeletedBeforeRestoreAndBuild_NotJustBeforeTests()
    {
        _repo.Write("results/old.trx", PassingTrx);
        var existedDuringRestore = true;
        var runner = PassingRunner().On("restore", effect: r => existedDuringRestore = File.Exists(Path.Combine(r.WorkingDirectory, "results", "old.trx")));

        await VerifyAsync(runner);

        Assert.False(existedDuringRestore);
    }

    // ---- Build ----

    [Fact]
    public async Task BuildFails_TestsAreNotRun_AndNothingIsReportedAsPassing()
    {
        var runner = PassingRunner().On("build", exitCode: 1);

        var outcome = await VerifyAsync(runner);

        Assert.Equal(BuildStatus.Failed, outcome.Build);
        Assert.Equal(UnitTestStatus.NotRun, outcome.UnitTests);
        Assert.Equal(CoverageStatus.NotMeasured, outcome.Coverage);
        Assert.DoesNotContain(runner.Requests, r => r.Arguments[0] == "test");
        Assert.Contains(outcome.Problems, p => p.Contains("build failed with exit code 1"));
    }

    [Fact]
    public async Task RestoreFails_CountsAsABuildFailure_AndStopsTheStep()
    {
        var runner = PassingRunner().On("restore", exitCode: 2);

        var outcome = await VerifyAsync(runner);

        Assert.Equal(BuildStatus.Failed, outcome.Build);
        Assert.Equal(["restore"], runner.Requests.Select(r => r.Arguments[0]));
        Assert.Contains(outcome.Problems, p => p.Contains("restore failed with exit code 2"));
    }

    [Fact]
    public async Task BuildTimesOut_IsAFailure_ThatSaysSo()
    {
        var runner = PassingRunner().On("build", _ => ScriptedRunner.TimedOut());

        var outcome = await VerifyAsync(runner);

        Assert.Equal(BuildStatus.Failed, outcome.Build);
        Assert.Contains(outcome.Problems, p => p.Contains("build timed out after 600 seconds"));
        Assert.True(outcome.Commands.Single(c => c.Label == "build").TimedOut);
    }

    [Fact]
    public async Task ToolchainMissing_IsUnavailable_NotFailed()
    {
        var runner = new ScriptedRunner().On("restore", _ => ProcessResult.NotStarted("'tool' could not be started: not found"));

        var outcome = await VerifyAsync(runner);

        Assert.Equal(BuildStatus.Unavailable, outcome.Build);
        Assert.Equal(UnitTestStatus.NotRun, outcome.UnitTests);
        Assert.Contains(outcome.Problems, p => p.Contains("could not be started"));
        Assert.Null(outcome.Commands.Single().LogPath);
    }

    [Fact]
    public async Task NoBuildCommand_IsNotApplicable_NeverAPass()
    {
        var outcome = await VerifyAsync(PassingRunner(), Profile(restore: false, build: false));

        Assert.Equal(BuildStatus.NotApplicable, outcome.Build);
        Assert.Equal(UnitTestStatus.Passed, outcome.UnitTests);
    }

    [Fact]
    public async Task StepDirectoryMissing_IsUnavailable_AndRunsNothing()
    {
        var runner = PassingRunner();

        var outcome = await VerifyAsync(runner, Profile(workingDirectory: "no/such/dir"));

        Assert.Equal(BuildStatus.Unavailable, outcome.Build);
        Assert.Empty(runner.Requests);
        Assert.Contains(outcome.Problems, p => p.Contains("does not exist"));
    }

    // ---- Unit tests ----

    [Fact]
    public async Task FailingTest_IsReadFromTheReport_WithItsMessage()
    {
        var runner = new ScriptedRunner().On("test", exitCode: 1, effect: r => WriteReports(r, FailingTrx, null));

        var outcome = await VerifyAsync(runner, Profile(coverage: false));

        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
        Assert.Equal(1, outcome.PassedCount);
        var failed = Assert.Single(outcome.Tests, t => t.Outcome == TestOutcome.Failed);
        Assert.Equal("T.Two", failed.Name);
        Assert.Equal("expected 1 but was 2", failed.Message);
    }

    /// <summary>The agent saying tests passed, or an exit code of zero, cannot override the report.</summary>
    [Fact]
    public async Task ReportShowsAFailure_EvenThoughTheCommandExitedZero_IsFailed()
    {
        var runner = new ScriptedRunner().On("test", exitCode: 0, effect: r => WriteReports(r, FailingTrx, null));

        var outcome = await VerifyAsync(runner, Profile(coverage: false));

        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
    }

    [Fact]
    public async Task NonZeroExitWithNoReport_IsFailedNoReport_NeverPassed()
    {
        var runner = new ScriptedRunner().On("test", exitCode: 1);

        var outcome = await VerifyAsync(runner);

        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
        Assert.Equal(0, outcome.PassedCount);
        Assert.Contains(outcome.Problems, p => p.Contains("exit code 1 (no report)"));
    }

    [Fact]
    public async Task NonZeroExit_ThoughTheReportShowsNoFailure_IsStillFailed()
    {
        var runner = new ScriptedRunner().On("test", exitCode: 1, effect: r => WriteReports(r, PassingTrx, null));

        var outcome = await VerifyAsync(runner, Profile(coverage: false));

        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
        Assert.Contains(outcome.Problems, p => p.Contains("although the report shows no failed test"));
    }

    [Fact]
    public async Task ZeroExitWithNoReport_IsNoTests_NotPassed()
    {
        var outcome = await VerifyAsync(new ScriptedRunner());

        Assert.Equal(UnitTestStatus.NoTests, outcome.UnitTests);
        Assert.Contains(outcome.Problems, p => p.Contains("produced no test results"));
    }

    /// <summary>§16: an interrupted test run is incomplete, never pass or fail evidence on its own.</summary>
    [Fact]
    public async Task TestRunTimesOut_IsFailed_AndReportedAsIncomplete()
    {
        var runner = new ScriptedRunner().On("test", _ => ScriptedRunner.TimedOut());

        var outcome = await VerifyAsync(runner);

        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
        Assert.Contains(outcome.Problems, p => p.Contains("timed out after 300 seconds") && p.Contains("incomplete"));
    }

    [Fact]
    public async Task TestToolMissing_IsUnavailable()
    {
        var runner = new ScriptedRunner().On("test", _ => ProcessResult.NotStarted("'tool' could not be started"));

        var outcome = await VerifyAsync(runner);

        Assert.Equal(UnitTestStatus.Unavailable, outcome.UnitTests);
        Assert.Equal(BuildStatus.Passed, outcome.Build);
    }

    [Fact]
    public async Task UnreadableTestReport_IsAFailure_NotSilentlySkipped()
    {
        var runner = new ScriptedRunner().On("test", effect: r => WriteReports(r, "<TestRun><Results>", null));

        var outcome = await VerifyAsync(runner, Profile(coverage: false));

        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
        Assert.Contains(outcome.Problems, p => p.Contains("run.trx") && p.Contains("could not be read"));
    }

    [Fact]
    public async Task SeveralReportFiles_AreAllRead()
    {
        var runner = new ScriptedRunner().On("test", effect: r =>
        {
            WriteReports(r, PassingTrx, null);
            File.WriteAllText(Path.Combine(r.WorkingDirectory, "results", "second.trx"), PassingTrx.Replace("T.One", "U.One").Replace("T.Two", "U.Two"));
        });

        var outcome = await VerifyAsync(runner, Profile(coverage: false));

        Assert.Equal(4, outcome.PassedCount);
    }

    [Fact]
    public async Task JUnitReports_AreReadWhenTheProfileDeclaresThem()
    {
        var profile = new VerificationProfile(2,
        [
            new VerificationStep("web", UnitTests:
            [
                new UnitTestCommand("npm", ["test"], TestReport: new TestReportSpec(TestReportFormat.Junit, "test-results/junit.xml")),
            ]),
        ]);
        var runner = new ScriptedRunner().On("test", effect: r =>
        {
            Directory.CreateDirectory(Path.Combine(r.WorkingDirectory, "test-results"));
            File.WriteAllText(Path.Combine(r.WorkingDirectory, "test-results", "junit.xml"), Fixtures.Read("insta-story.junit.xml"));
        });

        var outcome = await VerifyAsync(runner, profile);

        Assert.Equal(UnitTestStatus.Passed, outcome.UnitTests);
        Assert.Equal(123, outcome.PassedCount);
        Assert.Equal(BuildStatus.NotApplicable, outcome.Build);
    }

    // ---- Coverage ----

    [Fact]
    public async Task CoverageBelowThreshold_ListsTheUncoveredChangedLines()
    {
        var outcome = await VerifyAsync(PassingRunner(Cobertura((10, 1), (11, 0), (12, 0), (13, 1))));

        Assert.Equal(CoverageStatus.BelowThreshold, outcome.Coverage);
        Assert.Equal(50, outcome.ChangedLines!.Percent);
        Assert.Equal([11, 12], Assert.Single(outcome.ChangedLines.Uncovered).Lines);
        Assert.Equal(UnitTestStatus.Passed, outcome.UnitTests);
    }

    [Fact]
    public async Task CoverageExactlyAtTheThreshold_IsMet()
    {
        var outcome = await VerifyAsync(PassingRunner(Cobertura((10, 1), (11, 1), (12, 0), (13, 1))), Profile(threshold: 75));

        Assert.Equal(CoverageStatus.Met, outcome.Coverage);
    }

    [Fact]
    public async Task NoThreshold_CoverageIsMeasuredAndMet()
    {
        var outcome = await VerifyAsync(PassingRunner(Cobertura((10, 0), (11, 0))), Profile(threshold: null));

        Assert.Equal(CoverageStatus.Met, outcome.Coverage);
        Assert.Equal(0, outcome.ChangedLines!.Percent);
    }

    /// <summary>The baseline run is on the base commit: there are no changed lines to measure.</summary>
    [Fact]
    public async Task BaselineRun_DoesNotMeasureChangedLineCoverage()
    {
        var outcome = await VerifyAsync(PassingRunner(), baseline: true);

        Assert.Equal(CoverageStatus.NotMeasured, outcome.Coverage);
        Assert.Null(outcome.ChangedLines);
        Assert.Equal(UnitTestStatus.Passed, outcome.UnitTests);
    }

    [Fact]
    public async Task NoCoverageReportDeclared_IsNotMeasured()
    {
        var runner = new ScriptedRunner().On("test", effect: r => WriteReports(r, PassingTrx, null));

        var outcome = await VerifyAsync(runner, Profile(coverage: false));

        Assert.Equal(CoverageStatus.NotMeasured, outcome.Coverage);
    }

    [Fact]
    public async Task CoverageReportDeclaredButNotProduced_IsUnavailable_NeverMet()
    {
        var runner = new ScriptedRunner().On("test", effect: r => WriteReports(r, PassingTrx, null));

        var outcome = await VerifyAsync(runner);

        Assert.Equal(CoverageStatus.Unavailable, outcome.Coverage);
        Assert.Null(outcome.ChangedLines);
        Assert.Contains(outcome.Problems, p => p.Contains("No coverage report was produced"));
    }

    [Fact]
    public async Task UnreadableCoverageReport_IsUnavailable_WithTheReason()
    {
        var outcome = await VerifyAsync(PassingRunner("<coverage><packages>"));

        Assert.Equal(CoverageStatus.Unavailable, outcome.Coverage);
        Assert.Contains(outcome.Problems, p => p.Contains("coverage.xml") && p.Contains("could not be read"));
    }

    [Fact]
    public async Task ChangedFilesOutsideTheCoverageReport_AreNotHeldAgainstTheTask()
    {
        IReadOnlyList<FileChange> changes = [new FileChange("README.md", [new LineRange(1, 30)])];

        var outcome = await VerifyAsync(PassingRunner(), changes: changes);

        Assert.Equal(CoverageStatus.Met, outcome.Coverage);
        Assert.Equal(0, outcome.ChangedLines!.Measurable);
    }

    // ---- Several steps ----

    private static VerificationProfile TwoSteps() => new(
        2,
        [
            new VerificationStep("api", "api", Build: new CommandSpec("tool", ["build"]),
                UnitTests: [new UnitTestCommand("tool", ["test"], TestReport: new TestReportSpec(TestReportFormat.Trx, "results/*.trx"))]),
            new VerificationStep("web", "web", Build: new CommandSpec("tool", ["build"]),
                UnitTests: [new UnitTestCommand("tool", ["test"], TestReport: new TestReportSpec(TestReportFormat.Trx, "results/*.trx"))]),
        ]);

    [Fact]
    public async Task MultiStackRepository_RunsEachStepInItsOwnDirectory_AndAddsTheResults()
    {
        Directory.CreateDirectory(_repo.Combine("api"));
        Directory.CreateDirectory(_repo.Combine("web"));
        var runner = new ScriptedRunner().On("test", effect: r => WriteReports(r, PassingTrx, null));

        var outcome = await VerifyAsync(runner, TwoSteps());

        Assert.Equal(4, outcome.PassedCount);
        Assert.Equal(["api", "api", "web", "web"], outcome.Commands.Select(c => c.Step));
        Assert.Equal(
            [_repo.Combine("api"), _repo.Combine("web")],
            runner.Requests.Where(r => r.Arguments[0] == "test").Select(r => r.WorkingDirectory));
    }

    [Fact]
    public async Task OneStepsBuildFails_TheOtherStepStillRuns_AndTheBuildIsFailedOverall()
    {
        Directory.CreateDirectory(_repo.Combine("api"));
        Directory.CreateDirectory(_repo.Combine("web"));
        var runner = new ScriptedRunner()
            .On("build", r => ScriptedRunner.Exit(r.WorkingDirectory.EndsWith("api") ? 1 : 0))
            .On("test", effect: r => WriteReports(r, PassingTrx, null));

        var outcome = await VerifyAsync(runner, TwoSteps());

        Assert.Equal(BuildStatus.Failed, outcome.Build);
        Assert.Equal(2, outcome.PassedCount); // the web step's tests
        Assert.Single(runner.Requests, r => r.Arguments[0] == "test");
    }

    // ---- With real processes ----

    /// <summary>A test runner left in watch mode cannot hang a run past its timeout
    /// (acceptance scenario 10): the real runner kills it and the run is reported incomplete.</summary>
    [Fact]
    public async Task RealProcess_HangingTestCommand_IsKilledAtItsTimeout()
    {
        var script = Shell.WriteScript(_repo.Path, "watch", Shell.Sleep(60), Shell.Sleep(60));
        var profile = new VerificationProfile(2,
        [
            new VerificationStep("web", UnitTests:
            [
                new UnitTestCommand(script, [], TimeoutSeconds: 1, new TestReportSpec(TestReportFormat.Junit, "junit.xml")),
            ]),
        ]);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await new ProfileVerifier().VerifyAsync(new VerificationRequest(_repo.Path, profile, null, _logs.Path), default);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"took {stopwatch.Elapsed}");
        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
        Assert.True(Assert.Single(outcome.Commands).TimedOut);
    }

    [Fact]
    public async Task RealProcess_CommandWritesAReport_WhichIsReadBack_AndItsOutputIsLogged()
    {
        _repo.Write("canned.xml", """<testsuite><testcase classname="c" name="passes" /></testsuite>""");
        var script = Shell.WriteScript(
            _repo.Path, "run-tests",
            "echo running tests\ncopy /y canned.xml junit.xml >nul",
            "echo running tests\ncp canned.xml junit.xml");
        var profile = new VerificationProfile(2,
        [
            new VerificationStep("web", UnitTests:
            [
                new UnitTestCommand(script, [], TimeoutSeconds: 30, new TestReportSpec(TestReportFormat.Junit, "junit.xml")),
            ]),
        ]);

        var outcome = await new ProfileVerifier().VerifyAsync(new VerificationRequest(_repo.Path, profile, null, _logs.Path), default);

        Assert.Equal(UnitTestStatus.Passed, outcome.UnitTests);
        Assert.Equal("c :: passes", Assert.Single(outcome.Tests).Name);
        Assert.Contains("running tests", File.ReadAllText(Assert.Single(outcome.Commands).LogPath!));
    }
}

public class VerificationPresetTests
{
    [Theory]
    [InlineData(VerificationPresets.DotNet)]
    [InlineData(VerificationPresets.NodeReact)]
    public void Preset_LoadsAndIsAValidProfile(string name)
    {
        var profile = VerificationPresets.Load(name);

        Assert.Empty(profile.Validate());
        Assert.Equal(80, profile.Coverage!.ChangedLinesThresholdPercent);
        Assert.Equal(2, profile.MaximumRepairCycles);
        Assert.Equal("true", profile.Environment!["CI"]);
    }

    [Fact]
    public void Names_ListTheM1Presets()
    {
        Assert.Equal(["dotnet", "node-react"], VerificationPresets.Names);
    }

    [Fact]
    public void DotNet_UsesTrxAndCobertura_AndTurnsOffSharedBuildServers()
    {
        var step = Assert.Single(VerificationPresets.Load(VerificationPresets.DotNet).Steps);

        Assert.Equal("dotnet restore", step.Restore!.Display());
        Assert.Contains("-nodeReuse:false", step.Build!.Arguments);
        Assert.Contains("--disable-build-servers", step.Build.Arguments);
        var test = Assert.Single(step.UnitTests!);
        Assert.Equal(TestReportFormat.Trx, test.TestReport!.Format);
        Assert.Equal(CoverageReportFormat.Cobertura, test.CoverageReport!.Format);
        Assert.Contains("XPlat Code Coverage", test.Arguments);
        Assert.Equal("1", VerificationPresets.Load(VerificationPresets.DotNet).Environment!["MSBUILDDISABLENODEREUSE"]);
    }

    /// <summary>Matches how insta-story-generator is set up: `npm run test:ci` writes
    /// test-results/junit.xml and coverage/cobertura-coverage.xml.</summary>
    [Fact]
    public void NodeReact_UsesNpmCi_AJUnitReport_AndANonWatchTestCommand()
    {
        var step = Assert.Single(VerificationPresets.Load(VerificationPresets.NodeReact).Steps);

        Assert.Equal("npm ci", step.Restore!.Display());
        var test = Assert.Single(step.UnitTests!);
        Assert.Equal("npm run test:ci", test.Display());
        Assert.Equal(new TestReportSpec(TestReportFormat.Junit, "test-results/junit.xml"), test.TestReport);
        Assert.Equal(new CoverageReportSpec(CoverageReportFormat.Cobertura, "coverage/cobertura-coverage.xml"), test.CoverageReport);
    }

    [Fact]
    public void Load_UnknownPreset_NamesTheKnownOnes()
    {
        var ex = Assert.Throws<ArgumentException>(() => VerificationPresets.Load("cobol"));

        Assert.Contains("dotnet", ex.Message);
        Assert.Contains("node-react", ex.Message);
    }

    [Fact]
    public void LoadJson_IsTheEditableText_AnAdminStartsFrom()
    {
        var json = VerificationPresets.LoadJson(VerificationPresets.DotNet);

        Assert.Contains("\"profileVersion\": 2", json);
        Assert.Equal(VerificationProfile.Parse(json).ToJson(), VerificationPresets.Load(VerificationPresets.DotNet).ToJson());
    }
}
