using System.Collections.Concurrent;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// Owns the lifetime of every run this host executes. A claimed run is held in the
/// <see cref="RunRegistry"/> before its executor starts, and released only after the executor
/// has finished: after the run's stop is recorded, its worker shut down and its usage
/// reconciled. A run paused and resumed in the meantime therefore waits for the old executor to
/// be gone before the claim can start it again (docs/software-factory/m2-architecture.md §3.2).
/// </summary>
public sealed class RunSupervisor(
    RunRegistry registry, RunExecutor executor, ChatExecutor chat, SpecExecutor spec, FactorySignals signals, ILogger<RunSupervisor> logger)
    : IRunControl
{
    private readonly ConcurrentDictionary<ActiveRun, Task> _running = new();

    /// <summary>Holds a run that was just claimed, and executes it.</summary>
    /// <returns>The run's execution, which completes once its slot is released.</returns>
    /// <exception cref="InvalidOperationException">This host already holds the run: the claim
    /// started it twice, which the held runs it is given must prevent.</exception>
    public Task Start(ClaimedRun claimed, CancellationToken hostStopping)
    {
        var (run, thread, _) = claimed;
        var active = new ActiveRun(run.Id, thread.Id, run.RequestedBy, thread.Provider, thread.Model, isChat: run.Kind == RunKind.Chat);
        if (!registry.TryAdd(active))
            throw new InvalidOperationException($"Run {run.Id} was claimed while this host still holds it.");

        var task = Task.Run(() => RunAsync(claimed, active, hostStopping), CancellationToken.None);
        _running[active] = task;
        _ = task.ContinueWith(_ => _running.TryRemove(active, out Task? _), TaskScheduler.Default);
        return task;
    }

    /// <summary>Waits for every run this host is executing to finish, for shutdown.</summary>
    public Task DrainAsync() => Task.WhenAll(_running.Values);

    private async Task RunAsync(ClaimedRun claimed, ActiveRun active, CancellationToken hostStopping)
    {
        try
        {
            if (active.IsChat)
                await chat.ExecuteAsync(claimed, active, hostStopping);
            else if (claimed.Run.Kind == RunKind.Spec)
                await spec.ExecuteAsync(claimed, active, hostStopping);
            else
                await executor.ExecuteAsync(claimed, active, hostStopping);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} ended with an error its executor did not handle.", active.RunId);
        }
        finally
        {
            // Last: the slot is free, and a resumed run may be claimed, only now.
            registry.Remove(active);
            signals.EventsWritten();
            signals.WorkQueued();
        }
    }

    public bool RequestStop(Guid threadId, StopRequest request)
    {
        if (registry.FindByThread(threadId) is not { } active)
            return false;

        active.RequestStop(request);
        return true;
    }

    public async Task SteerAsync(Guid threadId, string text, CancellationToken ct)
    {
        if (registry.FindByThread(threadId) is { Client: { } client, SessionId: { } sessionId })
        {
            try
            {
                await client.SteerAsync(sessionId, text, ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "A follow-up for thread {ThreadId} could not be delivered to its worker.", threadId);
            }
        }
    }
}
