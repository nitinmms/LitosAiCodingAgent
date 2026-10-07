using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// More than one run at a time (docs/software-factory/m2-architecture.md §3.2): a run holds its
/// slot until its executor has finished, so a run resumed while its old worker is still shutting
/// down is not started twice, and runs on different repositories proceed side by side.
/// </summary>
public sealed class ConcurrencyTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync(o => o.SlotCap = 2);

    public async Task DisposeAsync()
    {
        _host.Workers.ShutdownGate = null;
        await _host.DisposeAsync();
    }

    private RunRegistry Registry => _host.App.Services.GetRequiredService<RunRegistry>();

    /// <summary>Scripts the next turn to edit a file, say it has started, and run until stopped.</summary>
    private TaskCompletionSource HangTheNextTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "in progress\n");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        return started;
    }

    private async Task<Guid> DelegatedAsync(string repository, string title = "Add CSV export")
    {
        var projectId = await _host.RegisterProjectAsync(repository);
        var threadId = await _host.CreateThreadAsync(projectId, title);
        await _host.DelegateAsync(threadId);
        return threadId;
    }

    /// <summary>Waits for the stored thread to satisfy a condition, whatever the registry holds.</summary>
    private async Task<TaskThread> WaitForThreadAsync(Guid threadId, Func<TaskThread, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var thread = (await _host.ThreadAsync(threadId)).Thread;
            if (condition(thread))
                return thread;
            Assert.True(DateTime.UtcNow < deadline, $"Thread is {thread.State} ({thread.StateReason}), not {what}.");
            await Task.Delay(40);
        }
    }

    /// <summary>
    /// The resume race. Before M2 the old executor removed the run from the registry by its id
    /// after its worker had shut down; with a second slot free, a resumed run was claimed in the
    /// meantime, and that removal deleted the new run's entry: its worker's callbacks were refused
    /// and the task could no longer be paused.
    /// </summary>
    [Fact]
    public async Task ResumedWhileTheOldWorkerIsShuttingDown_StartsOnlyOnceItHasGone_AndThenFinishes()
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Workers.ShutdownGate = shutdown.Task;
        var started = HangTheNextTurn();
        var threadId = await DelegatedAsync("salesapp");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var runId = (await _host.ThreadAsync(threadId)).LatestRun!.Id;
        var first = Registry.Find(runId);

        await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        // The stop is recorded at once; the worker is still shutting down.
        await WaitForThreadAsync(threadId, t => t.State == LifecycleState.PausedUser, "paused");
        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);

        await Task.Delay(TimeSpan.FromMilliseconds(600)); // several claim polls
        Assert.Equal(LifecycleState.Queued, (await _host.ThreadAsync(threadId)).Thread.State);
        Assert.Same(first, Registry.Find(runId));
        Assert.Single(_host.Workers.Workers);

        _host.Workers.ShutdownGate = null;
        shutdown.SetResult();

        // The new worker's callbacks are accepted: the resumed run submits, is reviewed and hands off.
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var workers = _host.Workers.Workers.ToArray();
        Assert.Equal(2, workers.Length);
        Assert.NotEqual(workers[0].Launch.Secret, workers[1].Launch.Secret);
        Assert.Equal([TurnKind.Implement, TurnKind.Implement, TurnKind.Review], _host.Workers.Turns.Select(t => t.Kind));
        Assert.Null(Registry.Find(runId));
    }

    /// <summary>A pause while the run is still held reaches that run, not a stale one.</summary>
    [Fact]
    public async Task ResumedRun_CanBePausedAgain()
    {
        var first = HangTheNextTurn();
        var threadId = await DelegatedAsync("salesapp");
        await first.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);

        var second = HangTheNextTurn();
        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
    }

    [Fact]
    public async Task TwoRepositories_RunAtTheSameTime_AndAThirdWaitsForAFreeSlot()
    {
        var startedA = HangTheNextTurn();
        var a = await DelegatedAsync("repo-a", "Task A");
        await startedA.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var startedB = HangTheNextTurn();
        var b = await DelegatedAsync("repo-b", "Task B");
        await startedB.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(LifecycleState.Running, (await _host.ThreadAsync(a)).Thread.State);
        Assert.Equal(LifecycleState.Running, (await _host.ThreadAsync(b)).Thread.State);
        Assert.Equal(2, Registry.Count);
        Assert.Equal(2, _host.Workers.Workers.Select(w => w.Launch.Secret).Distinct().Count());

        var c = await DelegatedAsync("repo-c", "Task C");
        await WaitForThreadAsync(c, t => t.StateReason == "Waiting for a free slot (2 of 2 busy).", "waiting for a slot");

        await _host.PostAsync($"api/threads/{a}/cancel", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(c, LifecycleState.AwaitingHumanTesting);
        Assert.Null((await _host.ThreadAsync(c)).Thread.StateReason);
        Assert.Equal(LifecycleState.Running, (await _host.ThreadAsync(b)).Thread.State);

        await _host.PostAsync($"api/threads/{b}/cancel", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(b, LifecycleState.Cancelled);
    }

    /// <summary>Two tasks on one repository never run together, even with a slot free (§6.3).</summary>
    [Fact]
    public async Task SameRepository_WaitsForTheHolder_EvenWithASlotFree()
    {
        var started = HangTheNextTurn();
        var projectId = await _host.RegisterProjectAsync("salesapp");
        var first = await _host.CreateThreadAsync(projectId, "Add CSV export");
        await _host.DelegateAsync(first);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var second = await _host.CreateThreadAsync(projectId, "Fix the footer");
        await _host.DelegateAsync(second);

        await WaitForThreadAsync(second, t => t.StateReason == "Waiting for salesapp — held by \"Add CSV export\" (running).", "waiting for the repository");
        Assert.Equal(1, Registry.Count);

        await _host.PostAsync($"api/threads/{first}/cancel", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(second, LifecycleState.AwaitingHumanTesting);
    }
}

/// <summary>The registry is the slot ledger; the supervisor is the only thing that changes it.</summary>
public sealed class RunRegistryTests
{
    private static ActiveRun Run(Guid runId) => new(runId, Guid.NewGuid(), Guid.NewGuid(), "openrouter", "model");

    [Fact]
    public void TryAdd_RefusesASecondHoldOnTheSameRun()
    {
        var registry = new RunRegistry();
        var runId = Guid.NewGuid();

        Assert.True(registry.TryAdd(Run(runId)));
        Assert.False(registry.TryAdd(Run(runId)));
        Assert.Equal(1, registry.Count);
    }

    /// <summary>The old executor's release must never remove a newer hold on the same run.</summary>
    [Fact]
    public void Remove_ReleasesOnlyThatInstance()
    {
        var registry = new RunRegistry();
        var runId = Guid.NewGuid();
        var old = Run(runId);
        registry.TryAdd(old);
        registry.Remove(old);
        var newer = Run(runId);
        registry.TryAdd(newer);

        Assert.False(registry.Remove(old));
        Assert.Same(newer, registry.Find(runId));
    }

    [Fact]
    public void RunIds_IsASnapshot()
    {
        var registry = new RunRegistry();
        var held = Run(Guid.NewGuid());
        registry.TryAdd(held);

        var snapshot = registry.RunIds;
        registry.Remove(held);

        Assert.Equal([held.RunId], snapshot);
        Assert.Empty(registry.RunIds);
    }
}

public sealed class RunSupervisorTests
{
    /// <summary>The claim is told which runs are held, so starting one twice is a bug, and loud.</summary>
    [Fact]
    public async Task Start_ARunTheHostAlreadyHolds_Throws()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var projectId = await host.RegisterProjectAsync();
        var threadId = await host.CreateThreadAsync(projectId);
        await host.DelegateAsync(threadId);
        var claimed = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        host.App.Services.GetRequiredService<RunRegistry>()
            .TryAdd(new ActiveRun(claimed.Run.Id, threadId, Guid.NewGuid(), "openrouter", "model"));

        var supervisor = host.App.Services.GetRequiredService<RunSupervisor>();

        // It throws before any execution starts, not from the returned task.
        Assert.Throws<InvalidOperationException>(() => { _ = supervisor.Start(claimed, default); });
    }

    [Fact]
    public async Task Start_HoldsTheRunUntilItsExecutorHasFinished()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var projectId = await host.RegisterProjectAsync();
        var threadId = await host.CreateThreadAsync(projectId);
        await host.DelegateAsync(threadId);
        var claimed = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        var registry = host.App.Services.GetRequiredService<RunRegistry>();

        var execution = host.App.Services.GetRequiredService<RunSupervisor>().Start(claimed, default);
        Assert.NotNull(registry.Find(claimed.Run.Id));

        await execution.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(registry.Find(claimed.Run.Id));
        Assert.Equal(LifecycleState.AwaitingHumanTesting, (await host.ThreadAsync(threadId)).Thread.State);
    }
}

