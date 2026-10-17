using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Litos.SoftwareFactory.Infrastructure.Workers;
using Microsoft.AspNetCore.Identity;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// The operational counters (ReadMe_LitosSoftwareFactory_V1.md §24, m4-architecture.md §7), for Admins:
/// what is queued, running and stopped, what is waiting on people, what the host is holding that nothing
/// will release, and each person's use against the quotas.
/// </summary>
public static class CountersApi
{
    /// <summary>How far back push and PR failures are counted.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromDays(7);

    /// <summary>The states a task is stopped in until someone acts.</summary>
    private static readonly LifecycleState[] Stopped =
        [LifecycleState.PausedUser, LifecycleState.PausedBudget, LifecycleState.Blocked, LifecycleState.Interrupted, LifecycleState.AwaitingDecision];

    public static IEndpointRouteBuilder MapFactoryCounters(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/counters", async (
            IFactoryStore store, RunRegistry registry, EventStreams streams, FactorySettings settings, RestoreCheck restore,
            UserManager<FactoryUser> users, IClock clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var data = await store.GetOperationsDataAsync(now - FailureWindow, now, ct);
            int Count(LifecycleState state) => data.TasksByState.GetValueOrDefault(state);
            var held = registry.RunIds;
            var (open, behind) = streams.Snapshot();
            var budgets = settings.Budgets;
            var dispatch = settings.Dispatch;
            var ids = data.Users.Select(u => u.UserId).ToHashSet();
            if (dispatch.ChangedBy is { } changedBy)
                ids.Add(changedBy);
            var names = users.Users.Where(u => ids.Contains(u.Id)).ToList()
                .ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName! : u.DisplayName);

            return Results.Ok(new
            {
                Dispatch = new
                {
                    dispatch.Paused,
                    dispatch.Reason,
                    dispatch.ChangedAt,
                    ChangedBy = dispatch.ChangedBy is { } by ? names.GetValueOrDefault(by, "(unknown)") : null,
                },
                // Only on a host started on data restored from a backup.
                Restore = restore.Report is { } report
                    ? new
                    {
                        report.BackupTakenAt,
                        report.BackupHostVersion,
                        report.FilesChecked,
                        Problems = report.Problems.Select(p => $"{p.Path} {p.Problem}"),
                        report.UnreadableSecrets,
                        report.CheckedAt,
                    }
                    : null,
                Tasks = new
                {
                    Queued = Count(LifecycleState.Queued),
                    Running = Count(LifecycleState.Running),
                    // Stopped until someone acts, by why.
                    Stopped = Stopped.Select(s => new { State = s.ToString(), Count = Count(s) }).Where(s => s.Count > 0),
                },
                data.OpenDecisions,
                OldestDecision = data.OldestDecision is { } d
                    ? new { d.ThreadId, d.Title, d.Question, AgeHours = Math.Round((now - d.At).TotalHours, 1) }
                    : null,
                // A run marked Running that this host holds no executor for, or whose worker is gone, needs looking at.
                Runs = data.Running.Select(r =>
                {
                    var executed = held.Contains(r.RunId);
                    var workerAlive = r.WorkerProcessId is { } pid && r.WorkerStartTime is { } started && WorkerLiveness.IsAlive(pid, started);
                    return new
                    {
                        r.RunId,
                        r.ThreadId,
                        r.Title,
                        Kind = r.Kind.ToString(),
                        MinutesRunning = r.StartedAt is { } at ? Math.Round((now - at).TotalMinutes, 1) : (double?)null,
                        MinutesSinceCheckpoint = r.LastCheckpointAt is { } beat ? Math.Round((now - beat).TotalMinutes, 1) : (double?)null,
                        Executed = executed,
                        WorkerAlive = workerAlive,
                        // A run between workers (none started yet, or one just stopped) has no live worker and is fine while executed.
                        NeedsAttention = !executed,
                    };
                }),
                Unsettled = new { data.Unsettled.Count, data.Unsettled.Tokens, data.Unsettled.Oldest },
                EventStreams = new { Open = open, SlowestSecondsBehind = Math.Round(behind.TotalSeconds) },
                PushFailures = new { Days = FailureWindow.Days, Count = data.HandoffFailures.Count, Latest = data.HandoffFailures.Take(5) },
                Reflections = new
                {
                    Failed = data.Reflections.GetValueOrDefault(ReflectionStatus.Failed),
                    Deferred = data.Reflections.GetValueOrDefault(ReflectionStatus.DeferredBudget),
                    Pending = data.Reflections.GetValueOrDefault(ReflectionStatus.Pending) + data.Reflections.GetValueOrDefault(ReflectionStatus.Running),
                    Completed = data.Reflections.GetValueOrDefault(ReflectionStatus.Completed),
                },
                Quotas = new { Daily = budgets.DailyUserQuota, Monthly = budgets.MonthlyUserQuota },
                Users = data.Users.Select(u => new
                {
                    u.UserId,
                    Name = names.GetValueOrDefault(u.UserId, "(unknown)"),
                    Day = u.DayUsed + u.DayReserved,
                    Month = u.MonthUsed + u.MonthReserved,
                    DayShare = budgets.DailyUserQuota is { } daily and > 0 ? Math.Round((double)(u.DayUsed + u.DayReserved) / daily, 3) : (double?)null,
                    MonthShare = budgets.MonthlyUserQuota is { } monthly and > 0 ? Math.Round((double)(u.MonthUsed + u.MonthReserved) / monthly, 3) : (double?)null,
                }),
            });
        }).RequireAuthorization(FactoryRoles.Admin);

        return app;
    }
}
