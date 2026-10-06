using System.Collections.Concurrent;
using System.Net;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Gateway;
using Litos.SoftwareFactory.Host.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// What a turn may spend. Review was about a third of every task's tokens in the first real
/// runs; a turn now carries an allowance, past which it is asked once to submit what it has.
/// </summary>
public class ActiveRunAllowanceTests
{
    private static ActiveRun Run() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "openrouter", "m");

    [Fact]
    public void ATurnWithNoAllowance_IsNeverAskedToFinish()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);

        for (var i = 0; i < 100; i++)
            Assert.Null(run.RecordTurnCall(50_000));
        Assert.Equal((100, 5_000_000L), (run.TurnCalls, run.TurnCharged));
        Assert.Equal("Implement", run.Phase);
    }

    [Fact]
    public void PastItsCallAllowance_ATurnIsAskedOnce()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Review, TurnKind.Review, "review-1", default, "LightReview", new TurnAllowance(2, null, 4));

        Assert.Null(run.RecordTurnCall(1_000));
        var wrapUp = run.RecordTurnCall(1_000);
        Assert.Null(run.RecordTurnCall(1_000));

        Assert.NotNull(wrapUp);
        Assert.StartsWith("This review has used 2 model calls, its allowance of 2.", wrapUp);
        Assert.Contains("call `submit_review` with the findings you have", wrapUp);
        Assert.Equal("LightReview", run.Phase);
    }

    [Fact]
    public void PastItsTokenAllowance_ATurnIsAsked_EvenWithCallsToSpare()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Review, TurnKind.Review, "review-1", default, "Review", new TurnAllowance(12, 84_702, 24));

        Assert.Null(run.RecordTurnCall(50_000));

        Assert.StartsWith("This review has used 90,000 tokens, its allowance of 84,702.", run.RecordTurnCall(40_000));
    }

    [Fact]
    public void ATurnThatHasAlreadySubmitted_IsNotAsked()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Review, TurnKind.Review, "review-1", default, "Review", new TurnAllowance(1, null, 4));
        Assert.True(run.Accept("review-1", new ReviewSubmission([])).Accepted);

        Assert.Null(run.RecordTurnCall(1_000));
    }

    [Fact]
    public void ANewTurn_StartsItsCountsAndItsReminderAfresh()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Review, TurnKind.Review, "review-1", default, "Review", new TurnAllowance(1, null, 4));
        Assert.NotNull(run.RecordTurnCall(1_000));

        run.BeginTurn(TurnKind.Nudge, TurnKind.Review, "review-1", default, "Review", new TurnAllowance(1, null, 4));

        Assert.Equal((0, 0L), (run.TurnCalls, run.TurnCharged));
        Assert.NotNull(run.RecordTurnCall(1_000));
    }

    [Fact]
    public void WithNoPhaseGiven_TheTurnKindIsThePhase()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Repair, TurnKind.Repair, "s", default);

        Assert.Equal("Repair", run.Phase);
        Assert.Null(run.Allowance);
    }
}

