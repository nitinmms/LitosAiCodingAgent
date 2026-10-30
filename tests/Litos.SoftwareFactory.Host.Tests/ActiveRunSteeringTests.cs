using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Host.Runs;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// What a run's turn in progress is doing, for a chat's snapshot (m2-architecture.md §5.3), and the
/// @factory messages passed to it (§5.4), as <see cref="ActiveRun"/> keeps them in memory.
/// </summary>
public sealed class ActiveRunSteeringTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class NoWorker : IWorkerClient
    {
        public Task<TurnStreamResult> RunTurnAsync(
            string sessionId, TurnKind kind, string brief, int maxToolCalls, CancellationToken ct, Action<TurnProgress>? onProgress = null,
            IReadOnlyList<TurnImage>? images = null) => throw new NotSupportedException();

        public Task<bool> SteerAsync(string sessionId, string message, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<string>> TakeUndeliveredSteeringAsync(string sessionId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> CancelAsync(string sessionId, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> CompactAsync(string sessionId, string instruction, CancellationToken ct) => Task.FromResult(false);

        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static ActiveRun Run() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "openrouter", "m") { Client = new NoWorker() };

    private static TurnProgress Tool(string path) =>
        new(TurnProgressKind.ToolCall, "read_file", JsonSerializer.SerializeToElement(new { path }));

    [Fact]
    public void Activity_IsNull_OutsideATurn_AndAfterItEnds()
    {
        var run = Run();
        Assert.Null(run.Activity);

        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default, startedAt: T0);
        Assert.NotNull(run.Activity);

        run.EndTurn();
        Assert.Null(run.Activity);
    }

    [Fact]
    public void Activity_GivesTheTurn_ItsStart_AndOnlyTheNewestToolCalls()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default, startedAt: T0);
        for (var i = 1; i <= ActiveRun.RecentToolCalls + 2; i++)
            run.TrackProgress(Tool($"src/File{i}.cs"), T0.AddSeconds(i));
        run.TrackProgress(new TurnProgress(TurnProgressKind.ModelReply), T0.AddMinutes(1)); // not a tool call
        run.RecordTurnCall(1_500);

        var activity = run.Activity!;

        Assert.Equal(("Implement", T0, 1, 1_500L), (activity.Turn, activity.TurnStartedAt, activity.ModelCalls, activity.TokensThisTurn));
        Assert.Equal(ActiveRun.RecentToolCalls, activity.RecentTools.Count);
        Assert.Contains("File3.cs", activity.RecentTools[0].Description);
        Assert.Contains($"File{ActiveRun.RecentToolCalls + 2}.cs", activity.RecentTools[^1].Description);
    }

    [Fact]
    public void ANewTurn_StartsWithNoToolCalls()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default, startedAt: T0);
        run.TrackProgress(Tool("src/A.cs"), T0);

        run.BeginTurn(TurnKind.Repair, TurnKind.Repair, "s", default, startedAt: T0.AddMinutes(5));

        Assert.Empty(run.Activity!.RecentTools);
    }

    [Theory]
    [InlineData(TurnKind.Implement, true)]
    [InlineData(TurnKind.Rework, true)]
    [InlineData(TurnKind.Repair, true)]
    [InlineData(TurnKind.Review, false)]
    [InlineData(TurnKind.Scan, false)]
    public void OnlyAWorkTurnInProgress_TakesSteering(TurnKind workKind, bool takes)
    {
        var run = Run();
        Assert.False(run.TakesSteering);

        run.BeginTurn(workKind, workKind, "s", default);
        Assert.Equal(takes, run.TakesSteering);

        run.EndTurn();
        Assert.False(run.TakesSteering);
    }

    [Fact]
    public void ASteer_CountsAsAFollowUp_UntilItIsForgotten()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        run.NoteSteer(first, "Use PageSize.");
        run.NoteSteer(second, "Keep it small.");
        Assert.Equal(2, run.FollowUps);

        run.ForgetSteer(first);
        run.ForgetSteer(first); // forgetting twice takes nothing more
        Assert.Equal(1, run.FollowUps);
    }

    [Fact]
    public void UnreadSteers_MatchEachUnreadTextToOneMessage_OldestFirst()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        run.NoteSteer(a, "Same text.");
        run.NoteSteer(b, "Same text.");
        run.NoteSteer(c, "Other.");

        // One "Same text." was read and one was not; an unknown text matches nothing.
        Assert.Equal([a], run.UnreadSteers(["Same text.", "Never sent."]));
        Assert.Equal([a, b, c], run.UnreadSteers(["Other.", "Same text.", "Same text."]));
    }

    [Fact]
    public void ANewTurn_ForgetsTheLastTurnsSteers()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);
        run.NoteSteer(Guid.NewGuid(), "Use PageSize.");

        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);

        Assert.Equal(0, run.FollowUps);
        Assert.Empty(run.UnreadSteers(["Use PageSize."]));
    }
}
