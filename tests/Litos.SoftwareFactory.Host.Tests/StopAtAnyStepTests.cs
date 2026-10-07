using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// A person's pause or cancel takes effect in whatever step the run is in (§16,
/// docs/software-factory/m2-architecture.md §3.3). It used to reach only a model turn, so one asked
/// for during verification waited for the next turn, or was lost when the run handed off instead.
/// </summary>
public sealed class StopAtAnyStepTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>Holds the first operation it is given open until released or cancelled.</summary>
    private sealed class Once
    {
        private int _used;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public async Task HoldAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _used, 1) == 1)
                return;

            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }

    private async Task<(Guid ProjectId, Guid ThreadId)> DelegatedAsync()
    {
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        return (projectId, threadId);
    }

    private TurnKind[] TurnKinds => [.. _host.Workers.Turns.Select(t => t.Kind)];

    private async Task<int> RunVerificationsAsync()
    {
        await using var db = await _host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync();
        return await db.Verifications.CountAsync(v => v.Kind == VerificationKind.Run);
    }

    private Task PauseAsync(Guid threadId) => _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);

    [Fact]
    public async Task PausedDuringVerification_StopsThere_RecordsNoEvidence_AndResumeVerifiesAgain()
    {
        var hold = new Once();
        _host.Verifier.Hold = (request, ct) => request.ChangedFiles is null ? Task.CompletedTask : hold.HoldAsync(ct);
        var (_, threadId) = await DelegatedAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await PauseAsync(threadId);

        var paused = await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        Assert.True(hold.WasCancelled);
        Assert.Equal(StopReason.PausedByUser, paused.LatestRun!.StopReason);
        Assert.Equal(Stage.Verify, paused.Thread.Stage);
        Assert.Equal(0, await RunVerificationsAsync()); // an interrupted test run is never evidence
        Assert.Equal([TurnKind.Implement], TurnKinds);

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        // The work is not done again: verification, then the review.
        Assert.Equal([TurnKind.Implement, TurnKind.Review], TurnKinds);
        Assert.Equal(1, await RunVerificationsAsync());
    }

    [Fact]
    public async Task CancelledDuringVerification_Stops_KeepsTheEdits_AndFreesTheRepository()
    {
        var hold = new Once();
        _host.Verifier.Hold = (request, ct) => request.ChangedFiles is null ? Task.CompletedTask : hold.HoldAsync(ct);
        var (projectId, threadId) = await DelegatedAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await _host.PostAsync($"api/threads/{threadId}/cancel", null, HttpStatusCode.Accepted);

        var cancelled = await _host.WaitForStateAsync(threadId, LifecycleState.Cancelled);
        Assert.Equal((RunStatus.Finished, StopReason.Cancelled), (cancelled.LatestRun!.Status, cancelled.LatestRun.StopReason));
        Assert.True(_host.Workspaces.Of(projectId).Files.ContainsKey("src/Orders.cs"));
        Assert.Empty(_host.Workspaces.Of(projectId).Pushed);
        Assert.Equal(0, await RunVerificationsAsync());
    }

    [Fact]
    public async Task PausedDuringPreflight_StopsBeforeAnyTurn_AndResumeStartsTheWork()
    {
        var hold = new Once();
        var projectId = await _host.RegisterProjectAsync();
        _host.Workspaces.Of(projectId).Hold = (operation, ct) => operation == "fetch" ? hold.HoldAsync(ct) : Task.CompletedTask;
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await PauseAsync(threadId);

        var paused = await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        Assert.True(hold.WasCancelled);
        Assert.Equal(StopReason.PausedByUser, paused.LatestRun!.StopReason);
        Assert.Empty(TurnKinds);
        Assert.Empty(_host.Workers.Workers); // no worker was started

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        Assert.Equal([TurnKind.Implement, TurnKind.Review], TurnKinds);
    }

    /// <summary>A clone cut short would leave a working copy that later looks complete, so the
    /// first clone finishes; the stop takes effect at the step after it.</summary>
    [Fact]
    public async Task PausedDuringTheFirstClone_LetsTheCloneFinish_ThenStops()
    {
        var hold = new Once();
        var projectId = await _host.RegisterProjectAsync();
        var workspace = _host.Workspaces.Of(projectId);
        workspace.Hold = (operation, ct) => operation == "clone" ? hold.HoldAsync(ct) : Task.CompletedTask;
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await PauseAsync(threadId);
        await Task.Delay(200);
        Assert.False(hold.WasCancelled);
        hold.Release.SetResult();

        await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        Assert.Contains("clone", workspace.Calls);
        Assert.DoesNotContain("fetch", workspace.Calls);
    }

    [Fact]
    public async Task PausedDuringHandoff_PushesNothing_AndResumeHandsOff()
    {
        var hold = new Once();
        var (projectId, threadId) = await DelegatedAsync();
        var workspace = _host.Workspaces.Of(projectId);
        workspace.Hold = (operation, ct) => operation == "push" ? hold.HoldAsync(ct) : Task.CompletedTask;
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await PauseAsync(threadId);

        var paused = await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        Assert.True(hold.WasCancelled);
        Assert.Equal(Stage.Handoff, paused.Thread.Stage);
        Assert.Empty(workspace.Pushed);
        Assert.Null(paused.LatestHandoff);

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        var handedOff = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        Assert.Single(workspace.Pushed);
        Assert.Single(workspace.Commits); // the commit made before the pause is not made again
        Assert.NotNull(handedOff.LatestHandoff);
        Assert.Equal([TurnKind.Implement, TurnKind.Review], TurnKinds);
    }
}

/// <summary>What a person's stop does to the run's in-memory state.</summary>
public sealed class ActiveRunStopTests
{
    private static ActiveRun Run() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "openrouter", "model");

    [Fact]
    public void RequestStop_CancelsTheRunsStopToken_AndTheTurn()
    {
        var run = Run();
        var turn = run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", CancellationToken.None);

        run.RequestStop(StopRequest.Pause);

        Assert.True(run.StopToken.IsCancellationRequested);
        Assert.True(turn.IsCancellationRequested);
        Assert.Equal(StopRequest.Pause, run.StopRequest);
    }

    /// <summary>A stop asked for between turns ends the next turn as soon as it begins.</summary>
    [Fact]
    public void ATurnBegunAfterAStop_IsAlreadyCancelled()
    {
        var run = Run();
        run.RequestStop(StopRequest.Cancel);

        Assert.True(run.BeginTurn(TurnKind.Review, TurnKind.Review, "s", CancellationToken.None).IsCancellationRequested);
    }

    [Fact]
    public void ACancel_IsNeverTurnedBackIntoAPause()
    {
        var run = Run();

        run.RequestStop(StopRequest.Cancel);
        run.RequestStop(StopRequest.Pause);

        Assert.Equal(StopRequest.Cancel, run.StopRequest);
    }

    [Fact]
    public void APause_CanBecomeACancel()
    {
        var run = Run();

        run.RequestStop(StopRequest.Pause);
        run.RequestStop(StopRequest.Cancel);

        Assert.Equal(StopRequest.Cancel, run.StopRequest);
    }

    /// <summary>A recorded decision ends the turn without stopping the run.</summary>
    [Fact]
    public void CancelTurn_LeavesTheRunGoing()
    {
        var run = Run();
        var turn = run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", CancellationToken.None);

        run.CancelTurn();

        Assert.True(turn.IsCancellationRequested);
        Assert.False(run.StopToken.IsCancellationRequested);
        Assert.Equal(StopRequest.None, run.StopRequest);
    }
}
