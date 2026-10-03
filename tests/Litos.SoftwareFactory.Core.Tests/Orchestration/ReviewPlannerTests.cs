using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// Review was about a third of every task's tokens in the first real runs, and a small, safe
/// change paid the same open-ended review as a change to a file format or to locking. The
/// planner chooses the depth from the host's own evidence, leaning to full.
/// </summary>
public class ReviewPlannerTests
{
    private static readonly RunLimits Limits = new();

    private static FileChange File(string path, int addedLines) => new(path, addedLines == 0 ? [] : [new LineRange(1, addedLines)]);

    private static VerificationOutcome Clean() => new(
        BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.Met,
        [new TestCaseResult("T.Passes", TestOutcome.Passed)], new ChangedLineCoverage(10, 10, []), [], []);

    private static WorkSubmission Work(params CriterionCoverage[] criteria) =>
        new("Did it.", criteria.Length == 0 ? [new CriterionCoverage("It works.", ["T.Passes"])] : criteria, ["T.Passes"], [], []);

    private static string Patch(params (string File, string Line)[] added) => string.Concat(
        added.GroupBy(a => a.File).Select(g => $"--- a/{g.Key}\n+++ b/{g.Key}\n@@ -1,0 +1,{g.Count()} @@\n" + string.Concat(g.Select(a => "+" + a.Line + "\n"))));

    private static ReviewInputs Small(string patch = "") => new(
        [File("src/Orders/OrdersPage.tsx", 12), File("src/Orders/OrdersPage.test.tsx", 30)],
        patch.Length > 0 ? patch : Patch(("src/Orders/OrdersPage.tsx", "const label = 'Export';")),
        Clean(), Work());

    private static ReviewPlan Plan(ReviewInputs inputs) => ReviewPlanner.Plan(inputs, Limits);

    [Fact]
    public void ASmallCleanChangeWithNoRiskSignals_GetsALightReview()
    {
        var plan = Plan(Small());

        Assert.Equal(ReviewDepth.Light, plan.Depth);
        Assert.Equal("Review: light. A small change (12 lines outside tests in 2 files) with a clean verification and no risk signals.", plan.Describe());
    }

    [Fact]
    public void LinesInTests_DoNotCountTowardsTheSize()
    {
        var inputs = Small() with { Files = [File("src/a.ts", 10), File("src/a.test.ts", 900), File("tests/Big/BigTests.cs", 900)] };

        Assert.Equal(ReviewDepth.Light, Plan(inputs).Depth);
    }

    [Fact]
    public void TooManyLinesOutsideTests_MakesItFull_AndSaysHowMany()
    {
        var inputs = Small() with { Files = [File("src/a.ts", 100), File("src/b.ts", 51)] };

        var plan = Plan(inputs);

        Assert.Equal(ReviewDepth.Full, plan.Depth);
        Assert.Contains("it changes 151 lines outside tests (light review is for up to 150)", plan.Reasons);
    }

    [Fact]
    public void ExactlyAtTheLineLimit_StaysLight()
    {
        Assert.Equal(ReviewDepth.Light, Plan(Small() with { Files = [File("src/a.ts", 150)] }).Depth);
    }

    [Fact]
    public void TooManyFiles_MakesItFull()
    {
        var files = Enumerable.Range(1, 9).Select(i => File($"src/f{i}.ts", 1)).ToList();

        Assert.Contains("it touches 9 files (light review is for up to 8)", Plan(Small() with { Files = files }).Reasons);
    }

    [Theory]
    [InlineData("src/Auth/LoginController.cs", "authentication or security")]
    [InlineData("server/permissions.ts", "authentication or security")]
    [InlineData("db/migrations/0003_add_ttl.sql", "schema or migration")]
    [InlineData("src/FileDbSharp/Storage/LogFormat.cs", "storage or a data format")]
    [InlineData("shared/serializer.ts", "storage or a data format")]
    [InlineData("package.json", "build, dependency or deployment")]
    [InlineData("src/App/App.csproj", "build, dependency or deployment")]
    [InlineData(".github/workflows/ci.yml", "build, dependency or deployment")]
    public void RiskyPaths_MakeItFull(string path, string signal)
    {
        var inputs = Small() with { Files = [File(path, 3)], Patch = Patch((path, "x = 1;")) };

        var plan = Plan(inputs);

        Assert.Equal(ReviewDepth.Full, plan.Depth);
        Assert.Contains(plan.Reasons, r => r.Contains(signal));
    }

