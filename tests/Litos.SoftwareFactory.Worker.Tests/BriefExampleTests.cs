using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Tools;
using Litos.Kernel;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Worker;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// The briefs show the agent how to call the completion tools from kernel code, because the
/// kernel's signature line names only a tool's top-level parameters. An example that does not
/// compile, or that the tool rejects, would teach every run the wrong call — so each one is run
/// here, through a real kernel subprocess, against the real tools.
/// </summary>
public sealed partial class BriefExampleTests
{
    private static readonly RunContext Context = new("SalesApp", "factory/7f3a-csv-export", "main", "Add CSV export for Orders.")
    {
        Diff = "+x",
        ChangedLineCount = 1,
    };

    private static string Brief(BriefKind brief, TurnKind kind, ReviewDepth depth = ReviewDepth.Full) =>
        BriefComposer.Compose(new StartTurnStep(kind, brief), Context with { ReviewDepth = depth }, RunOrchestrator.NewRun(RunKind.Implement), new RunLimits());

    [GeneratedRegex("```csharp\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex CSharpBlock();

    private static string[] Examples(string brief) =>
        [.. CSharpBlock().Matches(brief.ReplaceLineEndings("\n")).Select(m => m.Groups[1].Value)];

    /// <summary>Runs each example in a real kernel, with the tools posting to a fake host, and
    /// returns what reached the host.</summary>
    private static async Task<List<Submission>> RunAsync(string[] examples)
    {
        var handler = new FakeHttpMessageHandler();
        foreach (var _ in examples.SelectMany(e => Regex.Matches(e, "await ").Cast<Match>()))
            handler.EnqueueJson(new SubmissionResponse(true, ""));
        var host = TestOptions.HostClient(handler);
        CompletionTool[] tools = [new SubmitWorkTool(host, "s"), new RequestDecisionTool(host, "s"), new SubmitReviewTool(host, "s")];

        var scratch = Path.Combine(Path.GetTempPath(), "litos-brief-examples", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);
        await using var session = new KernelSession(
            sessionId: "brief-examples",
            workingDirectory: Path.GetTempPath(),
            scratchDirectory: scratch,
            bridgedToolsSource: () => new ToolRegistry(tools),
            mcpToolProvider: null,
            hardTimeout: TimeSpan.FromSeconds(120));

        foreach (var example in examples)
        {
            var result = await session.RunAsync(example, CancellationToken.None);
            Assert.False(result.IsError, $"The example failed:\n{example}\n---\n{result.Text}");
        }

        return [.. handler.Requests.Select(r => JsonSerializer.Deserialize<SubmissionRequest>(r.Body!, FactoryWire.Json)!.Submission)];
    }

    [Fact]
    public async Task TheRunBriefsExamples_CompileAndAreAccepted()
    {
        var examples = Examples(Brief(BriefKind.Run, TurnKind.Implement));
        Assert.Single(examples);

        var submissions = await RunAsync(examples);

        Assert.Equal(2, submissions.Count);
        var work = Assert.IsType<WorkSubmission>(submissions[0]);
        Assert.Equal("Added CSV export for orders.", work.Summary);
        Assert.Equal(["Export_Admin_Succeeds"], work.Criteria[0].Tests);
        Assert.True(work.Criteria[1].ManualOnly);
        Assert.Equal(["Export_Admin_Succeeds"], work.TestsAdded);
        Assert.Single(work.KnownLimitations);
        Assert.Single(work.ManualTestSteps);

        var decision = Assert.IsType<DecisionSubmission>(submissions[1]);
        Assert.Equal(2, decision.Options.Count);
        Assert.StartsWith("Every filtered row", decision.Recommendation);
    }

    [Theory]
    [InlineData(ReviewDepth.Full)]
    [InlineData(ReviewDepth.Light)]
    public async Task TheReviewBriefsExamples_CompileAndAreAccepted(ReviewDepth depth)
    {
        var examples = Examples(Brief(BriefKind.Review, TurnKind.Review, depth));
        Assert.Single(examples);

        var submissions = await RunAsync(examples);

        Assert.Equal(2, submissions.Count);
        var finding = Assert.Single(Assert.IsType<ReviewSubmission>(submissions[0]).Findings);
        Assert.Equal(new ReviewFinding(FindingSeverity.Minor, "src/Invoices.cs", 42, "The empty-list case has no test."), finding);
        Assert.Empty(Assert.IsType<ReviewSubmission>(submissions[1]).Findings);
    }
}
