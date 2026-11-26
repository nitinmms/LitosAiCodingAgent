using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Gui.Buzzies;
using Litos.Gui.Buzzies.Routines;
using Litos.Gui.Tests.Fakes;
using Litos.Host;
using Litos.Tools.FileSystem;
using Litos.Tools.Shell;

namespace Litos.Gui.Tests.Buzzies;

public sealed class BuzzyRunExecutorTests : IDisposable
{
    private static readonly DateTimeOffset Now = Routines.Monday9;
    private readonly TempDir _temp = new();
    private readonly BuzzyStore _store;
    private readonly BuzzyProfile _builder;
    private readonly ScriptedChatProvider _provider = new();

    private sealed class NoopPrompt : ISystemPromptProvider
    {
        public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult<SystemPromptSections?>(new SystemPromptSections("id", "tools", "rules", null, [], "footer"));
    }

    public BuzzyRunExecutorTests()
    {
        _store = new BuzzyStore(_temp.Path);
        _builder = _store.Create(BuzzyTemplates.Builder, "Builder", "#9B4DE0", Now);
    }

    public void Dispose() => _temp.Dispose();

    private BuzzyRunExecutor Executor(string model = "model") => new(_ => Task.FromResult(new BuzzyRunEnvironment(
        new AgentLoopFactory(new FakeTranscriptStore(), new ContextAccountant(), new NoopPrompt(), new Compactor(new CompactionSettings())),
        new ToolRegistry([new WriteFileTool(new GuiApprovalGate()), new ShellTool(new GuiApprovalGate()), new RecordingTool("artifact", pathRequired: false)]),
        _provider,
        model,
        200_000,
        new FakeTranscriptStore(),
        new BuzzyToolContext(_store, new PendingApprovalStore()))));

    private RunRequest Request(Routine? routine = null) =>
        new(_builder, routine ?? Routines.Manual(_builder.Id, Now, "Nightly notes"), Guid.NewGuid().ToString("n"), Now);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Succeeds_WritesInTheWorkspace_AndReportsSummaryOutputsAndTokens()
    {
        _provider.Enqueue(
            new ToolCallCompleted("c1", "write_file", Json(new { path = "notes/2026-11-23.md", content = "hello" })),
            new MessageCompleted(ChatMessage.Assistant([new ToolUseBlock("c1", "write_file", Json(new { path = "notes/2026-11-23.md", content = "hello" }))]), new UsageInfo(100, 20)));
        _provider.Enqueue(
            new TextDelta("Saved notes/2026-11-23.md."),
            new MessageCompleted(ChatMessage.Assistant([new TextBlock("Saved notes/2026-11-23.md.")]), new UsageInfo(150, 10)));

        var outcome = await Executor().RunAsync(Request(), default);

        Assert.Equal(RunStatus.Succeeded, outcome.Status);
        Assert.Equal("Saved notes/2026-11-23.md.", outcome.Summary);
        Assert.Equal(["notes/2026-11-23.md"], outcome.Outputs);
        Assert.Equal(250, outcome.InputTokens);
        Assert.Equal(30, outcome.OutputTokens);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_store.WorkspaceOf(_builder.Id), "notes", "2026-11-23.md")));
    }

    [Fact]
    public async Task Unattended_NeverOffersShell_EvenToBuilder()
    {
        _provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(1, 1)));

        await Executor().RunAsync(Request(), default);

        var tools = _provider.Requests[0].Tools.Select(t => t.Name).ToList();
        Assert.DoesNotContain("shell", tools);
        Assert.Contains("write_file", tools);
        Assert.Contains("artifact", tools);
    }

    [Fact]
    public async Task TheRoutinePrompt_IsSentWithTheUnattendedBrief()
    {
        _provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(1, 1)));
        var routine = Routines.Manual(_builder.Id, Now, "Nightly notes") with { Prompt = "Summarise today's commits." };

        await Executor().RunAsync(Request(routine), default);

        var sent = string.Join("\n", _provider.Requests[0].Messages.SelectMany(m => m.Content).OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("Summarise today's commits.", sent);
        Assert.Contains("Nobody is watching this run", sent);
        Assert.Contains("\"Nightly notes\"", sent);
    }

    [Fact]
    public async Task OverTheTokenBudget_StopsAndFails()
    {
        var call = Json(new { path = "a.md", content = "x" });
        for (var i = 0; i < 5; i++)
            _provider.Enqueue(
                new ToolCallCompleted($"c{i}", "write_file", call),
                new MessageCompleted(ChatMessage.Assistant([new ToolUseBlock($"c{i}", "write_file", call)]), new UsageInfo(6_000, 1_000)));
        var routine = Routines.Manual(_builder.Id, Now) with { MaxTokens = 10_000 };

        var outcome = await Executor().RunAsync(Request(routine), default);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Contains("budget", outcome.Error);
        Assert.True(_provider.Requests.Count <= 2);
    }

    [Fact]
    public async Task CancelledByTheCaller_EndsCancelled()
    {
        _provider.Delay = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var outcome = await Executor().RunAsync(Request(), cts.Token);

        Assert.Equal(RunStatus.Cancelled, outcome.Status);
    }

    [Fact]
    public async Task NoModelSelected_FailsWithoutCallingTheProvider()
    {
        var outcome = await Executor(model: "").RunAsync(Request(), default);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Contains("/model", outcome.Error);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task ProviderError_Fails_WithItsMessage()
    {
        _provider.Enqueue(new ErrorOccurred(new InvalidOperationException("rate limited")));

        var outcome = await Executor().RunAsync(Request(), default);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Equal("rate limited", outcome.Error);
    }

    [Fact]
    public void ShowablePath_IsRelativeInsideTheWorkspace()
    {
        var workspace = _store.WorkspaceOf(_builder.Id);

        Assert.Equal("reports/a.md", BuzzyRunExecutor.ShowablePath(workspace, "reports/a.md"));
        Assert.Equal("reports/a.md", BuzzyRunExecutor.ShowablePath(workspace, Path.Combine(workspace, "reports", "a.md")));
        Assert.EndsWith("/elsewhere.md", BuzzyRunExecutor.ShowablePath(workspace, "../../elsewhere.md"));
    }

    [Fact]
    public async Task ArtifactLinks_AreRecordedAsOutputs()
    {
        var args = Json(new { title = "Report" });
        _provider.Enqueue(
            new ToolCallCompleted("c1", "artifact", args),
            new MessageCompleted(ChatMessage.Assistant([new ToolUseBlock("c1", "artifact", args)]), new UsageInfo(1, 1)));
        _provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("Published.")]), new UsageInfo(1, 1)));
        var executor = new BuzzyRunExecutor(_ => Task.FromResult(new BuzzyRunEnvironment(
            new AgentLoopFactory(new FakeTranscriptStore(), new ContextAccountant(), new NoopPrompt(), new Compactor(new CompactionSettings())),
            new ToolRegistry([new FixedResultTool("artifact", "Published: http://127.0.0.1:5123/artifacts/abc123 (open it)")]),
            _provider, "model", 200_000, new FakeTranscriptStore(), new BuzzyToolContext(_store, new PendingApprovalStore()))));

        var outcome = await executor.RunAsync(Request(), default);

        Assert.Equal(["http://127.0.0.1:5123/artifacts/abc123"], outcome.Outputs);
    }

    private sealed class FixedResultTool(string name, string text) : ITool
    {
        public string Name => name;
        public string Description => "fixed";
        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok(text));
    }
}