/// <summary>FACTORY_VERIFY_CONCURRENCY: how many verifications run at once across all runs.</summary>
public sealed class VerificationGateTests
{
    private static VerificationGate Gate(int permits) => new(new FactoryOptions { VerifyConcurrency = permits });

    [Fact]
    public async Task AFreePermit_IsTakenWithoutWaiting()
    {
        var waited = 0;

        using var permit = await Gate(1).EnterAsync(() => { waited++; return Task.CompletedTask; }, default);

        Assert.Equal(0, waited);
    }

    [Fact]
    public async Task WithAllPermitsTaken_TheNextWaits_SaysSoOnce_AndGoesWhenOneIsReturned()
    {
        var gate = Gate(1);
        var first = await gate.EnterAsync(null, default);
        var waited = 0;

        var second = gate.EnterAsync(() => { waited++; return Task.CompletedTask; }, default);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, waited);

        first.Dispose();
        using var permit = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, waited);
    }

    [Fact]
    public async Task AWaitThatIsCancelled_TakesNothing()
    {
        var gate = Gate(1);
        var first = await gate.EnterAsync(null, default);
        using var cancel = new CancellationTokenSource();

        var waiting = gate.EnterAsync(null, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        first.Dispose();
        var waited = false;
        using var next = await gate.EnterAsync(() => { waited = true; return Task.CompletedTask; }, default);
        Assert.False(waited);
    }

    [Fact]
    public async Task TwoPermits_LetTwoInAtOnce()
    {
        var gate = Gate(2);
        var waited = false;

        using var one = await gate.EnterAsync(() => { waited = true; return Task.CompletedTask; }, default);
        using var two = await gate.EnterAsync(() => { waited = true; return Task.CompletedTask; }, default);

        Assert.False(waited);
    }

    [Fact]
    public async Task AReturnedPermit_IsReturnedOnlyOnce()
    {
        var gate = Gate(1);
        var permit = await gate.EnterAsync(null, default);
        permit.Dispose();
        permit.Dispose();

        using var one = await gate.EnterAsync(null, default);
        var two = gate.EnterAsync(null, default);
        await Task.Delay(100);

        Assert.False(two.IsCompleted);
    }
}