/// <summary>The gateway records what each call was for, and asks a turn past its allowance to finish.</summary>
public sealed class GatewayAllowanceTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private ModelGateway _gateway = null!;
    private ActiveRun _run = null!;
    private Guid _threadId;

    private sealed class SteerRecorder : IWorkerClient
    {
        public ConcurrentQueue<(string Session, string Message)> Steered { get; } = new();

        public Task<TurnStreamResult> RunTurnAsync(string sessionId, TurnKind kind, string brief, int maxToolCalls, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SteerAsync(string sessionId, string message, CancellationToken ct)
        {
            Steered.Enqueue((sessionId, message));
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(string sessionId, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> CompactAsync(string sessionId, string instruction, CancellationToken ct) => Task.FromResult(false);

        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private readonly SteerRecorder _client = new();

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _gateway = _host.App.Services.GetRequiredService<ModelGateway>();
        _threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync(), budgetCap: 900_000);
        await _host.DelegateAsync(_threadId);
        var claimed = (await _host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        _run = new ActiveRun(claimed.Run.Id, _threadId, claimed.Run.RequestedBy, "openrouter", "deepseek/deepseek-v4.1-flash") { Client = _client };
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task CallAsync(string session = "review-1") => _gateway.HandleAsync(
        _run,
        new GatewayRequest(Guid.NewGuid().ToString(), new ChatRequest([ChatMessage.User(new string('x', 4_000))], [], "m", SessionId: session)),
        _ => Task.CompletedTask, default);

    [Fact]
    public async Task EveryCall_IsRecordedWithThePhaseOfItsTurn()
    {
        _run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "thread", default);
        await CallAsync("thread");
        _run.BeginTurn(TurnKind.Review, TurnKind.Review, "review-1", default, "LightReview", new TurnAllowance(5, null, 8));
        await CallAsync();

        var usage = await _host.Store.ListUsageAsync(_threadId, default);
        Assert.Equal(["Implement", "LightReview"], usage.Select(u => u.Phase));

        var api = (await _host.GetAsync($"api/threads/{_threadId}/usage")).EnumerateArray().Select(u => u.GetProperty("phase").GetString());
        Assert.Equal(["Implement", "LightReview"], api);
    }

    [Fact]
    public async Task ACallOutsideAnyTurn_HasNoPhase()
    {
        await CallAsync("thread");

        Assert.Null(Assert.Single(await _host.Store.ListUsageAsync(_threadId, default)).Phase);
    }

    [Fact]
    public async Task AReviewPastItsAllowance_IsSteeredOnce_AndTheThreadSaysSo()
    {
        _run.BeginTurn(TurnKind.Review, TurnKind.Review, "review-1", default, "Review", new TurnAllowance(2, null, 24));

        await CallAsync();
        Assert.Empty(_client.Steered);
        await CallAsync();
        await CallAsync();

        await WaitUntilAsync(() => !_client.Steered.IsEmpty);
        var (session, message) = Assert.Single(_client.Steered);
        Assert.Equal("review-1", session);
        Assert.StartsWith("This review has used 2 model calls", message);

        await WaitUntilAsync(async () => (await _host.ThreadAsync(_threadId)).Messages.Any(m => m.Text.StartsWith("The review reached its allowance")));
        var note = (await _host.ThreadAsync(_threadId)).Messages.Last(m => m.Text.StartsWith("The review reached its allowance"));
        Assert.Equal((MessageAuthor.Factory, MessageKind.Status), (note.Author, note.Kind));
        Assert.Contains("2 model calls", note.Text);
    }

    [Fact]
    public async Task AnImplementTurn_IsNeverSteered()
    {
        _run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "thread", default);

        for (var i = 0; i < 5; i++)
            await CallAsync("thread");

        await Task.Delay(100);
        Assert.Empty(_client.Steered);
    }

    // ---- After the turn's result is recorded ----

    private static readonly ToolSchema KernelTool = new("run_kernel_code", "Runs C#.", System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }));

    /// <summary>A call as a turn makes it (with tools), collecting what the worker would receive.</summary>
    private async Task<List<GatewayEvent>> TurnCallAsync(string session, bool withTools = true)
    {
        var events = new List<GatewayEvent>();
        await _gateway.HandleAsync(
            _run,
            new GatewayRequest(Guid.NewGuid().ToString(), new ChatRequest([ChatMessage.User("go on")], withTools ? [KernelTool] : [], "m", SessionId: withTools ? session : null)),
            e => { events.Add(e); return Task.CompletedTask; }, default);
        return events;
    }

    private static bool IsFinishedReply(List<GatewayEvent> events) =>
        events.OfType<GatewayMessageCompleted>().SingleOrDefault() is { } done
        && done.Usage == new UsageInfo(0, 0)
        && done.Message.Content.OfType<TextBlock>().Single().Text == ModelGateway.FinishedReply;

    [Theory]
    [InlineData(TurnKind.Review)]
    [InlineData(TurnKind.Implement)]
    public async Task OnceTheTurnHasSubmitted_ItsNextCall_IsAnsweredWithoutTheProvider_AndCostsNothing(TurnKind kind)
    {
        _run.BeginTurn(kind, kind, "s-1", default);
        await TurnCallAsync("s-1");
        Submission submission = kind == TurnKind.Review ? new ReviewSubmission([]) : new WorkSubmission("Done.", [], [], [], []);
        Assert.True(_run.Accept("s-1", submission).Accepted);

        var events = await TurnCallAsync("s-1");

        Assert.True(IsFinishedReply(events));
        Assert.Equal(ModelGateway.FinishedReply, Assert.Single(events.OfType<GatewayTextDelta>()).Text);
        // One provider call reserved and charged, not two.
        Assert.Single(await _host.Store.ListUsageAsync(_threadId, default));
    }

    [Fact]
    public async Task BeforeTheTurnHasSubmitted_CallsGoToTheProvider()
    {
        _run.BeginTurn(TurnKind.Review, TurnKind.Review, "s-1", default);

        Assert.False(IsFinishedReply(await TurnCallAsync("s-1")));
        Assert.False(IsFinishedReply(await TurnCallAsync("s-1")));
        Assert.Equal(2, (await _host.Store.ListUsageAsync(_threadId, default)).Count);
    }

    [Fact]
    public async Task ACompactionCall_AfterTheSubmission_StillGoesToTheProvider()
    {
        _run.BeginTurn(TurnKind.Review, TurnKind.Review, "s-1", default);
        Assert.True(_run.Accept("s-1", new ReviewSubmission([])).Accepted);

        // A compaction request carries no tools and no session; answering it would replace the
        // conversation's summary with "Submitted."
        Assert.False(IsFinishedReply(await TurnCallAsync("s-1", withTools: false)));
        Assert.Single(await _host.Store.ListUsageAsync(_threadId, default));
    }

    [Fact]
    public async Task AnotherSessionsCall_IsNotAnswered_ForTheTurnThatSubmitted()
    {
        _run.BeginTurn(TurnKind.Review, TurnKind.Review, "s-1", default);
        Assert.True(_run.Accept("s-1", new ReviewSubmission([])).Accepted);

        Assert.False(IsFinishedReply(await TurnCallAsync("s-2")));
    }

    [Fact]
    public async Task ANewTurn_StartsUnfinished()
    {
        _run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s-1", default);
        Assert.True(_run.Accept("s-1", new WorkSubmission("Done.", [], [], [], [])).Accepted);
        _run.BeginTurn(TurnKind.Repair, TurnKind.Repair, "s-1", default);

        Assert.False(IsFinishedReply(await TurnCallAsync("s-1")));
    }

    [Fact]
    public void ARecordedDecision_DoesNotFinishTheTurnThisWay()
    {
        // Its turn is cancelled once the tool has returned (WorkerCallbacks).
        _run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s-1", default);
        Assert.True(_run.Accept("s-1", new DecisionSubmission("Which?", "It matters.", ["A", "B"], null, null)).Accepted);

        Assert.False(_run.HasFinished("s-1"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition) => await WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++)
            await Task.Delay(30);
        Assert.True(await condition(), "The condition was not met in time.");
    }
}