    [Theory]
    [InlineData("lock (_gate)", "locking or concurrency")]
    [InlineData("_gate.EnterReadLock(); // ReaderWriterLockSlim", "locking or concurrency")]
    [InlineData("await Task.Run(() => Work());", "locking or concurrency")]
    [InlineData("Interlocked.Increment(ref _count);", "locking or concurrency")]
    [InlineData("public async Task<T> GetAsync()", "asynchronous code")]
    [InlineData("const data = await response.json();", "asynchronous code")]
    [InlineData("using var stream = new FileStream(path, FileMode.Open);", "how data is written or stored")]
    [InlineData("BinaryPrimitives.WriteInt64LittleEndian(span, ticks);", "how data is written or stored")]
    [InlineData("localStorage.setItem(KEY, json);", "how data is written or stored")]
    [InlineData("el.innerHTML = html;", "runs processes or injects code or markup")]
    public void RiskyCode_MakesItFull(string line, string signal)
    {
        var plan = Plan(Small(Patch(("src/Orders/OrdersPage.tsx", line))));

        Assert.Equal(ReviewDepth.Full, plan.Depth);
        Assert.Contains(plan.Reasons, r => r.Contains(signal));
    }

    /// <summary>A test that awaits or locks is ordinary test code, not a risk in the change.</summary>
    [Fact]
    public void RiskyCodeInATest_IsNotASignal()
    {
        var patch = Patch(("src/Orders/OrdersPage.tsx", "const label = 'Export';"), ("src/Orders/OrdersPage.test.tsx", "await user.click(button);"));

        Assert.Equal(ReviewDepth.Light, Plan(Small(patch)).Depth);
    }

    [Fact]
    public void RemovingAPublicDeclaration_MakesItFull()
    {
        var patch = "--- a/src/Api.cs\n+++ b/src/Api.cs\n@@ -3 +3 @@\n-    public void Export(string path)\n+    public void Export(string path, bool all)\n";

        var plan = Plan(Small(patch) with { Files = [File("src/Api.cs", 1)] });

        Assert.Contains("it removes or changes 1 public declaration", plan.Reasons);
    }

    [Fact]
    public void RemovedPublicDeclarations_InTests_AreNotCounted()
    {
        Assert.Equal(0, ReviewPlanner.RemovedPublicMembers("--- a/tests/ApiTests.cs\n+++ b/tests/ApiTests.cs\n-    public void Old() { }\n"));
        Assert.Equal(2, ReviewPlanner.RemovedPublicMembers("--- a/src/a.ts\n+++ b/src/a.ts\n-export function a() {}\n-export const b = 1;\n- const c = 2;\n"));
    }

    [Fact]
    public void ADeletedFile_IsAttributedToItsOldName()
    {
        var patch = "--- a/src/Storage/Old.cs\n+++ /dev/null\n-public class Old { }\n";

        Assert.Equal(1, ReviewPlanner.RemovedPublicMembers(patch));
    }

    public static TheoryData<string, VerificationOutcome?, string> VerificationConcerns => new()
    {
        { "not verified", null, "the change has not been verified" },
        { "build failed", Clean() with { Build = BuildStatus.Failed }, "the build is Failed" },
        { "tests failed", Clean() with { UnitTests = UnitTestStatus.Failed }, "the unit tests are Failed" },
        { "no tests", Clean() with { UnitTests = UnitTestStatus.NoTests }, "the unit tests are NoTests" },
        { "coverage short", Clean() with { Coverage = CoverageStatus.BelowThreshold }, "changed-line coverage is BelowThreshold" },
        { "coverage unavailable", Clean() with { Coverage = CoverageStatus.Unavailable }, "changed-line coverage is Unavailable" },
    };

    [Theory]
    [MemberData(nameof(VerificationConcerns))]
    public void AnythingButACleanVerification_MakesItFull(string name, VerificationOutcome? verification, string reason)
    {
        var plan = Plan(Small() with { Verification = verification });

        Assert.True(plan.Depth == ReviewDepth.Full, name);
        Assert.Contains(reason, plan.Reasons);
    }

    [Fact]
    public void ABuildThatDoesNotApply_OrCoverageNotMeasured_IsNoConcern()
    {
        Assert.Equal(ReviewDepth.Light, Plan(Small() with { Verification = Clean() with { Build = BuildStatus.NotApplicable } }).Depth);
        Assert.Equal(ReviewDepth.Light, Plan(Small() with { Verification = Clean() with { Coverage = CoverageStatus.NotMeasured } }).Depth);
    }