/// <summary>Runs going on at the same time do not share temporary files or verify at once.</summary>
public sealed class RunIsolationTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync(o => o.SlotCap = 2);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task TwoRuns_VerifyOneAtATime_AndTheOneThatWaitsSaysSo()
    {
        var current = 0;
        var most = 0;
        var firstVerify = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        _host.Verifier.Hold = async (request, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref most, now);
            try
            {
                if (request.ChangedFiles is not null && Interlocked.Exchange(ref held, 1) == 0)
                {
                    firstVerify.SetResult();
                    await release.Task.WaitAsync(ct);
                }
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        };

        var a = await _host.CreateThreadAsync(await _host.RegisterProjectAsync("repo-a"), "Task A");
        await _host.DelegateAsync(a);
        await firstVerify.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var b = await _host.CreateThreadAsync(await _host.RegisterProjectAsync("repo-b"), "Task B");
        await _host.DelegateAsync(b);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(await _host.ThreadAsync(b)).Messages.Any(m => m.Text.StartsWith("Waiting for another task's verification", StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < deadline, "Task B never said it was waiting for a verification.");
            await Task.Delay(40);
        }

        release.SetResult();
        await _host.WaitForStateAsync(a, LifecycleState.AwaitingHumanTesting);
        await _host.WaitForStateAsync(b, LifecycleState.AwaitingHumanTesting);
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task EachRun_HasItsOwnTempDirectory_ForItsWorkerAndItsVerification_RemovedWhenItEnds()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        _host.Verifier.Hold = (request, _) =>
        {
            seen.Add(request.TempDirectory!);
            Directory.CreateDirectory(request.TempDirectory!);
            File.WriteAllText(Path.Combine(request.TempDirectory!, "scratch.txt"), "x");
            return Task.CompletedTask;
        };

        var a = await _host.CreateThreadAsync(await _host.RegisterProjectAsync("repo-a"), "Task A");
        var b = await _host.CreateThreadAsync(await _host.RegisterProjectAsync("repo-b"), "Task B");
        await _host.DelegateAsync(a);
        await _host.DelegateAsync(b);
        var runA = (await _host.WaitForStateAsync(a, LifecycleState.AwaitingHumanTesting)).LatestRun!.Id;
        var runB = (await _host.WaitForStateAsync(b, LifecycleState.AwaitingHumanTesting)).LatestRun!.Id;

        var expected = new[] { _host.Options.RunTempDirectory(runA), _host.Options.RunTempDirectory(runB) };
        Assert.Equal(expected.Order(), _host.Workers.Workers.Select(w => w.Launch.TempDirectory!).Order());
        Assert.Equal(expected.Order(), seen.Distinct().Order());
        Assert.All(expected, directory => Assert.False(Directory.Exists(directory)));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
