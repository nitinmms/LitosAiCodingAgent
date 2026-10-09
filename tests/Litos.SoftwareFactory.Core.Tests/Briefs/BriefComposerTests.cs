using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Briefs;

public class BriefComposerTests
{
    private static readonly RunLimits Limits = new();

    private static readonly RunContext Context = new("SalesApp", "factory/7f3a-csv-export", "main", "Add CSV export for Orders.")
    {
        AcceptanceCriteria = ["Administrators can export.", "Non-administrators are refused."],
        VerificationSummary = "dotnet build, then dotnet test (TRX, Cobertura)",
        CoverageThresholdPercent = 80,
    };

    private static RunState State(RunKind kind = RunKind.Implement) => RunOrchestrator.NewRun(kind);

    private static string Compose(BriefKind brief, RunState state, RunContext? context = null, RunLimits? limits = null, TurnKind kind = TurnKind.Implement) =>
        BriefComposer.Compose(new StartTurnStep(kind, brief), context ?? Context, state, limits ?? Limits);

    private static VerificationOutcome Failing(params (string Name, string Message)[] tests) => new(
        BuildStatus.Passed, UnitTestStatus.Failed, CoverageStatus.NotMeasured,
        [.. tests.Select(t => new TestCaseResult(t.Name, TestOutcome.Failed, t.Message))], null, [], []);

    // ---- Run brief ----

    [Fact]
    public void RunBrief_CarriesTheRequestTheContractAndTheInstructionToFinishWithSubmitWork()
    {
        var brief = Compose(BriefKind.Run, State());

        Assert.Contains("**SalesApp**", brief);
        Assert.Contains("`factory/7f3a-csv-export`", brief);
        Assert.Contains("created from `main`", brief);
        Assert.Contains("Add CSV export for Orders.", brief);
        Assert.Contains("1. Administrators can export.", brief);
        Assert.Contains("2. Non-administrators are refused.", brief);
        Assert.Contains("dotnet build, then dotnet test (TRX, Cobertura)", brief);
        Assert.Contains("at least 80% covered", brief);
        Assert.Contains("call `submit_work`", brief);
        Assert.Contains("`request_decision`", brief);
        Assert.Contains("Do not commit, push, merge", brief);
    }

    [Fact]
    public void RunBrief_OmitsEmptySections_AndLeavesNoPlaceholdersOrBlankRuns()
    {
        var context = new RunContext("P", "factory/x", "main", "Do the thing.");

        var brief = Compose(BriefKind.Run, State(), context);

        Assert.DoesNotContain("{{", brief);
        Assert.DoesNotContain("Acceptance criteria", brief);
        Assert.DoesNotContain("Decisions already made", brief);
        Assert.DoesNotContain("Project lessons", brief);
        Assert.DoesNotContain("already failing", brief);
        Assert.DoesNotContain("\n\n\n", brief);
        Assert.Contains("(not configured)", brief);
    }

    [Fact]
    public void RunBrief_IncludesDecisionsLessonsAndPreExistingFailures()
    {
        var context = Context with { Lessons = ["Exports must honour the tenant filter."], SpecificationSummary = "Export orders as CSV." };
        var state = State() with
        {
            Decisions = [new AnsweredDecision("All rows or the current page?", "All filtered rows.")],
            Baseline = Failing(("Legacy.BrokenTest", "old")),
        };

        var brief = Compose(BriefKind.Run, state, context);

        Assert.Contains("## Approved specification", brief);
        Assert.Contains("Export orders as CSV.", brief);
        Assert.Contains("**All rows or the current page?** — All filtered rows.", brief);
        Assert.Contains("- Exports must honour the tenant filter.", brief);
        Assert.Contains("- `Legacy.BrokenTest`", brief);
    }

    /// <summary>A request quoting template syntax must come through literally: substitution is a
    /// single pass, so values are never expanded.</summary>
    [Fact]
    public void RunBrief_RequestContainingBraces_IsNotExpanded()
    {
        var context = Context with { Request = "Render {{branch}} and {{nonexistent}} literally." };

        var brief = Compose(BriefKind.Run, State(), context);

        Assert.Contains("Render {{branch}} and {{nonexistent}} literally.", brief);
    }

    // ---- Rework brief ----

    [Fact]
    public void ReworkBrief_CarriesFeedbackPreviousHandoffAndChangedFiles()
    {
        var context = Context with
        {
            TesterFeedback = "CSV values containing commas are incorrect.",
            PreviousHandoffSummary = "Added OrdersCsvExporter and 4 tests.",
            ChangedFiles = ["src/OrdersCsvExporter.cs", "tests/OrdersCsvExporterTests.cs"],
        };

        var brief = Compose(BriefKind.Rework, State(RunKind.Rework), context, kind: TurnKind.Rework);

        Assert.Contains("CSV values containing commas are incorrect.", brief);
        Assert.Contains("Added OrdersCsvExporter and 4 tests.", brief);
        Assert.Contains("- `src/OrdersCsvExporter.cs`", brief);
        Assert.Contains("- `tests/OrdersCsvExporterTests.cs`", brief);
        Assert.Contains("do not start over", brief);
        Assert.Contains("`submit_work`", brief);
    }

    /// <summary>
    /// A rework or repair turn can follow a compaction, whose summary keeps the request and the
    /// decisions but not the rules. So those briefs state the rules themselves rather than
    /// pointing back at a run brief that may no longer be in the conversation.
    /// </summary>
    [Theory]
    [InlineData(BriefKind.Rework, TurnKind.Rework)]
    [InlineData(BriefKind.Repair, TurnKind.Repair)]
    public void ReworkAndRepairBriefs_RestateTheExecutionContract(BriefKind brief, TurnKind kind)
    {
        var text = Compose(brief, State(RunKind.Rework) with { RepairCyclesUsed = 1 }, kind: kind);

        Assert.Contains("The execution contract still applies:", text);
        Assert.Contains("Do not commit, push, merge, switch branches or rewrite history.", text);
        Assert.Contains("Do not delete or weaken existing tests", text);
        Assert.Contains("Do not start the application", text);
        Assert.DoesNotContain("from the original run brief", text);
        Assert.Contains("Work only inside the working directory", text);
        // F6's rework sent its criteria as plain strings: the shape is restated with the contract.
        Assert.Contains("never plain strings", text);
        Assert.Contains("new { criterion = \"...\", tests = new[] { \"TestName\" } }", text);
        // How to call the completion tools survives a compaction too.
        Assert.Contains("never call one to test it", text);
        Assert.Contains("`submit_work(\"summary\", \"...\", \"testsAdded\", new[] { \"...\" })`", text);
    }

