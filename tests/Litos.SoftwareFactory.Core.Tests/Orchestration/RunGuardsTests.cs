using System.Text.Json;
using Litos.Agent.Messages;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// The W3.1 run guards (Probe.Web README §11.3 findings 2, 11 and 15). On the W2.7 check a
/// Make-testable turn made 29 calls and a feature turn 28 without changing the working copy,
/// a turn that had already done its work explored until the budget ran out, and a verified
/// change waited for a person because a light review was 104 tokens short.
/// </summary>
public class RunGuardsTests
{
    private static readonly RunLimits Limits = new();

    private static ChatMessage Kernel(string code, string id = "k") =>
        new(Role.Assistant, [new ToolUseBlock(id, "run_kernel_code", JsonSerializer.SerializeToElement(new { code }))]);

    private static ChatMessage Tool(string name, object args, string id = "t") =>
        new(Role.Assistant, [new ToolUseBlock(id, name, JsonSerializer.SerializeToElement(args))]);

    private static ChatMessage Result(string id = "k") => new(Role.User, [new ToolResultBlock(id, "ok")]);

    private static IEnumerable<ChatMessage> Reads(int calls) =>
        Enumerable.Range(0, calls).SelectMany(i => new[] { Kernel($"Console.WriteLine(await read_file(\"path\", \"f{i}.cs\"));", $"r{i}"), Result($"r{i}") });

    // ---- What counts as a change to the working copy ----

    [Theory]
    [InlineData("await write_file(\"path\", \"litos.test.yaml\", \"content\", yaml);")]
    [InlineData("await edit_file(\"path\",\"Program.cs\", \"old_text\", a, \"new_text\", b);")]
    [InlineData("File.WriteAllText(\"Tests/Probe.cs\", text);")]
    [InlineData("File.Delete(\"factorycheck.bat\");")]
    [InlineData("Console.WriteLine(await shell(\"command\", \"git rm -r --cached artifacts\"));")]
    public void KernelCodeThatChangesTheWorkingCopy_IsAWrite(string code) =>
        Assert.True(ProgressGuard.Writes((ToolUseBlock)Kernel(code).Content[0]));

    [Theory]
    [InlineData("var d = Path.Combine(SCRATCH_DIR, \"exp1\"); File.WriteAllText(Path.Combine(d, \"App.csproj\"), x); // SCRATCH_DIR")]
    [InlineData("File.WriteAllText(Path.Combine(SCRATCH_DIR, \"csproj.bak\"), csproj);")]
    [InlineData("Console.WriteLine(await read_file(\"path\", \"Program.cs\"));")]
    [InlineData("Console.WriteLine(await shell(\"command\", \"dotnet test\"));")]
    public void ReadingTestingOrWritingToScratch_IsNotAWrite(string code) =>
        Assert.False(ProgressGuard.Writes((ToolUseBlock)Kernel(code).Content[0]));

    [Fact]
    public void TheFileToolsCalledDirectly_AreWrites()
    {
        Assert.True(ProgressGuard.Writes((ToolUseBlock)Tool("write_file", new { path = "a.cs", content = "x" }).Content[0]));
        Assert.True(ProgressGuard.Writes((ToolUseBlock)Tool("edit_file", new { path = "a.cs", old_text = "x", new_text = "y" }).Content[0]));
        Assert.False(ProgressGuard.Writes((ToolUseBlock)Tool("read_file", new { path = "a.cs" }).Content[0]));
    }

    [Fact]
    public void TheCount_IsTheCallsSinceTheLastWrite_IncludingTheReplyJustReceived()
    {
        List<ChatMessage> conversation = [ChatMessage.User("brief"), .. Reads(3), Kernel("await write_file(\"path\",\"a.cs\",\"content\",\"x\");", "w"), Result("w"), .. Reads(4)];

        Assert.Equal(4, ProgressGuard.CallsSinceLastWrite(conversation));
        Assert.Equal(0, ProgressGuard.CallsSinceLastWrite([.. conversation, Kernel("File.WriteAllText(\"b.cs\", x);", "w2")]));
        Assert.Equal(7, ProgressGuard.CallsSinceLastWrite([ChatMessage.User("brief"), .. Reads(7)]));
    }

    [Fact]
    public void AReplyWithoutToolCalls_IsNotCounted() =>
        Assert.Equal(2, ProgressGuard.CallsSinceLastWrite([.. Reads(2), ChatMessage.Assistant([new TextBlock("Thinking it over.")])]));

    // ---- The verdict ----

    [Fact]
    public void BelowTheNoteLimit_TheTurnCarriesOn() =>
        Assert.Equal(ProgressGuard.Verdict.Continue, ProgressGuard.Judge(14, noteSent: false, Limits));

    [Fact]
    public void AtTheNoteLimit_TheTurnIsToldOnce_ThenCarriesOnUntilTheStopLimit()
    {
        Assert.Equal(ProgressGuard.Verdict.Note, ProgressGuard.Judge(15, noteSent: false, Limits));
        Assert.Equal(ProgressGuard.Verdict.Continue, ProgressGuard.Judge(20, noteSent: true, Limits));
        Assert.Equal(ProgressGuard.Verdict.Stop, ProgressGuard.Judge(25, noteSent: true, Limits));
    }

    [Fact]
    public void BothStalledTurnsOfTheW27Check_AreStoppedBeforeTheyRanOut() =>
        Assert.All(new[] { 28, 29 }, calls => Assert.Equal(ProgressGuard.Verdict.Stop, ProgressGuard.Judge(calls, noteSent: true, Limits)));

