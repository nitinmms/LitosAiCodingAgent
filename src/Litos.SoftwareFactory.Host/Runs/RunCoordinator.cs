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
    FactorySignals signals, IClock clock, ILogger<RunCoordinator> logger) : BackgroundService
{
    /// <summary>Completes once startup recovery is done and the coordinator is claiming work.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
            Started.TrySetResult();

            var lastSweep = clock.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // On this loop, so a run claimed below is always registered before a sweep looks.
                    if (clock.UtcNow - lastSweep >= options.LivenessInterval)
                    {
                        lastSweep = clock.UtcNow;
                        await liveness.SweepAsync(stoppingToken);
                    }

                    // Asked even with every slot busy, so each queued thread says what it waits for.
                    if (await store.ClaimNextRunAsync(options.SlotCap, clock.UtcNow, stoppingToken, registry.RunIds) is { } claimed)
                    {
                        signals.EventsWritten();
                        _ = supervisor.Start(claimed, stoppingToken);
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
            await supervisor.DrainAsync();
        }
    }

    /// <summary>Runs still marked Running belong to a host that is gone (§16).</summary>
    internal Task RecoverAsync(CancellationToken ct) => liveness.RecoverAtStartupAsync(ct);
}