    /// <summary>On F6 the agent made a file format change that left existing databases unreadable,
    /// and reported it as a limitation. The briefs name the choices that always need a decision.</summary>
    [Fact]
    public void TheRunBrief_NamesTheChoicesThatAlwaysNeedADecision()
    {
        var brief = Compose(BriefKind.Run, State());

        Assert.Contains("These are always material choices, so ask before making one:", brief);
        Assert.Contains("existing data, files or saved state that would no longer load or would change meaning", brief);
        Assert.Contains("removing or changing public behaviour that callers rely on", brief);
        Assert.Contains("ask instead of reporting it", brief);
    }

    [Fact]
    public void TheReworkBrief_AsksBeforeBreakingWhatExists()
    {
        var brief = Compose(BriefKind.Rework, State(RunKind.Rework), kind: TurnKind.Rework);

        Assert.Contains("or addressing it would stop existing data, files or callers from working, call `request_decision` instead", brief);
    }

    /// <summary>An agent probed request_decision's parameter names with test calls, and one reached
    /// the user as a decision. The briefs name its fields and forbid calling a tool to test it.</summary>
    [Theory]
    [InlineData(BriefKind.Run, TurnKind.Implement)]
    [InlineData(BriefKind.Review, TurnKind.Review)]
    public void Briefs_ShowTheKernelCallingForm_AndForbidTestCalls(BriefKind brief, TurnKind kind)
    {
        var text = Compose(brief, State(), Context with { Diff = "+x", ChangedLineCount = 1 }, kind: kind);

        Assert.Contains("never call one to test it or to find out its parameters", text);
        Assert.Contains("pass the arguments as name and value pairs; named C# arguments", text);
        Assert.Contains("```csharp", text);
        if (brief == BriefKind.Run)
            Assert.Contains("Other directories on this machine, including other tasks' working copies, are not yours", text);
        if (brief == BriefKind.Run)
            Assert.Contains("Its fields are `question`, `whyItBlocks`, `options` (two to four)", text);
    }

    /// <summary>On the fifth real task the agent ran `taskkill /F /IM dotnet.exe /T` to clear a
    /// hung test, and stopped every dotnet process on the machine, its own worker included.</summary>
    [Theory]
    [InlineData(BriefKind.Run, TurnKind.Implement)]
    [InlineData(BriefKind.Rework, TurnKind.Rework)]
    [InlineData(BriefKind.Repair, TurnKind.Repair)]
    public void EveryBriefThatCanRunCommands_ForbidsStoppingProcessesByName(BriefKind brief, TurnKind kind)
    {
        var text = Compose(brief, State() with { RepairCyclesUsed = 1 }, kind: kind);

        Assert.Contains("Never stop processes by name", text);
        Assert.Contains("`taskkill /IM`", text);
        Assert.Contains("by its id", text);
    }

    // ---- Working economically ----
    //
    // Every model call re-sends the whole conversation. In the first real runs a turn made 30 to
    // 60 calls, mostly one small step each, and that count was what the tokens were spent on.

    [Theory]
    [InlineData(BriefKind.Run, TurnKind.Implement)]
    [InlineData(BriefKind.Rework, TurnKind.Rework)]
    [InlineData(BriefKind.Repair, TurnKind.Repair)]
    [InlineData(BriefKind.Review, TurnKind.Review)]
    public void EveryWorkBrief_SaysThatCallsAreWhatCosts_AndToReadFilesTogether(BriefKind brief, TurnKind kind)
    {
        var text = Compose(brief, State() with { RepairCyclesUsed = 1 }, kind: kind);

        Assert.Contains("re-sends this whole conversation", text);
        Assert.Matches("(?i)in (one|a single) (script|call)", text);
    }

    /// <summary>R1's context reached 67,000 tokens, most of it from ten whole files printed early
    /// and paid for again on every later call: R1 made 10 read_file calls and none used a line
    /// range, F6 made 23 and one did. search_code and read_file's offset and limit exist for this.</summary>
    [Theory]
    [InlineData(BriefKind.Run, TurnKind.Implement)]
    [InlineData(BriefKind.Rework, TurnKind.Rework)]
    [InlineData(BriefKind.Repair, TurnKind.Repair)]
    [InlineData(BriefKind.Review, TurnKind.Review)]
    public void EveryWorkBrief_SaysToFindWithSearchCode_ThenReadOnlyTheLinesNeeded(BriefKind brief, TurnKind kind)
    {
        var text = Compose(brief, State() with { RepairCyclesUsed = 1 }, kind: kind);

        Assert.Contains("`search_code`", text);
        Assert.Contains("`read_file` takes `offset` and `limit`", text);
        Assert.DoesNotContain("read every file you need in a single call", text);
    }

    [Theory]
    [InlineData(BriefKind.Run, TurnKind.Implement)]
    [InlineData(BriefKind.Rework, TurnKind.Rework)]
    [InlineData(BriefKind.Repair, TurnKind.Repair)]
    public void WorkBriefs_SayToPrintOnlyTheSummaryAndFailuresOfABuildOrTestRun(BriefKind brief, TurnKind kind)
    {
        var text = Compose(brief, State() with { RepairCyclesUsed = 1 }, kind: kind);

        Assert.Contains("only the summary and the failures of a build or test run", text);
    }

