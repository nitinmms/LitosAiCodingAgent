using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Infrastructure.Workers;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// One rule, applied at startup and on a sweep while the host runs (§16): a run marked Running
/// that no executor in this host owns is Interrupted. Its slot is freed, its lease kept, and it is
/// never re-run without a person. A worker it left behind is killed first.
///
/// A run whose executor is alive is never touched, even if its worker has died: the executor
/// already ends the turn as a failure or starts a new worker, so each run has one writer
/// (docs/software-factory/m2-architecture.md §3.2).
/// </summary>
public sealed class RunLiveness(
    IFactoryStore store, RunRegistry registry, IHostInstanceLock hostLock, IHostApplicationLifetime lifetime, FactorySignals signals,
    IClock clock, ILogger<RunLiveness> logger)
{
    internal const string StartupMessage =
        "The factory host stopped while this task was running. The working copy and the branch were left as they were. Recover the task to continue.";

    internal const string SweepMessage =
        "The factory lost track of this task while it was running. The working copy and the branch were left as they were. Recover the task to continue.";

    /// <summary>
    /// Every run marked Running belongs to a host that is gone. Calls a previous host had in
    /// flight can never report their usage now, so they are charged their estimate.
    /// </summary>
    public async Task RecoverAtStartupAsync(CancellationToken ct)
    {
        foreach (var claimed in await store.ListRunningAsync(ct))
            await InterruptAsync(claimed, StartupMessage, reconcile: false, ct);

        var reconciled = await store.ReconcileUsageAsync(runId: null, clock.UtcNow, ct);
        if (reconciled > 0)
            logger.LogWarning("{Count} model call(s) left without reported usage were charged their input estimate.", reconciled);

        signals.EventsWritten();
    }

    /// <summary>
    /// Interrupts the Running runs this host no longer owns: for example one whose stop could not
    /// be recorded because the database failed at that moment. Without this, such a run would
    /// hold its slot until the host restarts. Returns how many it interrupted.
    /// </summary>
    /// <remarks>Run on the coordinator's loop, which also claims and registers runs, so a run
    /// that is claimed but not yet registered is never seen here.</remarks>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        // Another host may have taken the database: stop rather than act on it.
        if (!await hostLock.IsHeldAsync(ct))
        {
            logger.LogCritical("The factory host lost its lock on the database; another host may be using it. Stopping.");
            lifetime.StopApplication();
            return 0;
        }

        var interrupted = 0;
        foreach (var claimed in await store.ListRunningAsync(ct))
        {
            if (registry.Find(claimed.Run.Id) is not null)
                continue;

            logger.LogWarning("Run {RunId} is marked Running but no executor in this host owns it.", claimed.Run.Id);
            if (await InterruptAsync(claimed, SweepMessage, reconcile: true, ct))
                interrupted++;
        }

        if (interrupted > 0)
        {
            signals.EventsWritten();
            signals.WorkQueued(); // their slots are free
        }

        return interrupted;
    }

    private async Task<bool> InterruptAsync(ClaimedRun claimed, string message, bool reconcile, CancellationToken ct)
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
                new StopRunCommand(run.Id, LifecycleTrigger.Interrupt, StopReason.Interrupted, message) { Stage = claimed.Thread.Stage },
                clock.UtcNow, ct);
            logger.LogWarning("Run {RunId} is now Interrupted.", run.Id);
        }
        catch (StoreConflictException ex)
        {
            logger.LogWarning(ex, "Run {RunId} could not be marked Interrupted.", run.Id);
            return false;
        }

        // Nothing can report this run's calls any more, not even those still marked Reserved.
        if (reconcile)
            await store.ReconcileUsageAsync(run.Id, clock.UtcNow, ct, includeInFlight: true);
        return true;
    }
}
