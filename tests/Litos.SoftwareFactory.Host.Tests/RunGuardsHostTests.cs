using System.Collections.Concurrent;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Gateway;
using Litos.SoftwareFactory.Host.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The W3.1 progress guard on a run's turn: counted per turn, noted once, then stopped.</summary>
public class ActiveRunProgressTests
{
    private static readonly RunLimits Limits = new();

    private static ActiveRun Run() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "anthropic", "claude-haiku-5-5");

    private static void Calls(ActiveRun run, int count)
    {
        for (var i = 0; i < count; i++)
            run.RecordTurnCall(1_000);
    }

    [Fact]
    public void OnlyThisTurnsCallsCount_SoAResumedTurnIsNotStoppedAtItsFirstCall()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "thread", default);
        Calls(run, 3);

        // The conversation has gone 40 calls without a write, but this turn has made only 3.
        Assert.Equal((ProgressGuard.Verdict.Continue, 3), run.JudgeProgress(40, Limits));
    }

    [Fact]
    public void TheNoteIsGivenOnce_ThenTheTurnIsStopped_AndStaysStopped()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "thread", default);

        Calls(run, 15);
        Assert.Equal(ProgressGuard.Verdict.Note, run.JudgeProgress(15, Limits).Verdict);
        Calls(run, 1);
        Assert.Equal(ProgressGuard.Verdict.Continue, run.JudgeProgress(16, Limits).Verdict);
        Calls(run, 9);
        Assert.Equal(ProgressGuard.Verdict.Stop, run.JudgeProgress(25, Limits).Verdict);

        Assert.StartsWith("The turn made 25 model calls without changing a file", run.NoProgressStop);
        Assert.Equal(ProgressGuard.Verdict.Continue, run.JudgeProgress(26, Limits).Verdict);
    }

    [Fact]
    public void ANewTurn_ClearsTheStopAndTheNotes()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "thread", default);
        Calls(run, 25);
        run.JudgeProgress(25, Limits);
        Assert.True(run.BudgetNoteDue(1, Limits));

        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "thread", default);

        Assert.Null(run.NoProgressStop);
        Assert.True(run.BudgetNoteDue(1, Limits));
    }

    [Theory]
    [InlineData(TurnKind.Review)]
    [InlineData(TurnKind.Scan)]
    public void AReviewOrAScan_IsNeverGuarded_TheyDoNotWrite(TurnKind kind)
    {
        var run = Run();
        run.BeginTurn(kind, kind, "review-1", default);
        Calls(run, 30);

        Assert.Equal(ProgressGuard.Verdict.Continue, run.JudgeProgress(30, Limits).Verdict);
        Assert.False(run.BudgetNoteDue(0, Limits));
    }

    [Fact]
    public void ATurnThatHasSubmitted_IsNotGuarded()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "thread", default);
        Calls(run, 30);
        Assert.True(run.Accept("thread", new WorkSubmission("done", [], [], [], [])).Accepted);

        Assert.Equal(ProgressGuard.Verdict.Continue, run.JudgeProgress(30, Limits).Verdict);
    }

    [Fact]
    public void TheBudgetNoteIsDueOncePerTurn()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Repair, TurnKind.Repair, "thread", default);

        Assert.False(run.BudgetNoteDue(9, Limits));
        Assert.True(run.BudgetNoteDue(4, Limits));
        Assert.False(run.BudgetNoteDue(2, Limits));
    }
}

