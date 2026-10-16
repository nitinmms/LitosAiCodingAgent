using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Metrics;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>What the operational counters are read from (m4-architecture.md §7).</summary>
public sealed partial class EfFactoryStore
{
    public async Task<OperationsData> GetOperationsDataAsync(DateTimeOffset failuresSince, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var states = await db.Threads.AsNoTracking().GroupBy(t => t.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var titles = await db.Threads.AsNoTracking().Select(t => new { t.Id, t.Title }).ToDictionaryAsync(t => t.Id, t => t.Title, ct);

        var running = await db.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatus.Running)
            .Select(r => new { r.Id, r.ThreadId, r.Kind, r.StartedAt, r.HeartbeatAt, r.WorkerProcessId, r.WorkerStartTime })
            .ToListAsync(ct);

        // Reserved or unknown, on a run no executor is working (finished, or stopped until someone acts):
        // nothing will settle them now but reconciliation.
        var finished = db.Runs.Where(r => r.Status != RunStatus.Running && r.Status != RunStatus.Queued).Select(r => r.Id);
        var unsettled = await db.Usage.AsNoTracking()
            .Where(u => (u.Status == UsageStatus.Reserved || u.Status == UsageStatus.Unknown) && u.RunId != null && finished.Contains(u.RunId.Value))
            .Select(u => new { u.Reserved, u.CreatedAt })
            .ToListAsync(ct);

        // Read whole and filtered after: SQLite cannot compare a DateTimeOffset in every query shape.
        var handoffFailures = (await db.Runs.AsNoTracking()
                .Where(r => r.StopReason == StopReason.HandoffFailed)
                .Select(r => new { r.ThreadId, r.EndedAt, r.CreatedAt })
                .ToListAsync(ct))
            .Select(r => (r.ThreadId, At: r.EndedAt ?? r.CreatedAt))
            .Where(r => r.At >= failuresSince)
            .OrderByDescending(r => r.At)
            .Select(r => new HandoffFailure(r.ThreadId, titles.GetValueOrDefault(r.ThreadId, ""), r.At))
            .ToList();

        var reflections = await db.ReflectionJobs.AsNoTracking().GroupBy(j => j.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var decisions = (await db.Decisions.AsNoTracking()
            .Where(d => d.Status == DecisionStatus.Open)
            .Select(d => new { d.ThreadId, d.Question, d.CreatedAt })
            .ToListAsync(ct)).OrderBy(d => d.CreatedAt).ToList();

        var (dayStart, monthStart) = BudgetLedger.QuotaWindowStarts(now);
        var users = await db.Usage.AsNoTracking()
            .Where(u => u.CreatedAt >= monthStart)
            .GroupBy(u => u.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                MonthUsed = g.Sum(u => u.Status == UsageStatus.Settled || u.Status == UsageStatus.Estimated ? u.Charged : 0),
                MonthReserved = g.Sum(u => u.Status == UsageStatus.Reserved || u.Status == UsageStatus.Unknown ? u.Reserved : 0),
                DayUsed = g.Sum(u => u.CreatedAt >= dayStart && (u.Status == UsageStatus.Settled || u.Status == UsageStatus.Estimated) ? u.Charged : 0),
                DayReserved = g.Sum(u => u.CreatedAt >= dayStart && (u.Status == UsageStatus.Reserved || u.Status == UsageStatus.Unknown) ? u.Reserved : 0),
            })
            .ToListAsync(ct);

        return new OperationsData(
            states.ToDictionary(s => s.Key, s => s.Count),
            [.. running.Select(r => new RunningRun(r.Id, r.ThreadId, titles.GetValueOrDefault(r.ThreadId, ""), r.Kind, r.StartedAt, r.HeartbeatAt, r.WorkerProcessId, r.WorkerStartTime))],
            new UnsettledUsage(unsettled.Count, unsettled.Sum(u => u.Reserved), unsettled.Count == 0 ? null : unsettled.Min(u => u.CreatedAt)),
            handoffFailures,
            reflections.ToDictionary(r => r.Key, r => r.Count),
            decisions.FirstOrDefault() is { } oldest ? new PendingDecision(oldest.ThreadId, titles.GetValueOrDefault(oldest.ThreadId, ""), oldest.Question, oldest.CreatedAt) : null,
            decisions.Count,
            [.. users.OrderByDescending(u => u.MonthUsed + u.MonthReserved).Select(u => new UserUsage(u.UserId, u.DayUsed, u.DayReserved, u.MonthUsed, u.MonthReserved))]);
    }
}