/// <summary>A run's review, as the executor plans and limits it.</summary>
public sealed class ReviewPlanningRunTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private TurnCall Review => _host.Workers.Turns.First(t => t.Kind == TurnKind.Review);

    [Fact]
    public async Task ASmallSafeChange_GetsALightReview_WithItsOwnBrief_AndASmallToolLimit()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Contains("# Factory review brief: a light review", Review.Brief);
        Assert.Contains("## The implementing agent's account (claims to check, not facts)", Review.Brief);
        Assert.Equal(6, Review.MaxToolCalls);
        Assert.Contains(details.Messages, m => m.Text.StartsWith("Review: light (risk score 0). A small change (1 line outside tests in 1 file)"));
        // The implement turn kept the ordinary limit.
        Assert.Equal(200, _host.Workers.Turns.First().MaxToolCalls);
    }

    [Fact]
    public async Task ARiskyChange_GetsAFullReview_AndTheThreadSaysWhy()
    {
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Storage/LogFormat.cs", "lock (_gate) { }\n");
            await call.Worker.SubmitAsync(call.SessionId, FakeWorkerLauncher.Work());
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.DoesNotContain("a light review", Review.Brief);
        Assert.Equal(24, Review.MaxToolCalls);
        var note = Assert.Single(details.Messages, m => m.Text.StartsWith("Review: full"));
        Assert.Contains("it touches storage or a data format", note.Text);
    }

    /// <summary>A review that runs out of tool calls is reminded, with a small limit, and its
    /// findings still reach the handoff.</summary>
    [Fact]
    public async Task AReviewThatRunsOutOfToolCalls_IsReminded_AndTheRunStillHandsOff()
    {
        _host.Workers.Script.Enqueue(_host.DefaultTurnAsync);
        _host.Workers.Script.Enqueue(_ => Task.FromResult(new TurnStreamResult(false, 5, "The turn exceeded 6 tool calls.")));
        _host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.SubmitAsync(call.SessionId, new ReviewSubmission([new ReviewFinding(FindingSeverity.Minor, "src/Orders.cs", 1, "Unconfirmed: the name is unclear.")]));
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var turns = _host.Workers.Turns.ToArray();
        Assert.Equal([TurnKind.Implement, TurnKind.Review, TurnKind.Nudge], turns.Select(t => t.Kind));
        Assert.Equal(6, turns[2].MaxToolCalls);
        Assert.Equal(turns[1].SessionId, turns[2].SessionId);
        Assert.Contains(details.Findings, f => f.Text == "Unconfirmed: the name is unclear.");
    }
}
