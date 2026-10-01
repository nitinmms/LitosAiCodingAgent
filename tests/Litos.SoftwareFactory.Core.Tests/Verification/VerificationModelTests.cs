using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Verification;

public class VerificationProfileTests
{
    /// <summary>The schema example from ReadMe_LitosSoftwareFactory_V1.md §10.1, with real commands.</summary>
    private const string BlueprintJson = """
        {
          "profileVersion": 2,
          "environment": { "CI": "true" },
          "steps": [
            {
              "name": "api",
              "workingDirectory": "src/api",
              "restore": { "executable": "dotnet", "arguments": ["restore"], "timeoutSeconds": 600 },
              "build":   { "executable": "dotnet", "arguments": ["build", "-nodeReuse:false"], "timeoutSeconds": 600 },
              "unitTests": [
                { "executable": "dotnet", "arguments": ["test", "--logger", "trx"], "timeoutSeconds": 300,
                  "testReport":     { "format": "junit", "path": "artifacts/test-results/*.xml" },
                  "coverageReport": { "format": "cobertura", "path": "artifacts/coverage/*.xml" } }
              ]
            }
          ],
          "coverage": { "changedLinesThresholdPercent": 80 },
          "applicationTesting": "human",
          "databaseExecution": "disabled",
          "maximumRepairCycles": 2
        }
        """;

    private static VerificationProfile Valid() => VerificationProfile.Parse(BlueprintJson);

    [Fact]
    public void Parse_BlueprintExample_ReadsEveryField()
    {
        var profile = Valid();

        Assert.Equal(2, profile.ProfileVersion);
        Assert.Equal("true", profile.Environment!["CI"]);
        Assert.Equal(80, profile.Coverage!.ChangedLinesThresholdPercent);
        Assert.Equal(2, profile.MaximumRepairCycles);

        var step = Assert.Single(profile.Steps);
        Assert.Equal("api", step.Name);
        Assert.Equal("src/api", step.WorkingDirectory);
        Assert.Equal("dotnet", step.Restore!.Executable);
        Assert.Equal(["build", "-nodeReuse:false"], step.Build!.Arguments);
        Assert.Equal(600, step.Build.TimeoutSeconds);

        var test = Assert.Single(step.UnitTests!);
        Assert.Equal(["test", "--logger", "trx"], test.Arguments);
        Assert.Equal(300, test.TimeoutSeconds);
        Assert.Equal(new TestReportSpec(TestReportFormat.Junit, "artifacts/test-results/*.xml"), test.TestReport);
        Assert.Equal(new CoverageReportSpec(CoverageReportFormat.Cobertura, "artifacts/coverage/*.xml"), test.CoverageReport);
    }

    [Fact]
    public void Parse_BlueprintExample_IsValid()
    {
        Assert.Empty(Valid().Validate());
    }

    [Theory]
    [InlineData("trx", TestReportFormat.Trx)]
    [InlineData("junit", TestReportFormat.Junit)]
    public void Parse_TestReportFormats(string format, TestReportFormat expected)
    {
        var profile = VerificationProfile.Parse(BlueprintJson.Replace("\"format\": \"junit\"", $"\"format\": \"{format}\""));

        Assert.Equal(expected, profile.Steps[0].UnitTests![0].TestReport!.Format);
    }

    [Fact]
    public void Parse_LcovCoverage()
    {
        var profile = VerificationProfile.Parse(BlueprintJson.Replace("\"format\": \"cobertura\"", "\"format\": \"lcov\""));

        Assert.Equal(CoverageReportFormat.Lcov, profile.Steps[0].UnitTests![0].CoverageReport!.Format);
    }

