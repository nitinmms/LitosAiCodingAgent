using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>README_Litos.Probe.Web.md W3.3: findings 6 (generated output) and 14 (limitations the verification contradicts).</summary>
public class EvidenceAndReviewTests
{
    [Theory]
    [InlineData("artifacts/factory-test-results/abc/coverage.cobertura.xml")]
    [InlineData("Tests/TestResults/run.trx")]
    [InlineData(@"Tests\bin\Debug\net10.0\App.dll")]
    [InlineData("obj/project.assets.json")]
    [InlineData("node_modules/left-pad/index.js")]
    [InlineData("coverage/lcov.info")]
    [InlineData("test-results/junit.xml")]
    [InlineData("web/coverage-final.json")]
    [InlineData("src/__pycache__/app.cpython-312.pyc")]
    [InlineData("playwright-report/index.html")]
    public void Build_test_and_coverage_output_is_generated(string path) =>
        Assert.True(GeneratedOutput.IsGenerated(path));

    [Theory]
    [InlineData("src/Coverage.cs")]
    [InlineData("src/coverage.ts")]
    [InlineData("Tests/CartTests.cs")]
    [InlineData("docs/test-results.md")]
    [InlineData("Binder/Model.cs")]
    [InlineData("logs/README.md")]
    [InlineData("litos.test.yaml")]
    public void Source_is_not(string path) =>
        Assert.False(GeneratedOutput.IsGenerated(path));

    [Fact]
    public void Find_keeps_only_the_generated_paths_in_order() =>
        Assert.Equal(["obj/a.json", "x.trx"], GeneratedOutput.Find(["src/a.cs", "obj/a.json", "x.trx", "README.md"]));

    private static VerificationOutcome Ran(params TestCaseResult[] tests) =>
        new(BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.Met, tests, null, [], []);

    private static readonly VerificationOutcome W27 = Ran(
        new("ClinicPortal.Tests.TestDataTests.Seeds_one_user_per_role", TestOutcome.Passed),
        new("ClinicPortal.Tests.TestDataTests.Seeds_appointments(count: 3)", TestOutcome.Passed),
        new("ClinicPortal.Tests.TestDataTests.Reset_clears_the_data", TestOutcome.Passed),
        new("ClinicPortal.Tests.TestingEnvironmentTests.Reset_is_gone_in_Production", TestOutcome.Passed),
        new("ClinicPortal.Tests.CancelTests.Only_upcoming_can_be_cancelled", TestOutcome.Failed));

    [Fact]
    public void A_limitation_that_says_tests_fail_is_answered_when_the_verification_ran_them_green()
    {
        const string limitation = "TestDataTests and TestingEnvironmentTests fail on a clean checkout (DbSet for IdentityUserPasskey).";

        var text = HandoffComposer.Contradicted(limitation, W27);

        Assert.StartsWith(limitation, text);
        Assert.EndsWith(
            "(Not what the factory measured: its verification ran TestDataTests (3 tests) and TestingEnvironmentTests (1 test), and all passed.)", text);
    }

    [Theory]
    // Nothing about failing.
    [InlineData("TestDataTests seed only users, not appointments.")]
    // A named test that did fail: the limitation is true.
    [InlineData("CancelTests fail when the clock passes midnight.")]
    // No test named, or a name too short to be one.
    [InlineData("Four tests fail on a clean checkout.")]
    [InlineData("Some Tests fail.")]
    public void Anything_else_is_left_as_written(string limitation) =>
        Assert.Equal(limitation, HandoffComposer.Contradicted(limitation, W27));

    [Fact]
    public void Without_a_verification_nothing_is_answered() =>
        Assert.Equal("TestDataTests fail.", HandoffComposer.Contradicted("TestDataTests fail.", null));

    [Fact]
    public void A_method_name_counts_too() =>
        Assert.Contains("Reset_is_gone_in_Production (1 test), and all passed",
            HandoffComposer.Contradicted("Reset_is_gone_in_Production fails on Linux.", W27));
}