    [Fact]
    public void ACriterionWithNoTest_MakesItFull_ButOneTestedByHandDoesNot()
    {
        var untested = Plan(Small() with { Submission = Work(new("It exports.", ["T.Export"]), new("It quotes commas.", [])) });
        var manual = Plan(Small() with { Submission = Work(new("It exports.", ["T.Export"]), new("It looks right in Excel.", [], ManualOnly: true)) });

        Assert.Contains("1 acceptance criterion has no test", untested.Reasons);
        Assert.Equal(ReviewDepth.Light, manual.Depth);
    }

    [Fact]
    public void NoSubmission_OrNoCriteriaMapped_MakesItFull()
    {
        Assert.Contains("there is no submission to check the change against", Plan(Small() with { Submission = null }).Reasons);
        Assert.Contains("the agent mapped no acceptance criterion to a test", Plan(Small() with { Submission = new WorkSubmission("Did it.", [], [], [], []) }).Reasons);
    }

    [Fact]
    public void AnEmptyDiff_MakesItFull()
    {
        Assert.Contains("the diff could not be read", Plan(Small() with { Files = [], Patch = "" }).Reasons);
    }

    [Fact]
    public void AFullPlan_ListsEveryReason_Once()
    {
        var inputs = Small(Patch(("src/Storage/LogFormat.cs", "lock (_gate) { await WriteAsync(); }"))) with
        {
            Files = [File("src/Storage/LogFormat.cs", 200)],
            Verification = Clean() with { Coverage = CoverageStatus.BelowThreshold },
        };

        var plan = Plan(inputs);

        Assert.Equal(
            ["it changes 200 lines outside tests (light review is for up to 150)", "it touches storage or a data format", "it changes locking or concurrency", "it adds asynchronous code", "changed-line coverage is BelowThreshold"],
            plan.Reasons);
        Assert.StartsWith("Review: full, because it changes 200 lines outside tests", plan.Describe());
    }

    [Theory]
    [InlineData("tests/FileDbSharp.Tests/ExpiryTests.cs", true)]
    [InlineData("src/story/render.test.ts", true)]
    [InlineData("src/components/SlideEditor.test.tsx", true)]
    [InlineData("src/__tests__/liveContextUsage.test.ts", true)]
    [InlineData("server/app.spec.ts", true)]
    [InlineData("test/fixtures.ts", true)]
    [InlineData("src/FileDbSharp/FileDatabase.cs", false)]
    [InlineData("src/story/geometry.ts", false)]
    [InlineData("src/Testing/Harness.cs", false)]
    public void IsTest(string path, bool expected) => Assert.Equal(expected, ReviewPlanner.IsTest(path));

    /// <summary>The first real tasks, by what their diffs touched.</summary>
    [Fact]
    public void TheFirstRealTasks_WouldHaveBeenPlannedAsExpected()
    {
        // F4's rework: one exception's base type.
        var f4Rework = new ReviewInputs(
            [File("src/FileDbSharp/Exceptions.cs", 1), File("src/FileDbSharp/FileDatabaseOptions.cs", 1), File("tests/FileDbSharp.Tests/ReadOnlyModeTests.cs", 25)],
            Patch(("src/FileDbSharp/Exceptions.cs", "public sealed class ReadOnlyDatabaseException : InvalidOperationException")),
            Clean(), Work());
        // F6: the file format.
        var f6 = new ReviewInputs(
            [File("src/FileDbSharp/Storage/LogFormat.cs", 30), File("src/FileDbSharp/FileDatabase.cs", 90)],
            Patch(("src/FileDbSharp/Storage/LogFormat.cs", "BinaryPrimitives.WriteInt64LittleEndian(payload[pos..], op.ExpiresAtUtcTicks);")),
            Clean(), Work());

        Assert.Equal(ReviewDepth.Light, Plan(f4Rework).Depth);
        Assert.Equal(ReviewDepth.Full, Plan(f6).Depth);
    }
}

public class TurnAllowanceTests
{
    private static readonly RunLimits Limits = new();

    [Fact]
    public void ALightReview_IsAskedToFinishAlmostAtOnce_AndStoppedSoonAfter()
    {
        var allowance = TurnAllowance.ForReview(ReviewDepth.Light, implementationTokens: 300_000, Limits);

        Assert.Equal(new TurnAllowance(2, null, 4), allowance);
    }