    [Fact]
    public void TheHealthiestLongGapSeen_IsOnlyNoted() =>
        Assert.Equal(ProgressGuard.Verdict.Note, ProgressGuard.Judge(16, noteSent: false, Limits));

    [Fact]
    public void WithTheGuardsOff_NothingIsSaidOrStopped()
    {
        var off = Limits with { RunGuards = false };
        Assert.Equal(ProgressGuard.Verdict.Continue, ProgressGuard.Judge(40, noteSent: false, off));
        Assert.False(BudgetNote.IsDue(1, sent: false, off));
    }

    [Fact]
    public void TheNoteSaysWhatToDo_AndWhenTheTurnWillBeStopped()
    {
        var note = ProgressGuard.Note(15, Limits);
        Assert.Contains("15 model calls without changing a file in the working copy", note);
        Assert.Contains("request_decision", note);
        Assert.Contains("After 25 calls", note);
        Assert.Contains("Resume to continue", ProgressGuard.StopReason(25));
    }

    // ---- The budget-low note ----

    [Theory]
    [InlineData(50_000L, 10_000L, 5L)]
    [InlineData(12_889L, 13_000L, 0L)]
    [InlineData(-5L, 1_000L, 0L)]
    public void CallsLeft_IsTheRemainingBudgetOverTheLastCallsCost(long remaining, long charged, long expected) =>
        Assert.Equal(expected, BudgetNote.CallsLeft(remaining, charged));

    [Fact]
    public void NoCap_OrAFreeCall_MeansNoEstimate()
    {
        Assert.Null(BudgetNote.CallsLeft(null, 5_000));
        Assert.Null(BudgetNote.CallsLeft(10_000, 0));
    }

    [Fact]
    public void TheBudgetNote_IsDueOnceWhenFewCallsAreLeft()
    {
        Assert.False(BudgetNote.IsDue(6, sent: false, Limits));
        Assert.True(BudgetNote.IsDue(5, sent: false, Limits));
        Assert.False(BudgetNote.IsDue(2, sent: true, Limits));
        Assert.False(BudgetNote.IsDue(null, sent: false, Limits));
        Assert.Contains("call submit_work", BudgetNote.Note(41_000, 5));
    }

    [Fact]
    public void AdmittingACall_CarriesWhatTheTasksBudgetHadLeft()
    {
        var policy = new BudgetPolicy();
        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(new BudgetSnapshot(300_000, 190_000, 10_000), 5_000, policy));
        Assert.Equal(100_000, admitted.TaskRemaining);
        Assert.Null(Assert.IsType<Admitted>(BudgetLedger.Admit(new BudgetSnapshot(null, 0, 0), 5_000, policy)).TaskRemaining);
    }

    // ---- The orchestrator ----

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private sealed class Run
    {
        private readonly RunOrchestrator _orchestrator = new();

        public RunState State { get; private set; } = RunOrchestrator.NewRun(RunKind.Implement);

        public RunStep Report(StepOutcome outcome)
        {
            var transition = _orchestrator.Next(State, outcome, T0);
            State = transition.State;
            return transition.Step;
        }

        public RunStep Implementing()
        {
            Report(new RunStarted());
            return Report(new PreflightCompleted(true));
        }

        public RunStep AtReview()
        {
            Implementing();
            Report(new TurnEnded(TurnEndReason.Completed, new WorkSubmission("done", [], [], [], []), FilesChanged: true));
            return Report(new Verified(new VerificationOutcome(BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.NotMeasured, [], null, [], [])));
        }
    }

    [Fact]
    public void AWorkTurnStoppedForNoProgress_IsBlocked_AndResumesTheSameTurn()
    {
        var run = new Run();
        run.Implementing();

        var step = Assert.IsType<StopStep>(run.Report(new TurnEnded(TurnEndReason.NoProgress, Detail: ProgressGuard.StopReason(25))));

        Assert.Equal(StopReason.NoProgress, step.Reason);
        Assert.Contains("25 model calls without changing a file", step.Message);
        var resume = Assert.IsType<StartTurnStep>(run.State.ResumePoint);
        Assert.Equal((TurnKind.Implement, BriefKind.Resume), (resume.Kind, resume.Brief));
    }

    [Fact]
    public void AReviewWithNoBudgetLeft_HandsOff_WithTheReviewNotRunAndSaysWhy()
    {
        var run = new Run();
        run.AtReview();

        var next = run.Report(new TurnEnded(TurnEndReason.BudgetRefused, Detail: "The next model call needs about 12,993 tokens, but the task's budget has 12,889 left."));

        Assert.IsType<HandoffStep>(next);
        Assert.Equal(ReviewStatus.SkippedForBudget, run.State.Review);
        var disclosure = Assert.Single(run.State.Disclosures);
        Assert.StartsWith("The agent review was not run: there was no budget left for it once the factory's own verification had passed", disclosure);
        Assert.Contains("12,889 left", disclosure);
        Assert.Contains("Review the change yourself", disclosure);
    }

    [Fact]
    public void AWorkTurnWithNoBudgetLeft_StillPausesForBudget()
    {
        var run = new Run();
        run.Implementing();

        var step = Assert.IsType<StopStep>(run.Report(new TurnEnded(TurnEndReason.BudgetRefused, Detail: "no room")));

        Assert.Equal(StopReason.BudgetExhausted, step.Reason);
    }
}