    /// <summary>On F5's re-run a test hung after a locking change, and the agent concluded the
    /// environment could not run tests; it spent about 35 calls building a way around the runner.</summary>
    [Theory]
    [InlineData(BriefKind.Run, TurnKind.Implement)]
    [InlineData(BriefKind.Rework, TurnKind.Rework)]
    [InlineData(BriefKind.Repair, TurnKind.Repair)]
    public void WorkBriefs_SayAHangingTestRunIsAHangingTest(BriefKind brief, TurnKind kind)
    {
        var text = Compose(brief, State() with { RepairCyclesUsed = 1 }, kind: kind);

        Assert.Contains("If a test run hangs or times out, one of the tests is hanging, most likely on code you changed", text);
        Assert.Contains("The test runner works in this environment, so do not build a way around it.", text);
    }

    // ---- The decision scan ----

    private static RunState Scanning() => State() with { WorkTurn = TurnKind.Scan };

    [Fact]
    public void TheScanBrief_GivesTheRequest_TheCategories_AndHowToSubmit()
    {
        var brief = Compose(BriefKind.Scan, Scanning(), kind: TurnKind.Scan);

        Assert.StartsWith("# Factory decision scan", brief);
        Assert.Contains("Add CSV export for Orders.", brief);
        Assert.Contains("You do not implement it.", brief);
        Assert.Contains("Do not edit files, and do not run builds or tests.", brief);
        foreach (var category in ChoiceCategories.All)
            Assert.Contains($"`{category}`", brief);
        Assert.Contains("List every open choice in the first five categories, even when you would recommend the obvious option", brief);
        Assert.Contains("await submit_plan(", brief);
        Assert.DoesNotContain("{{", brief);
    }

    [Fact]
    public void TheRunBriefAfterAScan_CarriesThePlan_TheAnswers_AndTheAssumptions()
    {
        var state = State() with
        {
            Plan = new PlanSubmission("Store expiry per put record.", ["src/LogFormat.cs"], []),
            Assumptions = ["On read or a sweep: On read"],
            Decisions = [new AnsweredDecision("What happens to version-1 files?", "Keep reading them.")],
        };

        var brief = Compose(BriefKind.Run, state);

        Assert.Contains("## The plan from the decision scan", brief);
        Assert.Contains("Store expiry per put record.", brief);
        Assert.Contains("- `src/LogFormat.cs`", brief);
        Assert.Contains("- On read or a sweep: On read", brief);
        Assert.Contains("**What happens to version-1 files?** — Keep reading them.", brief);
    }

    [Fact]
    public void TheRunBriefWithoutAScan_HasNoPlanSection() =>
        Assert.DoesNotContain("The plan from the decision scan", Compose(BriefKind.Run, State()));

    [Fact]
    public void TheReminderAndTheResumeOfAScan_NameSubmitPlan()
    {
        var nudge = Compose(BriefKind.Nudge, Scanning(), kind: TurnKind.Nudge);
        var resume = Compose(BriefKind.Resume, Scanning(), kind: TurnKind.Scan);

        Assert.Contains("You stopped without calling `submit_plan`.", nudge);
        Assert.DoesNotContain("request_decision", nudge);
        Assert.Contains("`submit_plan`", resume);
    }

    [Fact]
    public void ReviewBrief_SaysNotToRepeatTheFactorysVerification_ButAllowsRunningCodeForASpecificConcern()
    {
        var brief = Compose(BriefKind.Review, State(), kind: TurnKind.Review);

        Assert.Contains("The factory has already built the change and run its tests", brief);
        Assert.Contains("Run code of your own only when a specific concern cannot be settled by reading", brief);
        Assert.Contains("A review is not a second implementation.", brief);
    }

    // ---- The agent's account, and a light review ----

    private static RunState Submitted(WorkSubmission work) => State() with { LastSubmission = work };

    [Fact]
    public void ReviewBrief_CarriesTheAgentsAccount_AsClaimsToCheck()
    {
        var work = new WorkSubmission(
            "Added CSV export.",
            [new CriterionCoverage("Admins can export.", ["Export_Admin_Succeeds", "Export_Admin_Quotes"]), new CriterionCoverage("Looks right in Excel.", [], ManualOnly: true), new CriterionCoverage("Commas are quoted.", [])],
            ["Export_Admin_Succeeds"], ["Large exports are not streamed."], []);

        var brief = Compose(BriefKind.Review, Submitted(work), Context with { Diff = "+x", ChangedLineCount = 1 }, kind: TurnKind.Review);

        Assert.Contains("## The implementing agent's account (claims to check, not facts)", brief);
        Assert.Contains("Added CSV export.", brief);
        Assert.Contains("- Admins can export. — `Export_Admin_Succeeds`, `Export_Admin_Quotes`", brief);
        Assert.Contains("- Looks right in Excel. — manual testing only", brief);
        Assert.Contains("- Commas are quoted. — **no test named**", brief);
        Assert.Contains("- Large exports are not streamed.", brief);
        Assert.Contains("A criterion it calls covered may not be", brief);
        // The host's measurement comes first and is not mixed with the claims.
        Assert.True(brief.IndexOf("## Verification result", StringComparison.Ordinal) < brief.IndexOf("The implementing agent's account", StringComparison.Ordinal));
    }

    [Fact]
    public void ReviewBrief_WithNoSubmission_HasNoAccountSection()
    {
        var brief = Compose(BriefKind.Review, State(), Context with { Diff = "+x", ChangedLineCount = 1 }, kind: TurnKind.Review);

        Assert.DoesNotContain("implementing agent's account", brief);
        Assert.DoesNotContain("{{", brief);
    }

    [Fact]
    public void ALightReview_UsesItsOwnBrief_ThatForbidsReadingAndRunning()
    {
        var context = Context with { Diff = "+public void Export() { }", ChangedLineCount = 1, ReviewDepth = ReviewDepth.Light };

        var brief = Compose(BriefKind.Review, Submitted(new WorkSubmission("Added it.", [new("Admins can export.", ["T"])], ["T"], [], [])), context, kind: TurnKind.Review);

        Assert.Contains("# Factory review brief: a light review", brief);
        Assert.Contains("Do not read other files, list directories, search the code or run anything.", brief);
        Assert.Contains("call `submit_review` in your first reply", brief);
        Assert.Contains("```diff\n+public void Export() { }\n```", brief);
        Assert.Contains("Added it.", brief);
        Assert.Contains("## Verification result", brief);
        Assert.DoesNotContain("{{", brief);
    }