/// <summary>The gateway's side: the steers it sends, the stop, and the refusal after it.</summary>
public sealed class GatewayRunGuardTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private ModelGateway _gateway = null!;
    private Guid _projectId;

    private sealed class SteerRecorder : IWorkerClient
    {
        public ConcurrentQueue<(string Session, string Message)> Steered { get; } = new();

        public Task<TurnStreamResult> RunTurnAsync(
            string sessionId, TurnKind kind, string brief, int maxToolCalls, CancellationToken ct, Action<TurnProgress>? onProgress = null,
            IReadOnlyList<TurnImage>? images = null) =>
            throw new NotSupportedException();

        public Task<bool> SteerAsync(string sessionId, string message, CancellationToken ct)
        {
            Steered.Enqueue((sessionId, message));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> TakeUndeliveredSteeringAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> CancelAsync(string sessionId, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> CompactAsync(string sessionId, string instruction, CancellationToken ct) => Task.FromResult(false);

        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private readonly SteerRecorder _client = new();

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _gateway = _host.App.Services.GetRequiredService<ModelGateway>();
        _projectId = await _host.RegisterProjectAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(ActiveRun Run, Guid ThreadId)> RunAsync(long budgetCap = 900_000)
    {
        var threadId = await _host.CreateThreadAsync(_projectId, budgetCap: budgetCap);
        await _host.DelegateAsync(threadId);
        var claimed = (await _host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        var run = new ActiveRun(claimed.Run.Id, threadId, claimed.Run.RequestedBy, "openrouter", "deepseek/deepseek-v4.1-flash") { Client = _client };
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "thread", default);
        return (run, threadId);
    }

    private static ChatMessage Read(int i) => new(Role.Assistant,
        [new ToolUseBlock($"r{i}", "run_kernel_code", JsonSerializer.SerializeToElement(new { code = $"Console.WriteLine(await read_file(\"path\", \"f{i}.cs\"));" }))]);

    private static readonly ToolSchema AnyTool = new("run_kernel_code", "Runs code.", JsonDocument.Parse("""{"type":"object"}""").RootElement);

    /// <summary>One call of the turn, with a conversation that has made <paramref name="reads"/> calls without writing.</summary>
    private async Task<List<GatewayEvent>> CallAsync(ActiveRun run, int reads)
    {
        List<ChatMessage> messages = [ChatMessage.User("brief"), .. Enumerable.Range(0, reads).SelectMany(i => new[] { Read(i), ChatMessage.User([new ToolResultBlock($"r{i}", "ok")]) })];
        var written = new List<GatewayEvent>();
        await _gateway.HandleAsync(run, new GatewayRequest(Guid.NewGuid().ToString(), new ChatRequest(messages, [AnyTool], "m", SessionId: "thread")),
            e => { written.Add(e); return Task.CompletedTask; }, default);
        return written;
    }

    private async Task<bool> ThreadSaysAsync(Guid threadId, string start)
    {
        for (var i = 0; i < 100; i++)
        {
            if ((await _host.ThreadAsync(threadId)).Messages.Any(m => m.Text.StartsWith(start, StringComparison.Ordinal)))
                return true;
            await Task.Delay(30);
        }

        return false;
    }

    [Fact]
    public async Task AWorkTurnThatStopsWriting_IsToldOnce_ThenStopped_AndItsNextCallIsRefusedWithoutSpending()
    {
        _host.Options.Limits = _host.Options.Limits with { ProgressNoteAfterCalls = 2, ProgressStopAfterCalls = 3, CostNotes = false };
        var (run, threadId) = await RunAsync();

        await CallAsync(run, 5);
        await CallAsync(run, 6);
        Assert.True(await ThreadSaysAsync(threadId, "Progress note sent to the agent: 2 model calls without changing a file"));
        var (session, steer) = Assert.Single(_client.Steered);
        Assert.Equal("thread", session);
        Assert.StartsWith("Progress note from the factory. This turn has made 2 model calls", steer);

        await CallAsync(run, 7);
        Assert.True(await ThreadSaysAsync(threadId, "The turn made 3 model calls without changing a file in the working copy, so the factory stopped it."));
        Assert.Single(_client.Steered); // the stop is not a steer
        var spent = (await _host.Store.ListUsageAsync(threadId, default)).Count;

        var refused = await CallAsync(run, 8);

        var error = Assert.IsType<GatewayError>(Assert.Single(refused));
        Assert.Equal(GatewayErrorCodes.NoProgress, error.Code);
        Assert.True(GatewayErrorCodes.IsRefusal(error.Code));
        Assert.Equal(spent, (await _host.Store.ListUsageAsync(threadId, default)).Count);
    }

    [Fact]
    public async Task ATurnThatHasJustWritten_IsNotNoted()
    {
        _host.Options.Limits = _host.Options.Limits with { ProgressNoteAfterCalls = 1, ProgressStopAfterCalls = 50, CostNotes = false };
        var (run, threadId) = await RunAsync();
        var write = new ChatMessage(Role.Assistant,
            [new ToolUseBlock("w", "run_kernel_code", JsonSerializer.SerializeToElement(new { code = "await write_file(\"path\", \"a.cs\", \"content\", \"x\");" }))]);

        await _gateway.HandleAsync(run, new GatewayRequest(Guid.NewGuid().ToString(),
            new ChatRequest([ChatMessage.User("brief"), write, ChatMessage.User([new ToolResultBlock("w", "ok")])], [AnyTool], "m", SessionId: "thread")),
            _ => Task.CompletedTask, default);

        await Task.Delay(150);
        Assert.Empty(_client.Steered);
    }

    [Fact]
    public async Task WhenTheBudgetHasRoomForFewCalls_TheTurnIsAskedOnceToFinishAndSubmit()
    {
        _host.Options.Limits = _host.Options.Limits with { CostNotes = false };
        _host.Provider.EnqueueReply("one", new UsageInfo(9_000, 400));
        _host.Provider.EnqueueReply("two", new UsageInfo(9_000, 400));
        var (run, threadId) = await RunAsync(budgetCap: 40_000);

        await CallAsync(run, 0);
        await CallAsync(run, 0);

        Assert.True(await ThreadSaysAsync(threadId, "Budget note sent to the agent: about"));
        var steer = Assert.Single(_client.Steered).Message;
        Assert.StartsWith("Budget note from the factory. The task's budget has about", steer);
        Assert.Contains("call submit_work", steer);
    }

    [Fact]
    public async Task WithTheGuardsOff_NothingIsNotedOrStopped()
    {
        _host.Options.Limits = _host.Options.Limits with { RunGuards = false, ProgressNoteAfterCalls = 1, ProgressStopAfterCalls = 2, CostNotes = false };
        var (run, _) = await RunAsync(budgetCap: 40_000);

        await CallAsync(run, 5);
        await CallAsync(run, 6);
        var third = await CallAsync(run, 7);

        await Task.Delay(150);
        Assert.Empty(_client.Steered);
        Assert.DoesNotContain(third, e => e is GatewayError);
        Assert.Null(run.NoProgressStop);
    }
}