    [Fact]
    public void Parse_UnknownReportFormat_IsRejected()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => VerificationProfile.Parse(BlueprintJson.Replace("\"format\": \"junit\"", "\"format\": \"tap\"")));
    }

    [Fact]
    public void ToJson_RoundTrips()
    {
        var profile = Valid();

        var reparsed = VerificationProfile.Parse(profile.ToJson());

        Assert.Equal(profile.ToJson(), reparsed.ToJson());
        Assert.Contains("\"format\": \"junit\"", profile.ToJson());
    }

    /// <summary>"Every command is optional except unit tests": an interpreted stack leaves build out.</summary>
    [Fact]
    public void Validate_NoRestoreOrBuild_IsValid()
    {
        var profile = new VerificationProfile(2,
        [
            new VerificationStep("py", UnitTests:
            [
                new UnitTestCommand("pytest", ["--junitxml=report.xml"], TestReport: new TestReportSpec(TestReportFormat.Junit, "report.xml")),
            ]),
        ]);

        Assert.Empty(profile.Validate());
        Assert.Equal(["unitTests"], profile.Steps[0].Commands().Select(c => c.Label));
    }

    [Fact]
    public void Validate_NoUnitTests_IsInvalid()
    {
        var profile = new VerificationProfile(2, [new VerificationStep("api", Build: new CommandSpec("dotnet", ["build"]))]);

        Assert.Contains(profile.Validate(), p => p.Contains("no unit-test command"));
    }

    [Fact]
    public void Validate_UnitTestsWithoutATestReport_IsInvalid_BecauseCountsComeOnlyFromReports()
    {
        var profile = new VerificationProfile(2, [new VerificationStep("api", UnitTests: [new UnitTestCommand("dotnet", ["test"])])]);

        Assert.Contains(profile.Validate(), p => p.Contains("must declare a testReport"));
    }

    [Fact]
    public void Validate_NoSteps_IsInvalid()
    {
        Assert.Contains(new VerificationProfile(2, []).Validate(), p => p.Contains("at least one step"));
    }

    [Fact]
    public void Validate_WrongVersion_IsInvalid()
    {
        Assert.Contains((Valid() with { ProfileVersion = 1 }).Validate(), p => p.Contains("profileVersion must be 2"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_ThresholdOutOfRange_IsInvalid(double threshold)
    {
        Assert.Contains((Valid() with { Coverage = new CoverageRule(threshold) }).Validate(), p => p.Contains("between 0 and 100"));
    }

    [Fact]
    public void Validate_NegativeRepairCycles_IsInvalid()
    {
        Assert.Contains((Valid() with { MaximumRepairCycles = -1 }).Validate(), p => p.Contains("maximumRepairCycles"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("src/../../outside")]
    [InlineData("/etc")]
    public void Validate_WorkingDirectoryOutsideTheRepository_IsInvalid(string directory)
    {
        var profile = Valid();
        profile = profile with { Steps = [profile.Steps[0] with { WorkingDirectory = directory }] };

        Assert.Contains(profile.Validate(), p => p.Contains("must stay inside the repository"));
    }

    [Fact]
    public void Validate_ReportPathOutsideTheWorkingDirectory_IsInvalid()
    {
        var profile = new VerificationProfile(2,
        [
            new VerificationStep("api", UnitTests:
            [
                new UnitTestCommand("dotnet", ["test"], TestReport: new TestReportSpec(TestReportFormat.Trx, "../../elsewhere/*.trx")),
            ]),
        ]);

        Assert.Contains(profile.Validate(), p => p.Contains("must stay inside the step's working directory"));
    }

    [Fact]
    public void Validate_EmptyExecutableOrBadTimeout_IsInvalid()
    {
        var profile = new VerificationProfile(2,
        [
            new VerificationStep("api",
                Restore: new CommandSpec("", ["restore"]),
                Build: new CommandSpec("dotnet", ["build"], TimeoutSeconds: 0),
                UnitTests: [new UnitTestCommand("dotnet", ["test"], TestReport: new TestReportSpec(TestReportFormat.Trx, "*.trx"))]),
        ]);

        var problems = profile.Validate();

        Assert.Contains(problems, p => p.Contains("restore command has no executable"));
        Assert.Contains(problems, p => p.Contains("build command's timeoutSeconds must be positive"));
    }

    [Fact]
    public void Validate_UnnamedStep_IsInvalid()
    {
        var profile = Valid();
        profile = profile with { Steps = [profile.Steps[0] with { Name = " " }] };

        Assert.Contains(profile.Validate(), p => p.Contains("Every step needs a name"));
    }

    [Fact]
    public void Validate_ReportsEveryProblem_NotJustTheFirst()
    {
        var profile = new VerificationProfile(1, [new VerificationStep("")], new CoverageRule(500), MaximumRepairCycles: -2);

        Assert.True(profile.Validate().Count >= 5);
    }

    [Fact]
    public void Commands_AreInRunOrder()
    {
        Assert.Equal(["restore", "build", "unitTests"], Valid().Steps[0].Commands().Select(c => c.Label));
    }

    [Fact]
    public void Display_JoinsExecutableAndArguments()
    {
        Assert.Equal("dotnet build -nodeReuse:false", new CommandSpec("dotnet", ["build", "-nodeReuse:false"]).Display());
        Assert.Equal("make", new CommandSpec("make", []).Display());
    }
}

public class VerificationOutcomeTests
{
    private static VerificationOutcome Outcome(params (string Name, TestOutcome Outcome)[] tests) => new(
        BuildStatus.Passed, UnitTestStatus.Failed, CoverageStatus.NotMeasured,
        [.. tests.Select(t => new TestCaseResult(t.Name, t.Outcome))], null, [], []);

    [Fact]
    public void Counts_ComeFromTheTestCases()
    {
        var outcome = Outcome(("A", TestOutcome.Passed), ("B", TestOutcome.Passed), ("C", TestOutcome.Failed), ("D", TestOutcome.Skipped));

        Assert.Equal(2, outcome.PassedCount);
        Assert.Equal(1, outcome.FailedCount);
        Assert.Equal(1, outcome.SkippedCount);
        Assert.Equal(new HashSet<string> { "C" }, outcome.FailingTestNames);
    }

    [Fact]
    public void NewFailures_ExcludeTestsAlreadyFailingOnTheBaseline()
    {
        var baseline = Outcome(("Old", TestOutcome.Failed), ("Fine", TestOutcome.Passed));
        var outcome = Outcome(("Old", TestOutcome.Failed), ("Fine", TestOutcome.Failed), ("Added", TestOutcome.Failed));

        Assert.Equal(["Fine", "Added"], outcome.NewFailures(baseline).Select(t => t.Name));
        Assert.Equal(["Old"], outcome.PreExistingFailures(baseline).Select(t => t.Name));
    }

    [Fact]
    public void NewFailures_NoBaseline_AreAllFailures()
    {
        var outcome = Outcome(("A", TestOutcome.Failed), ("B", TestOutcome.Passed));

        Assert.Equal(["A"], outcome.NewFailures(null).Select(t => t.Name));
        Assert.Empty(outcome.PreExistingFailures(null));
    }

    [Fact]
    public void NewTestCount_CountsTestsTheBaselineDidNotHave()
    {
        var baseline = Outcome(("A", TestOutcome.Passed), ("B", TestOutcome.Passed));
        var outcome = Outcome(("A", TestOutcome.Passed), ("B", TestOutcome.Passed), ("C", TestOutcome.Passed), ("D", TestOutcome.Failed));

        Assert.Equal(2, outcome.NewTestCount(baseline));
        Assert.Equal(0, outcome.NewTestCount(null)); // without a baseline nothing can be called new
    }

    [Fact]
    public void ChangedLineCoverage_Percent()
    {
        Assert.Equal(91, new ChangedLineCoverage(91, 100, []).Percent);
        Assert.Equal(50, new ChangedLineCoverage(1, 2, []).Percent);
    }

    [Fact]
    public void ChangedLineCoverage_NothingMeasurable_IsOneHundredPercent()
    {
        Assert.Equal(100, new ChangedLineCoverage(0, 0, []).Percent);
    }

    [Fact]
    public void CommandRun_SucceededOnlyOnExitZeroWithoutTimeout()
    {
        Assert.True(new CommandRun("api", "build", "dotnet build", 0, TimeSpan.FromSeconds(3), TimedOut: false).Succeeded);
        Assert.False(new CommandRun("api", "build", "dotnet build", 1, TimeSpan.FromSeconds(3), TimedOut: false).Succeeded);
        Assert.False(new CommandRun("api", "build", "dotnet build", null, TimeSpan.FromSeconds(600), TimedOut: true).Succeeded);
        Assert.False(new CommandRun("api", "build", "dotnet build", 0, TimeSpan.FromSeconds(600), TimedOut: true).Succeeded);
    }

    [Fact]
    public void LineRange_CountIsInclusive()
    {
        Assert.Equal(1, new LineRange(7, 7).Count);
        Assert.Equal(4, new LineRange(7, 10).Count);
    }
}