    // ---- The briefs name the completion tools' fields ----
    // From PTC kernel code the model sees only a tool's top-level signature, so the brief is the
    // only place it learns what goes inside a finding or a criterion.

    [Theory]
    [InlineData(ReviewDepth.Light)]
    [InlineData(ReviewDepth.Full)]
    public void ReviewBriefs_NameEachFindingsFields(ReviewDepth depth)
    {
        var brief = Compose(BriefKind.Review, State(), Context with { Diff = "+x", ChangedLineCount = 1, ReviewDepth = depth }, kind: TurnKind.Review);

        Assert.Contains("each finding has exactly these fields: `severity` (`\"blocking\"` or `\"minor\"`), `file`", brief);
        Assert.Contains("`text` (the finding, in one sentence)", brief);
        Assert.DoesNotContain("one-sentence description", brief);
    }

    [Fact]
    public void TheRunBrief_NamesSubmitWorksFields()
    {
        var brief = Compose(BriefKind.Run, State());

        Assert.Contains("using exactly these names", brief);
        foreach (var field in new[] { "`summary`", "`criteria`", "`testsAdded`", "`knownLimitations`", "`manualTestSteps`" })
            Assert.Contains(field, brief);
        Assert.Contains("`{ criterion, tests }`", brief);
        Assert.Contains("`{ criterion, manualOnly: true }`", brief);
    }

    [Fact]
    public void AFullReview_KeepsTheOpenBrief()
    {
        var brief = Compose(BriefKind.Review, State(), Context with { Diff = "+x", ChangedLineCount = 1 }, kind: TurnKind.Review);

        Assert.DoesNotContain("a light review", brief);
        Assert.Contains("Run code of your own only when a specific concern cannot be settled by reading", brief);
    }

    [Fact]
    public void ALightReviewOfARework_StillCarriesTheReworkScope()
    {
        var brief = Compose(BriefKind.Review, State(RunKind.Rework), ReworkReview with { ReviewDepth = ReviewDepth.Light }, kind: TurnKind.Review);

        Assert.Contains("# Factory review brief: a light review", brief);
        Assert.Contains("## Scope: a rework", brief);
    }

    // ---- The review of a rework covers the rework ----

    private static readonly RunContext ReworkReview = Context with
    {
        Diff = "+public string Quote(string v) => v;",
        ChangedLineCount = 1,
        ChangedFiles = ["src/OrdersCsvExporter.cs"],
        ReviewedThroughCommit = "1c9e2b4a77f0d3e5b6a8c9d0e1f2a3b4c5d6e7f8",
        TesterFeedback = "CSV values containing commas are incorrect.\nFix this.",
    };

    /// <summary>On the first real tasks, the review after a rework re-reviewed the whole task
    /// and cost more than the task's first implementation.</summary>
    [Fact]
    public void ReviewBrief_ForARework_SaysWhatWasAlreadyReviewed_AndWhatTheTesterAskedFor()
    {
        var brief = Compose(BriefKind.Review, State(RunKind.Rework), ReworkReview, kind: TurnKind.Review);

        Assert.Contains("## Scope: a rework", brief);
        Assert.Contains("reviewed and handed off at commit `1c9e2b4`", brief);
        Assert.Contains("> CSV values containing commas are incorrect.\n> Fix this.", brief);
        Assert.Contains("Raise something in the earlier work only if the rework breaks it.", brief);
        Assert.Contains("```diff\n+public string Quote(string v) => v;\n```", brief);
        Assert.DoesNotContain("{{", brief);
    }

    [Fact]
    public void ReviewBrief_ForAFirstRun_HasNoReworkScope()
    {
        var brief = Compose(BriefKind.Review, State(), Context with { Diff = "+x", ChangedLineCount = 1 }, kind: TurnKind.Review);

        Assert.DoesNotContain("Scope: a rework", brief);
        Assert.DoesNotContain("handed off at commit", brief);
    }

    [Fact]
    public void ReviewBrief_ForALargeRework_PointsAtTheHandoffCommit_NotTheBaseBranch()
    {
        var limits = new RunLimits { InlineReviewMaxChangedLines = 10 };

        var rework = Compose(BriefKind.Review, State(RunKind.Rework), ReworkReview with { ChangedLineCount = 11 }, limits, TurnKind.Review);
        var first = Compose(BriefKind.Review, State(), Context with { Diff = "+x", ChangedLineCount = 11, ChangedFiles = ["a.cs"] }, limits, TurnKind.Review);

        Assert.Contains("compare them with commit `1c9e2b4`", rework);
        Assert.Contains("- `src/OrdersCsvExporter.cs`", rework);
        Assert.Contains("compare them with `main`", first);
    }

    [Fact]
    public void ReworkBrief_MissingInputs_SayNoneRatherThanLeavingGaps()
    {
        var brief = Compose(BriefKind.Rework, State(RunKind.Rework), kind: TurnKind.Rework);

        Assert.Equal(3, brief.Split("(none)").Length - 1);
        Assert.DoesNotContain("{{", brief);
    }

    // ---- Repair brief ----

    [Fact]
    public void RepairBrief_NamesTheFailingTests_WithExcerpts_AndTheCycle()
    {
        var state = State() with
        {
            RepairCyclesUsed = 1,
            LastVerification = Failing(("Orders.Tests.Export_Commas", "Expected \"a,b\" but was a,b"), ("Orders.Tests.Export_Empty", "NullReferenceException")),
        };

        var brief = Compose(BriefKind.Repair, state, kind: TurnKind.Repair);

        Assert.Contains("repair cycle 1 of 2", brief);
        Assert.Contains("## Failing tests (2)", brief);
        Assert.Contains("### `Orders.Tests.Export_Commas`", brief);
        Assert.Contains("Expected \"a,b\" but was a,b", brief);
        Assert.Contains("### `Orders.Tests.Export_Empty`", brief);
        Assert.Contains("do not delete, skip or weaken a test", brief);
        Assert.DoesNotContain("last repair cycle", brief);
    }

