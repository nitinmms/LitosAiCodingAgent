using System.Collections.Concurrent;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Infrastructure.Workers;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// The coordinator (ReadMe_LitosSoftwareFactory_V1.md §7, §8): it wakes when work is queued or
/// on a poll, claims runs under the slot cap, and hands each to a <see cref="RunExecutor"/>.
/// On startup it marks every run that was in progress when the host last stopped as Interrupted
/// — work is never blindly re-run.
/// </summary>
public sealed class RunCoordinator(
    IFactoryStore store, RunExecutor executor, RunRegistry registry, FactoryOptions options, FactorySignals signals, IClock clock,
    ILogger<RunCoordinator> logger) : BackgroundService, IRunControl
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>Completes once startup recovery is done and the coordinator is claiming work.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
            Started.TrySetResult();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (registry.Count < options.SlotCap
                        && await store.ClaimNextRunAsync(options.SlotCap, clock.UtcNow, stoppingToken) is { } claimed)
                    {
                        signals.EventsWritten();
                        var task = Task.Run(() => executor.ExecuteAsync(claimed, stoppingToken), CancellationToken.None);
                        _running[claimed.Run.Id] = task;
                        _ = task.ContinueWith(_ => _running.TryRemove(claimed.Run.Id, out Task? _), TaskScheduler.Default);
                        continue; // there may be another to claim
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Losing the database stops new dispatch (§14 rule 5); running commands settle
                    // on their own, and claiming resumes when the database is back.
                    logger.LogError(ex, "Could not claim work; will retry.");
                }

                await signals.WaitForWorkAsync(options.PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Started.TrySetResult();
            await Task.WhenAll(_running.Values);
        }
    }

    /// <summary>
    /// Runs still marked Running belong to a host that is gone. Their workers exit with their
    /// host, but a worker that somehow survived is killed first — liveness is checked by process
    /// id and start time together, because ids are reused (§16).
    /// </summary>
    internal async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var claimed in await store.ListRunningAsync(ct))
        {
            var run = claimed.Run;
            if (run.WorkerProcessId is { } processId && run.WorkerStartTime is { } startTime && WorkerLiveness.IsAlive(processId, startTime))
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(processId);
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }

            try
            {
                await store.StopRunAsync(
                    new StopRunCommand(run.Id, LifecycleTrigger.Interrupt, StopReason.Interrupted,
                        "The factory host stopped while this task was running. The working copy and the branch were left as they were. Recover the task to continue.")
                    {
                        Stage = claimed.Thread.Stage,
                    },
                    clock.UtcNow, ct);
                logger.LogWarning("Run {RunId} was in progress at the last shutdown and is now Interrupted.", run.Id);
            }
            catch (StoreConflictException ex)
            {
                logger.LogWarning(ex, "Run {RunId} could not be marked Interrupted.", run.Id);
            }
        }

        signals.EventsWritten();
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
