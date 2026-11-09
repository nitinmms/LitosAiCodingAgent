using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// Watches the pull requests other tasks wait for (docs/software-factory/run-order-architecture.md).
/// The claim never calls GitHub: it reads what this records. A merge into the project's default
/// branch releases the waiting tasks and wakes the coordinator; a pull request closed unmerged asks
/// their owners what to do; a merge into another branch is not the work reaching the base those
/// tasks start from, so they keep waiting. It also reminds owners of tasks waited on for long.
/// </summary>
public sealed class MergeWatcher(
    IFactoryStore store, FactorySignals signals, IClock clock, FactoryOptions options, ILogger<MergeWatcher> logger, IGitHub? gitHub = null)
    : BackgroundService
{
    /// <summary>One look: every awaited pull request, then the reminders. Returns how many pull
    /// requests changed what a task waits for.</summary>
    public async Task<int> CheckAsync(CancellationToken ct)
    {
        var changed = 0;
        if (gitHub is { IsConfigured: true })
        {
            foreach (var awaited in await store.ListAwaitedPullRequestsAsync(ct))
            {
                PullRequestDetails details;
                try
                {
                    details = await gitHub.GetPullRequestDetailsAsync(awaited.Owner, awaited.Repository, awaited.Number, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One pull request GitHub will not describe does not stop the others being asked.
                    logger.LogWarning("Pull request {Owner}/{Repository}#{Number} could not be read: {Message}",
                        awaited.Owner, awaited.Repository, awaited.Number, ex.Message);
                    continue;
                }

                if (OutcomeOf(details, awaited.DefaultBranch) is not { } outcome)
                    continue;

                await store.RecordPullRequestOutcomeAsync(awaited.ThreadId, outcome, clock.UtcNow, ct);
                changed++;
            }
        }

        if (await store.RemindOfLongWaitsAsync(options.BlockerReminderAfter, clock.UtcNow, ct) > 0 || changed > 0)
        {
            signals.EventsWritten();
            if (changed > 0)
                signals.WorkQueued();
        }

        return changed;
    }

    /// <summary>What a pull request's details mean for the tasks waiting on it; null for nothing yet.
    /// A branch GitHub does not name is taken as the default one, the base the factory opened it on.</summary>
    public static PullRequestOutcome? OutcomeOf(PullRequestDetails details, string defaultBranch) => details.State switch
    {
        PullRequestState.Merged when details.BaseBranch is null || details.BaseBranch == defaultBranch => PullRequestOutcome.Merged,
        PullRequestState.Closed => PullRequestOutcome.Closed,
        _ => null,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking awaited pull requests failed; it runs again in {Interval}.", options.MergeCheckInterval);
            }

            try
            {
                await Task.Delay(options.MergeCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