    [Fact]
    public void RepairBrief_LastCycle_SaysSo()
    {
        var state = State() with { RepairCyclesUsed = 2, LastVerification = Failing(("T.A", "boom")) };

        Assert.Contains("This is the last repair cycle.", Compose(BriefKind.Repair, state, kind: TurnKind.Repair));
    }

    [Fact]
    public void RepairBrief_LeavesOutFailuresThatPredateTheTask()
    {
        var state = State() with
        {
            RepairCyclesUsed = 1,
            LastVerification = Failing(("Legacy.Broken", "old"), ("New.Broken", "new")),
            Baseline = Failing(("Legacy.Broken", "old")),
        };

        var brief = Compose(BriefKind.Repair, state, kind: TurnKind.Repair);

        Assert.Contains("## Failing tests (1)", brief);
        Assert.Contains("`New.Broken`", brief);
        Assert.DoesNotContain("Legacy.Broken", brief);
    }

    [Fact]
    public void RepairBrief_BoundsTheExcerptsToTheConfiguredTotal()
    {
        var limits = new RunLimits { RepairExcerptTokens = 100 }; // 400 characters in total
        var state = State() with
        {
            RepairCyclesUsed = 1,
            LastVerification = Failing(("T.A", new string('~', 5_000)), ("T.B", new string('^', 5_000))),
        };

        var brief = Compose(BriefKind.Repair, state, limits: limits, kind: TurnKind.Repair);

        // Neither character appears in the template, so every one counted is excerpt text.
        Assert.Equal(200, brief.Count(c => c == '~'));
        Assert.Equal(200, brief.Count(c => c == '^'));
        Assert.Contains("[truncated]", brief);
        Assert.Contains("`T.A`", brief);
        Assert.Contains("`T.B`", brief);
    }

    [Fact]
    public void BoundExcerpts_SharesTheBudgetEvenly_AndKeepsEveryName()
    {
        List<TestCaseResult> failing =
        [
            new("T.Short", TestOutcome.Failed, "short"),
            new("T.Long", TestOutcome.Failed, new string('x', 1_000)),
            new("T.None", TestOutcome.Failed),
        ];

        var bounded = BriefComposer.BoundExcerpts(failing, totalChars: 300);

        Assert.Equal(["T.Short", "T.Long", "T.None"], bounded.Select(b => b.Name));
        Assert.Equal("short", bounded[0].Excerpt);
        Assert.StartsWith(new string('x', 100), bounded[1].Excerpt);
        Assert.EndsWith("[truncated]", bounded[1].Excerpt);
        Assert.Equal("", bounded[2].Excerpt);
    }

    [Fact]
    public void BoundExcerpts_ZeroBudget_KeepsNamesWithEmptyExcerpts()
    {
        var bounded = BriefComposer.BoundExcerpts([new("T.A", TestOutcome.Failed, "message")], totalChars: 0);

        Assert.Equal(("T.A", ""), Assert.Single(bounded));
    }

    [Fact]
    public void RepairBrief_BuildFailure_SaysTheBuildFailed()
    {
        var state = State() with
        {
            RepairCyclesUsed = 1,
            LastVerification = new VerificationOutcome(BuildStatus.Failed, UnitTestStatus.NotRun, CoverageStatus.NotMeasured, [], null, [], []),
        };

        Assert.Contains("The build failed.", Compose(BriefKind.Repair, state, kind: TurnKind.Repair));
    }

    [Fact]
    public void RepairBrief_TestCommandWithNoReport_SaysSo()
    {
        var state = State() with
        {
            RepairCyclesUsed = 1,
            LastVerification = new VerificationOutcome(
                BuildStatus.Passed, UnitTestStatus.Failed, CoverageStatus.NotMeasured, [], null, [],
                ["Step 'api': unit tests failed with exit code 1 (no report)."]),
        };

        var brief = Compose(BriefKind.Repair, state, kind: TurnKind.Repair);

        Assert.Contains("no failing test could be read from its report", brief);
        Assert.Contains("- Step 'api': unit tests failed with exit code 1 (no report).", brief);
    }

    [Fact]
    public void RepairBrief_CoverageBelowThreshold_ListsTheUncoveredChangedLines()
    {
        var state = State() with
        {
            RepairCyclesUsed = 1,
            LastVerification = new VerificationOutcome(
                BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.BelowThreshold, [],
                new ChangedLineCoverage(5, 10, [new UncoveredLines("src/Orders.cs", [3, 4, 5, 9, 12, 13])]), [], []),
        };

        var brief = Compose(BriefKind.Repair, state, kind: TurnKind.Repair);

        Assert.Contains("50% of the lines you changed are covered", brief);
        Assert.Contains("the project requires 80%", brief);
        Assert.Contains("- `src/Orders.cs`: lines 3-5, 9, 12-13", brief);
        Assert.DoesNotContain("Failing tests", brief);
    }

    [Theory]
    [InlineData(new[] { 1 }, "1")]
    [InlineData(new[] { 1, 2, 3 }, "1-3")]
    [InlineData(new[] { 5, 1, 2, 9, 10, 2 }, "1-2, 5, 9-10")]
    [InlineData(new int[0], "")]
    public void FormatLines_CollapsesRuns(int[] lines, string expected)
    {
        Assert.Equal(expected, BriefComposer.FormatLines(lines));
    }

    [Fact]
    public void RepairBrief_ForReviewFindings_ListsOnlyTheBlockingOnes_AndNoStaleTestFailures()
    {
        var state = State() with
        {
            RepairCyclesUsed = 1,
            ReviewRepairPending = true,
            LastVerification = Failing(("Stale.Failure", "from before the review")),
            Findings =
            [
                new ReviewFinding(FindingSeverity.Blocking, "src/Orders.cs", 42, "Crashes on an empty list."),
                new ReviewFinding(FindingSeverity.Blocking, "src/Auth.cs", null, "Non-administrators are not refused."),
                new ReviewFinding(FindingSeverity.Minor, "src/Orders.cs", 7, "Leftover Console.WriteLine."),
            ],
        };

        var brief = Compose(BriefKind.Repair, state, kind: TurnKind.Repair);

        Assert.Contains("## Blocking review findings", brief);
        Assert.Contains("- `src/Orders.cs:42` — Crashes on an empty list.", brief);
        Assert.Contains("- `src/Auth.cs` — Non-administrators are not refused.", brief);
        Assert.DoesNotContain("Leftover Console.WriteLine", brief);
        Assert.DoesNotContain("Stale.Failure", brief);
    }

