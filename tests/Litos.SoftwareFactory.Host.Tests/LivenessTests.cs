using System.Diagnostics;
using System.Net;
using System.Reflection;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The liveness rule while the host runs (§16, docs/software-factory/m2-architecture.md §3.2): a
/// run marked Running that no executor in this host owns is Interrupted, never re-run, and a
/// host that has lost its database lock stops.
/// </summary>
public sealed class LivenessTests
{
    private static async Task<(TestHost Host, Guid ThreadId, ClaimedRun Orphan)> OrphanAsync(Action<IServiceCollection>? services = null)
    {
        var host = await TestHost.StartAsync(services: services, startCoordinator: false);
        var projectId = await host.RegisterProjectAsync();
        var threadId = await host.CreateThreadAsync(projectId);
        await host.DelegateAsync(threadId);
        // Claimed in the database, but no executor in this host holds it.
        var orphan = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        return (host, threadId, orphan);
    }

    private static RunLiveness Liveness(TestHost host) => host.App.Services.GetRequiredService<RunLiveness>();

    private static async Task<int> LeaseCountAsync(TestHost host)
    {
        await using var db = await host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync();
        return await db.Leases.CountAsync();
    }

    [Fact]
    public async Task Sweep_ARunNoExecutorOwns_IsInterrupted_KeepsItsLease_AndSettlesItsCallsInFlight()
    {
        var (host, threadId, orphan) = await OrphanAsync();
        await using var _ = host;
        await host.Store.ReserveAsync(
            new ReserveCommand("in-flight", threadId, orphan.Run.Id, orphan.Run.RequestedBy, "openrouter", "model", 10_000, 10_000),
            new BudgetPolicy { OutputAllowanceTokens = 4_000, Margin = 0.10 }, DateTimeOffset.UtcNow, default);

        Assert.Equal(1, await Liveness(host).SweepAsync(default));

        var details = await host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.Interrupted, details.Thread.State);
        Assert.Contains("lost track of this task", details.Thread.StateReason);
        Assert.Equal((RunStatus.Suspended, StopReason.Interrupted), (details.LatestRun!.Status, details.LatestRun.StopReason));
        Assert.Equal(1, await LeaseCountAsync(host)); // its uncommitted work still owns the repository
        var call = Assert.Single(await host.Store.ListUsageAsync(threadId, default));
        Assert.Equal(UsageStatus.Estimated, call.Status);
        Assert.Equal(0, (await host.ThreadAsync(threadId)).Thread.TokensReserved);

        // Never re-run without a person; recovering it queues it.
        Assert.Equal(0, await Liveness(host).SweepAsync(default));
        await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        Assert.Equal(LifecycleState.Queued, (await host.ThreadAsync(threadId)).Thread.State);
    }

    [Fact]
    public async Task Sweep_LeavesARunWhoseExecutorIsAlive()
    {
        await using var host = await TestHost.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // Even with its worker gone: the executor deals with that itself.
        host.Workers.Workers.Single().Kill();

        Assert.Equal(0, await Liveness(host).SweepAsync(default));
        Assert.Equal(LifecycleState.Running, (await host.ThreadAsync(threadId)).Thread.State);

        await host.PostAsync($"api/threads/{threadId}/cancel", null, HttpStatusCode.Accepted);
        await host.WaitForStateAsync(threadId, LifecycleState.Cancelled);
    }

    [Fact]
    public async Task Sweep_KillsAWorkerTheRunLeftBehind()
    {
        var (host, threadId, orphan) = await OrphanAsync();
        await using var _ = host;
        using var worker = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }
            : new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        try
        {
            await host.Store.SetRunWorkerAsync(orphan.Run.Id, worker.Id, new DateTimeOffset(worker.StartTime), default);

            Assert.Equal(1, await Liveness(host).SweepAsync(default));

            Assert.True(worker.WaitForExit(10_000), "The left-behind worker was not killed.");
            Assert.Equal(LifecycleState.Interrupted, (await host.ThreadAsync(threadId)).Thread.State);
        }
        finally
        {
            if (!worker.HasExited)
                worker.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Another host may own the database now: acting on its runs would break them.</summary>
    [Fact]
    public async Task Sweep_AfterTheHostLockIsLost_StopsTheHost_AndInterruptsNothing()
    {
        var (host, threadId, _) = await OrphanAsync(s => s.AddSingleton<IHostInstanceLock>(new ScriptedLock { Held = false }));
        await using var _ = host;

        Assert.Equal(0, await Liveness(host).SweepAsync(default));

        Assert.True(host.App.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.Equal(LifecycleState.Running, (await host.ThreadAsync(threadId)).Thread.State);
    }

    [Fact]
    public async Task Prepare_RefusesToStart_WhenAnotherHostHoldsTheDatabase()
    {
        var hostLock = new ScriptedLock();
        await using var host = await TestHost.StartAsync(services: s => s.AddSingleton<IHostInstanceLock>(hostLock), startCoordinator: false);
        hostLock.Refusal = "Another factory host is already running against this database.";

        Assert.Equal(hostLock.Refusal, await FactoryHostApp.PrepareAsync(host.App, host.Options, default));
    }

    /// <summary>
    /// The case the sweep exists for. The database fails at the moment a run stops, so its stop
    /// cannot be recorded: the executor ends, but the run stays marked Running. The sweep then
    /// interrupts it, and the person can recover it.
    /// </summary>
    [Fact]
    public async Task ARunWhoseStopCouldNotBeRecorded_IsInterruptedByTheSweep_AndCanBeRecovered()
    {
        await using var host = await TestHost.StartAsync(
            o => o.LivenessInterval = TimeSpan.FromMilliseconds(300),
            s => s.AddSingleton<IFactoryStore>(sp => StopFailingStore.Wrap(new EfFactoryStore(sp.GetRequiredService<IDbContextFactory<FactoryDbContext>>()), failures: 2)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "in progress\n");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // The pause cannot be recorded, and neither can the block the executor falls back to.
        await host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);

        var interrupted = await host.WaitForStateAsync(threadId, LifecycleState.Interrupted);
        Assert.Contains("lost track of this task", interrupted.Thread.StateReason);

        await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
    }

    private sealed class ScriptedLock : IHostInstanceLock
    {
        public bool Held { get; init; } = true;

        public string? Refusal { get; set; }

        public Task<string?> AcquireAsync(CancellationToken ct) => Task.FromResult(Refusal);

        public Task<bool> IsHeldAsync(CancellationToken ct) => Task.FromResult(Held);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The real store, except that its first few StopRunAsync calls fail as if the database
/// had gone away.</summary>
public class StopFailingStore : DispatchProxy
{
    private IFactoryStore _inner = null!;
    private int _failures;

    public static IFactoryStore Wrap(IFactoryStore inner, int failures)
    {
        var store = Create<IFactoryStore, StopFailingStore>();
        var proxy = (StopFailingStore)(object)store;
        proxy._inner = inner;
        proxy._failures = failures;
        return store;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IFactoryStore.StopRunAsync) && Interlocked.Decrement(ref _failures) >= 0)
            return Task.FromException(new InvalidOperationException("The database is unavailable."));

        try
        {
            return method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
