using System.Text.Json;
using Litos.Agent.Messages;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// The live cost note for implementation turns. On R3 the context grew from 6,000 to 115,000
/// tokens over 69 calls and 61% of the implementation was that context re-read; brief wording
/// had not changed how much the agent read or how many calls it made.
/// </summary>
public class CostMeterTests
{
    private static readonly RunLimits Limits = new();

    private static (bool Due, CostMeterState Next) Due(int calls, long context, CostMeterState? state = null, RunLimits? limits = null)
    {
        var due = CostMeter.IsDue(calls, context, state ?? new CostMeterState(), limits ?? Limits, out var next);
        return (due, next);
    }

    [Fact]
    public void ASmallTurn_GetsNoNote()
    {
        for (var calls = 1; calls < 15; calls++)
            Assert.False(Due(calls, 30_000).Due);
    }

    [Fact]
    public void ANoteComesEvery15Calls()
    {
        var first = Due(15, 30_000);
        Assert.True(first.Due);
        Assert.Equal(new CostMeterState(15, 0), first.Next);

        Assert.False(Due(29, 40_000, first.Next).Due);
        Assert.True(Due(30, 40_000, first.Next).Due);
    }

    [Fact]
    public void ANoteComesWhenTheContextFirstPassesEachThreshold()
    {
        var at50 = Due(8, 51_000);
        Assert.True(at50.Due);
        Assert.Equal(new CostMeterState(8, 1), at50.Next);

        // The same threshold is not reported twice.
        Assert.False(Due(14, 60_000, at50.Next).Due);
        // The next one is.
        var at80 = Due(14, 81_000, at50.Next);
        Assert.True(at80.Due);
        Assert.Equal(2, at80.Next.ThresholdsReported);
    }

    /// <summary>A context that grows fast still gets at most one note every five calls.</summary>
    [Fact]
    public void NotesAreNeverCloserThanFiveCalls()
    {
        var first = Due(8, 51_000);

        Assert.False(Due(10, 85_000, first.Next).Due);
        Assert.True(Due(13, 85_000, first.Next).Due);
    }

    [Fact]
    public void WithCostNotesOff_NothingIsEverDue() =>
        Assert.False(Due(30, 120_000, limits: Limits with { CostNotes = false }).Due);

    // ---- What fills the context ----

    private static ChatMessage Script(string id, string code) =>
        new(Role.Assistant, [new ToolUseBlock(id, "run_kernel_code", JsonSerializer.SerializeToElement(new { code }))]);

    private static ChatMessage Result(string id, int chars) => new(Role.User, [new ToolResultBlock(id, new string('x', chars))]);

    [Fact]
    public void Composition_SortsToolOutputByKind_LargestFirst()
    {
        ChatMessage[] messages =
        [
            ChatMessage.User("brief"),
            Script("1", "Console.WriteLine(System.IO.File.ReadAllText(\"src/App.tsx\"));"), Result("1", 40_000),
            Script("2", "Console.WriteLine(await shell(\"command\", \"npx vitest run\"));"), Result("2", 12_000),
            Script("3", "Console.WriteLine(await read_file(\"path\", \"src/api.ts\"));"), Result("3", 8_000),
            Script("4", "Console.WriteLine(await shell(\"command\", \"git status\"));"), Result("4", 400),
            Script("5", "await edit_file(\"path\", \"a.ts\", \"old\", \"x\", \"new\", \"y\");"), Result("5", 100),
            new(Role.Assistant, [new ToolUseBlock("6", "search_code", JsonSerializer.SerializeToElement(new { query = "x" }))]), Result("6", 4_000),
        ];

        var composition = CostMeter.Composition(messages);

        Assert.Equal(
            [new ContextShare(CostMeter.WholeFiles, 10_000), new ContextShare(CostMeter.TestRuns, 3_000), new ContextShare(CostMeter.FileTools, 3_000),
             new ContextShare(CostMeter.Commands, 100), new ContextShare(CostMeter.Other, 25)],
            composition);
    }

    [Fact]
    public void TheNote_GivesTheRealFigures_SaysItIsNotAStop_AndWhatToDoDifferently()
    {
        var note = CostMeter.Note(30, 62_000, 7_840, [new ContextShare(CostMeter.WholeFiles, 24_500), new ContextShare(CostMeter.TestRuns, 9_800), new ContextShare(CostMeter.Other, 200)]);

        Assert.StartsWith("Cost note from the factory. This is information, not a request to stop. This turn has made 30 model calls.", note);
        Assert.Contains("now about 62,000 tokens, so the last call cost about 7,840 tokens", note);
        Assert.Contains("What is filling it: whole files printed from kernel code, about 24,500 tokens; test-run output, about 9,800 tokens.", note);
        Assert.DoesNotContain("other tool output", note);
        Assert.Contains("read only the lines you need", note);
        Assert.Contains("put your next edits and the test run in one script", note);
        Assert.EndsWith("Carry on with the task.", note);
    }

    [Fact]
    public void TheThreadLine_NamesTheLargestPart()
    {
        Assert.Equal(
            "Cost note sent to the agent after 30 model calls: its context is about 62,000 tokens, the largest part whole files printed from kernel code (about 24,500).",
            CostMeter.ThreadLine(30, 62_000, [new ContextShare(CostMeter.WholeFiles, 24_500)]));
        Assert.Equal("Cost note sent to the agent after 15 model calls: its context is about 30,000 tokens.", CostMeter.ThreadLine(15, 30_000, []));
    }
}