    // ---- Review brief ----

    [Fact]
    public void ReviewBrief_SmallChange_IncludesTheDiffTheCriteriaAndTheVerificationResult()
    {
        var context = Context with { Diff = "+public void Export() { }", ChangedLineCount = 1 };
        var state = State() with
        {
            LastVerification = new VerificationOutcome(
                BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.Met,
                [new("T.A", TestOutcome.Passed), new("T.B", TestOutcome.Passed), new("T.C", TestOutcome.Skipped)],
                new ChangedLineCoverage(91, 100, []), [], []),
        };

        var brief = Compose(BriefKind.Review, state, context, kind: TurnKind.Review);

        Assert.Contains("```diff\n+public void Export() { }\n```", brief);
        Assert.Contains("1. Administrators can export.", brief);
        Assert.Contains("- Build: Passed", brief);
        Assert.Contains("- Unit tests: Passed (2 passed, 0 failed, 1 skipped)", brief);
        Assert.Contains("- Changed-line coverage: Met (91%)", brief);
        Assert.Contains("read-only tools", brief);
        Assert.Contains("`submit_review`", brief);
    }

    [Fact]
    public void ReviewBrief_AboveTheInlineLimit_GivesTheFileListInsteadOfTheDiff()
    {
        var context = Context with
        {
            Diff = "+a very large diff",
            ChangedLineCount = 1_501,
            ChangedFiles = ["src/A.cs", "src/B.cs"],
        };

        var brief = Compose(BriefKind.Review, State(), context, kind: TurnKind.Review);

        Assert.DoesNotContain("a very large diff", brief);
        Assert.Contains("1501 lines", brief);
        Assert.Contains("- `src/A.cs`", brief);
        Assert.Contains("- `src/B.cs`", brief);
    }

    [Fact]
    public void ReviewBrief_ExactlyAtTheInlineLimit_StillIncludesTheDiff()
    {
        var context = Context with { Diff = "+the diff", ChangedLineCount = 1_500 };

        Assert.Contains("+the diff", Compose(BriefKind.Review, State(), context, kind: TurnKind.Review));
    }

    [Fact]
    public void ReviewBrief_NoVerificationYet_SaysNotRun()
    {
        Assert.Contains("(not run)", Compose(BriefKind.Review, State(), Context with { Diff = "+x", ChangedLineCount = 1 }, kind: TurnKind.Review));
    }

    // ---- Nudge, decision answer, proceed, resume ----

    [Fact]
    public void Nudge_ForAWorkTurn_NamesSubmitWorkAndRequestDecision()
    {
        var nudge = Compose(BriefKind.Nudge, State() with { WorkTurn = TurnKind.Implement }, kind: TurnKind.Nudge);

        Assert.Contains("You stopped without calling `submit_work` or `request_decision`.", nudge);
        Assert.Contains("call `request_decision`", nudge);
    }

    [Fact]
    public void Nudge_ForAReviewTurn_NamesOnlySubmitReview()
    {
        var nudge = Compose(BriefKind.Nudge, State() with { WorkTurn = TurnKind.Review }, kind: TurnKind.Nudge);

        Assert.Contains("You stopped without calling `submit_review`.", nudge);
        Assert.DoesNotContain("request_decision", nudge);
        Assert.DoesNotContain("submit_work", nudge);
    }

    [Fact]
    public void DecisionAnswer_QuotesTheLatestQuestionAndAnswer()
    {
        var state = State() with
        {
            Decisions = [new AnsweredDecision("First?", "One."), new AnsweredDecision("All rows or\nthe current page?", "All filtered rows.")],
        };

        var brief = Compose(BriefKind.DecisionAnswer, state);

        Assert.Contains("> All rows or\n> the current page?", brief);
        Assert.Contains("> All filtered rows.", brief);
        Assert.DoesNotContain("First?", brief);
    }