    [Fact]
    public void AFullReview_MayCostHalfOfTheImplementation()
    {
        var allowance = TurnAllowance.ForReview(ReviewDepth.Full, implementationTokens: 169_403, Limits);

        Assert.Equal(new TurnAllowance(12, 84_702, 24), allowance);
    }

    [Fact]
    public void ACheapImplementation_StillGetsARealReview()
    {
        Assert.Equal(40_000, TurnAllowance.ForReview(ReviewDepth.Full, implementationTokens: 10_000, Limits).WrapUpAfterTokens);
        Assert.Equal(40_000, TurnAllowance.ForReview(ReviewDepth.Full, implementationTokens: 0, Limits).WrapUpAfterTokens);
    }

    [Fact]
    public void AReminderAfterAReviewRanOut_GetsLittleMore()
    {
        Assert.Equal(new TurnAllowance(2, null, 4), TurnAllowance.ForReviewNudge(Limits));
    }

    [Fact]
    public void TheShareAndFloorAreConfigurable()
    {
        var limits = new RunLimits { ReviewAllowanceShare = 0.25, ReviewAllowanceFloorTokens = 5_000, FullReviewWrapUpCalls = 6, FullReviewMaxToolCalls = 9 };

        Assert.Equal(new TurnAllowance(6, 25_000, 9), TurnAllowance.ForReview(ReviewDepth.Full, 100_000, limits));
    }
}

/// <summary>A review that runs out of tool calls has still looked at the change.</summary>
public class ReviewToolLimitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static (RunOrchestrator Orchestrator, RunState State) AtReview()
    {
        var orchestrator = new RunOrchestrator();
        var state = RunOrchestrator.NewRun(RunKind.Implement);
        state = orchestrator.Next(state, new RunStarted(), T0).State;
        state = orchestrator.Next(state, new PreflightCompleted(true), T0).State;
        state = orchestrator.Next(state, new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [], [], [], []), FilesChanged: true), T0).State;
        var passing = new VerificationOutcome(BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.Met, [new TestCaseResult("T", TestOutcome.Passed)], new ChangedLineCoverage(1, 1, []), [], []);
        var review = orchestrator.Next(state, new Verified(passing), T0);
        Assert.Equal(TurnKind.Review, Assert.IsType<StartTurnStep>(review.Step).Kind);
        return (orchestrator, review.State);
    }

    [Fact]
    public void AReviewThatRunsOutOfToolCalls_IsRemindedToSubmit_NotBlocked()
    {
        var (orchestrator, state) = AtReview();

        var next = orchestrator.Next(state, new TurnEnded(TurnEndReason.ToolCallLimit), T0);

        var nudge = Assert.IsType<StartTurnStep>(next.Step);
        Assert.Equal((TurnKind.Nudge, BriefKind.Nudge, SessionScope.Review), (nudge.Kind, nudge.Brief, nudge.Session));
    }

    [Fact]
    public void IfTheReminderAlsoRunsOut_TheRunIsBlocked()
    {
        var (orchestrator, state) = AtReview();
        state = orchestrator.Next(state, new TurnEnded(TurnEndReason.ToolCallLimit), T0).State;

        var next = orchestrator.Next(state, new TurnEnded(TurnEndReason.ToolCallLimit), T0);

        var stop = Assert.IsType<StopStep>(next.Step);
        Assert.Equal((LifecycleTrigger.Block, StopReason.NoCompletionCall), (stop.Trigger, stop.Reason));
        Assert.Contains("submit_review", stop.Message);
    }

    [Fact]
    public void AReminderThatSubmits_ContinuesToHandoff()
    {
        var (orchestrator, state) = AtReview();
        state = orchestrator.Next(state, new TurnEnded(TurnEndReason.ToolCallLimit), T0).State;

        var next = orchestrator.Next(state, new TurnEnded(TurnEndReason.Completed, new ReviewSubmission([])), T0);

        Assert.IsType<HandoffStep>(next.Step);
    }

    [Fact]
    public void AnImplementTurnThatRunsOutOfToolCalls_IsStillBlocked()
    {
        var orchestrator = new RunOrchestrator();
        var state = orchestrator.Next(RunOrchestrator.NewRun(RunKind.Implement), new RunStarted(), T0).State;
        state = orchestrator.Next(state, new PreflightCompleted(true), T0).State;

        var stop = Assert.IsType<StopStep>(orchestrator.Next(state, new TurnEnded(TurnEndReason.ToolCallLimit), T0).Step);

        Assert.Equal(StopReason.ToolCallLimit, stop.Reason);
    }
}
