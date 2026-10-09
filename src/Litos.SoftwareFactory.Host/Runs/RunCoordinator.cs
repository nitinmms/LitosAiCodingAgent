using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// The coordinator (ReadMe_LitosSoftwareFactory_V1.md §7, §8): it wakes when work is queued or
/// on a poll, claims runs under the slot cap, and hands each to the <see cref="RunSupervisor"/>.
/// The runs the registry holds are the busy slots. On startup, and on a sweep while it runs, it
/// marks every Running run no executor owns as Interrupted (<see cref="RunLiveness"/>) — work is
/// never blindly re-run.
/// </summary>
public sealed class RunCoordinator(
    IFactoryStore store, RunSupervisor supervisor, RunRegistry registry, RunLiveness liveness, FactoryOptions options,
    FactorySignals signals, IClock clock, IHostApplicationLifetime lifetime, Settings.FactorySettings settings, ILogger<RunCoordinator> logger) : BackgroundService
{
    /// <summary>Completes once startup recovery is done and the coordinator is claiming work.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // What runs are told when the host stops. The stopping token alone comes too late: the
        // host cancels it only after the web server has gone, and with it the model gateway and
        // the worker callbacks, so a run would first see its model calls refused and record that
        // as the task's failure. Application stopping is signalled before anything stops.
        using var hostStopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        try
        {
            await RecoverAsync(stoppingToken);
            Started.TrySetResult();

            var lastSweep = clock.UtcNow;
            // Nothing new is claimed once the host is stopping.
            while (!hostStopping.IsCancellationRequested)
            {
                try
                {
                    // On this loop, so a run claimed below is always registered before a sweep looks.
                    if (clock.UtcNow - lastSweep >= options.LivenessInterval)
                    {
                        lastSweep = clock.UtcNow;
                        await liveness.SweepAsync(hostStopping.Token);
                    }

                    // Asked even with every slot busy, so each queued thread says what it waits for.
                    // Read on every claim, so a changed cap applies to the next run without a restart.
                    if (await store.ClaimNextRunAsync(settings.Budgets.SlotCap, clock.UtcNow, hostStopping.Token, registry.RunIds) is { } claimed)
                    {
                        signals.EventsWritten();
                        _ = supervisor.Start(claimed, hostStopping.Token);
                        continue; // there may be another to claim
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Losing the database stops new dispatch (§14 rule 5); running commands settle
                    // on their own, and claiming resumes when the database is back.
                    logger.LogError(ex, "Could not claim work; will retry.");
                }

                await signals.WaitForWorkAsync(options.PollInterval, hostStopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Started.TrySetResult();
            await supervisor.DrainAsync();
        }
    }

    /// <summary>Runs still marked Running belong to a host that is gone (§16).</summary>
    internal Task RecoverAsync(CancellationToken ct) => liveness.RecoverAtStartupAsync(ct);
}