    [Fact]
    public void DecisionAnswer_WithNoAnsweredDecision_IsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => Compose(BriefKind.DecisionAnswer, State()));
    }

    [Fact]
    public void ProceedOnRecommendation_StatesTheLimit_AndAsksForTheAssumptionToBeDisclosed()
    {
        var brief = Compose(BriefKind.ProceedOnRecommendation, State(), limits: new RunLimits { MaxDecisions = 3 }, kind: TurnKind.Nudge);

        Assert.Contains("already asked the 3 questions", brief);
        Assert.Contains("Proceed on your own recommendation.", brief);
        Assert.Contains("known limitations", brief);
    }

    [Fact]
    public void Resume_SaysWhyTheRunStopped_AndWhichToolFinishesIt()
    {
        var state = State() with
        {
            LastStop = new StopStep(LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted, "The token allowance ran out."),
        };

        var brief = Compose(BriefKind.Resume, state);

        Assert.Contains("It had stopped because: The token allowance ran out.", brief);
        Assert.Contains("Check the current state of the working copy", brief);
        Assert.Contains("`submit_work`", brief);
    }

    [Fact]
    public void AFollowUp_ReachesTheAgentAsAnAside_ToAnswerWithoutStoppingTheWork()
    {
        var text = BriefComposer.FollowUp("  what are you working on now?  ");

        Assert.Contains("\n\nwhat are you working on now?\n\n", text);
        Assert.Contains("keep working in the same turn", text);
        Assert.Contains("submit_work or request_decision", text);
        Assert.Contains("Do not stop the work just to answer.", text);
    }

    [Fact]
    public void Continue_AfterAnsweringAFollowUp_SaysTheAnswerIsPosted_AndWhichToolFinishes()
    {
        var brief = Compose(BriefKind.Continue, State());

        Assert.Contains("Your answer to the person has been posted on the thread.", brief);
        Assert.Contains("`submit_work`", brief);
    }

    [Fact]
    public void Resume_WithAnUnchangedWorkingCopy_SaysNothingAboutDrift()
    {
        var brief = Compose(BriefKind.Resume, State(), Context with { WorkspaceDrift = "" });

        Assert.DoesNotContain("last checkpoint", brief);
        Assert.DoesNotContain("\n\n\n", brief);
    }

    [Fact]
    public void Resume_WithAChangedWorkingCopy_ListsTheChanges_BeforeAskingForACheck()
    {
        var context = Context with { WorkspaceDrift = "- Changed since then: `src/Orders.cs`." };

        var brief = Compose(BriefKind.Resume, State(), context);

        Assert.Contains("compared the working copy with the run's last checkpoint", brief);
        Assert.Contains("- Changed since then: `src/Orders.cs`.", brief);
        Assert.True(brief.IndexOf("src/Orders.cs", StringComparison.Ordinal) < brief.IndexOf("Check the current state", StringComparison.Ordinal));
    }

    [Fact]
    public void Resume_OfAReview_FinishesWithSubmitReview()
    {
        Assert.Contains("`submit_review`", Compose(BriefKind.Resume, State() with { WorkTurn = TurnKind.Review }, kind: TurnKind.Review));
    }

    // ---- Templates and revision ----

    [Fact]
    public void Compose_UnknownBriefKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Compose((BriefKind)999, State()));
    }

    [Fact]
    public void EveryBriefKind_Composes_WithNoPlaceholderLeftBehind()
    {
        var state = State() with { Decisions = [new AnsweredDecision("Q?", "A.")] };

        foreach (var kind in Enum.GetValues<BriefKind>())
        {
            var brief = Compose(kind, state);
            Assert.False(string.IsNullOrWhiteSpace(brief));
            Assert.DoesNotMatch(@"\{\{\w+\}\}", brief);
        }
    }

    [Fact]
    public void CompactionInstruction_SaysWhatMustSurvive()
    {
        var instruction = BriefComposer.CompactionInstruction();

        Assert.Contains("acceptance criterion", instruction);
        Assert.Contains("decision", instruction);
        Assert.Contains("file changed", instruction);
        Assert.Contains("outstanding", instruction);
        Assert.Contains("Drop raw tool output", instruction);
    }

    [Fact]
    public void Revision_IsSet_SoARunCanRecordWhichPromptsItUsed()
    {
        Assert.False(string.IsNullOrWhiteSpace(BriefComposer.Revision));
        // m1.2: economy guidance, the contract restated in rework and repair briefs, and the
        // review of a rework scoped to the rework. Bump it with every change to a brief.
        // m1.3: never stop processes by name.
        // m1.11: the decision scan brief, and the plan section of the run brief.
        // m2.1: the resume brief states how the working copy changed since the last checkpoint.
        // m2.2: the chat brief, for a read-only answer to a plain message.
        // m2.3: the spec brief; approved criteria reported on in their own words.
        Assert.Equal("m2.3", BriefComposer.Revision);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("rework")]
    [InlineData("repair")]
    [InlineData("review")]
    [InlineData("review-light")]
    [InlineData("scan")]
    [InlineData("nudge")]
    [InlineData("decision-answer")]
    [InlineData("proceed")]
    [InlineData("resume")]
    [InlineData("compaction")]
    [InlineData("chat")]
    [InlineData("spec")]
    public void Templates_AreEmbedded(string name)
    {
        Assert.False(string.IsNullOrWhiteSpace(BriefComposer.LoadTemplate(name)));
    }

    [Fact]
    public void LoadTemplate_Unknown_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => BriefComposer.LoadTemplate("no-such-template"));
    }

    [Fact]
    public void Render_PlaceholderWithNoValue_IsAnError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BriefComposer.Render("proceed", []));

        Assert.Contains("maxDecisions", ex.Message);
    }

    [Fact]
    public void Render_ValueWithNoPlaceholder_IsAnError()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BriefComposer.Render("proceed", new() { ["maxDecisions"] = "3", ["typo"] = "x" }));

        Assert.Contains("typo", ex.Message);
    }

    [Fact]
    public void Composing_IsDeterministic()
    {
        var state = State() with { Baseline = Failing(("B.Test", "x"), ("A.Test", "y")) };

        Assert.Equal(Compose(BriefKind.Run, state), Compose(BriefKind.Run, state));
    }

    [Fact]
    public void TheRecheckBrief_ListsTheAnswersAndTheQuestionsNotYetAsked_AndAsksForExactQuotes()
    {
        var state = RunOrchestrator.NewRun(RunKind.Implement) with
        {
            Decisions = [new AnsweredDecision("What does the user see?", "Nothing partial is shown.")],
            PendingQuestions = [new OpenChoice("Is a partial response kept?", ChoiceCategories.ExistingData, ["Discard it", "Keep it"], "Discard it")],
        };

        var brief = BriefComposer.Compose(new StartTurnStep(TurnKind.Scan, BriefKind.ScanRecheck, SessionScope.Scan), new RunContext("salesapp", "factory/x", "main", "Add cancel."), state, new RunLimits());

        Assert.StartsWith("# Factory decision scan: re-check the remaining questions", brief);
        Assert.Contains("Nothing partial is shown.", brief);
        Assert.Contains("1. Is a partial response kept? (category `existing-data`; options: Discard it / Keep it)", brief);
        Assert.Contains("**quoted exactly**", brief);
        Assert.Contains("Do not add new questions", brief);
    }

    // ---- Chat (m2-architecture.md §5) ----

    private static ChatContext Chat(IReadOnlyList<ChatLine>? conversation = null, string question = "Where are orders exported?") => new(
        "SalesApp", "the default branch `main`", "Add CSV export", "Add CSV export for Orders.", conversation ?? [], question);

    [Fact]
    public void Chat_StatesTheProjectTheTaskWhereItReads_AndTheQuestion()
    {
        var brief = BriefComposer.Chat(Chat());

        Assert.Contains("**SalesApp**", brief);
        Assert.Contains("the default branch `main`", brief);
        Assert.Contains("**Add CSV export**", brief);
        Assert.Contains("Add CSV export for Orders.", brief);
        Assert.Contains("> Where are orders exported?", brief);
    }

    [Fact]
    public void Chat_SaysItIsReadOnly_AndThatOnlyAtFactoryStartsWork()
    {
        var brief = BriefComposer.Chat(Chat());

        Assert.Contains("read-only tools", brief);
        Assert.Contains("cannot edit files", brief);
        Assert.Contains("`@factory`", brief);
        Assert.Contains("Your last message is posted to the thread as your answer", brief);
    }

    [Fact]
    public void Chat_WithNoEarlierMessages_HasNoThreadSection_AndATaskWithNoRequestSaysNone()
    {
        var brief = BriefComposer.Chat(Chat() with { Request = null });

        Assert.DoesNotContain("The thread so far", brief);
        Assert.Contains("(none)", brief);
        Assert.DoesNotContain("{{", brief);
    }

    [Fact]
    public void Chat_CarriesTheThreadSoFar_InOrder_WithWhoSaidIt()
    {
        var brief = BriefComposer.Chat(Chat([new ChatLine("Person", "Is there an export already?"), new ChatLine("Factory", "No.\nOnly an import.")]));

        Assert.Contains("## The thread so far", brief);
        var person = brief.IndexOf("**Person:**\n> Is there an export already?", StringComparison.Ordinal);
        var factory = brief.IndexOf("**Factory:**\n> No.\n> Only an import.", StringComparison.Ordinal);
        Assert.True(person >= 0 && factory > person, brief);
    }

    [Fact]
    public void Chat_KeepsOnlyTheNewestMessages_AndSaysHowManyItLeftOut()
    {
        var lines = Enumerable.Range(1, BriefComposer.ChatConversationMessages + 5).Select(i => new ChatLine("Person", $"message {i}.")).ToList();

        var brief = BriefComposer.Chat(Chat(lines));

        Assert.Contains("(5 earlier message(s) not shown.)", brief);
        Assert.DoesNotContain("message 5.", brief);
        Assert.Contains("message 6.", brief);
        Assert.Contains($"message {BriefComposer.ChatConversationMessages + 5}.", brief);
    }

    [Fact]
    public void Chat_CutsALongEarlierMessage()
    {
        var brief = BriefComposer.Chat(Chat([new ChatLine("Factory", new string('x', BriefComposer.ChatMessageChars + 100))]));

        Assert.Contains(new string('x', BriefComposer.ChatMessageChars) + " [...]", brief);
        Assert.DoesNotContain(new string('x', BriefComposer.ChatMessageChars + 1), brief);
    }

    [Fact]
    public void Chat_AMultiLineQuestion_StaysInsideTheQuote()
    {
        var brief = BriefComposer.Chat(Chat(question: "First line.\nSecond line."));

        Assert.Contains("> First line.\n> Second line.", brief);
    }

    // ---- Spec (m2-architecture.md §5) ----

    private static readonly SpecDraft Draft = new(
        2, "Administrators can export orders as CSV.", ["An Export button is shown to administrators.", "Others get a 403."],
        ["src/Orders/OrdersController.cs"], "Unit tests cover the 403; the button is checked by hand.", ["Include cancelled orders?"]);

    private static SpecContext SpecFor(SpecDraft? previous = null, IReadOnlyList<ChatLine>? conversation = null) => new(
        "SalesApp", "the default branch `main`", "Add CSV export", "Export the orders list.", previous, conversation ?? []);

    [Fact]
    public void Spec_StatesTheTaskWhatWasAsked_AndThatNothingIsBuiltYet()
    {
        var brief = BriefComposer.Spec(SpecFor());

        Assert.Contains("**SalesApp**", brief);
        Assert.Contains("**Add CSV export**", brief);
        Assert.Contains("> Export the orders list.", brief);
        Assert.Contains("the default branch `main`", brief);
        Assert.Contains("You do not implement it.", brief);
        Assert.Contains("`submit_spec`", brief);
        Assert.DoesNotContain("The draft to revise", brief);
        Assert.DoesNotContain("{{", brief);
    }

    [Fact]
    public void Spec_ARevision_CarriesTheDraftItRevises_InFull()
    {
        var brief = BriefComposer.Spec(SpecFor(Draft));

        Assert.Contains("## The draft to revise (revision 2)", brief);
        Assert.Contains("Keep what the request does not ask to change.", brief);
        Assert.Contains("**Summary.** Administrators can export orders as CSV.", brief);
        Assert.Contains("1. An Export button is shown to administrators.\n2. Others get a 403.", brief);
        Assert.Contains("- src/Orders/OrdersController.cs", brief);
        Assert.Contains("**Test plan.** Unit tests cover the 403", brief);
        Assert.Contains("- Include cancelled orders?", brief);
    }

    [Fact]
    public void Spec_CarriesTheThreadSoFar()
    {
        var brief = BriefComposer.Spec(SpecFor(conversation: [new ChatLine("Person", "Only admins should export.")]));

        Assert.Contains("## The thread so far", brief);
        Assert.Contains("**Person:**\n> Only admins should export.", brief);
    }

    [Fact]
    public void DescribeSpec_LeavesOutEmptySections()
    {
        var text = BriefComposer.DescribeSpec(Draft with { AffectedAreas = [], OpenQuestions = [], TestPlan = " " });

        Assert.DoesNotContain("Affected areas", text);
        Assert.DoesNotContain("Open questions", text);
        Assert.DoesNotContain("Test plan", text);
    }

    [Fact]
    public void ARunWithApprovedCriteria_IsToldToReportOnEachInItsOwnWords()
    {
        var brief = BriefComposer.Compose(
            new StartTurnStep(TurnKind.Implement, BriefKind.Run, SessionScope.Thread), Context, State(), Limits);

        Assert.Contains("1. Administrators can export.", brief);
        Assert.Contains("each in exactly the words written here", brief);
    }

    [Fact]
    public void ARunWithoutCriteria_IsNotToldAboutThem()
    {
        var brief = BriefComposer.Compose(
            new StartTurnStep(TurnKind.Implement, BriefKind.Run, SessionScope.Thread), Context with { AcceptanceCriteria = [] }, State(), Limits);

        Assert.DoesNotContain("each in exactly the words written here", brief);
    }
}
