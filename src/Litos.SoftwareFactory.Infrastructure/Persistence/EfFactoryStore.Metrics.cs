using System.Text.Json;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Metrics;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>The rows the flow metrics are computed from (m4-architecture.md §6): nothing new is stored for them.</summary>
public sealed partial class EfFactoryStore
{
    public async Task<FlowData> GetFlowDataAsync(IReadOnlyCollection<Guid>? projectIds, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        // Filtered by date after reading: SQLite cannot compare a DateTimeOffset in a query.
        var visible = await db.Threads.AsNoTracking()
            .Where(t => projectIds == null || projectIds.Contains(t.ProjectId))
            .Select(t => new { t.Id, t.ProjectId, t.Title, t.State, t.CreatedAt })
            .ToListAsync(ct);
        var threads = visible.Where(t => t.CreatedAt >= since).Select(t => new FlowThread(t.Id, t.ProjectId, t.Title, t.State, t.CreatedAt)).ToList();
        var ids = threads.Select(t => t.Id).ToList();

        var events = (await db.Outbox.AsNoTracking()
                .Where(e => ids.Contains(e.ThreadId) && e.Type == EventTypes.StateChanged)
                .Select(e => new { e.ThreadId, e.CreatedAt, e.PayloadJson })
                .ToListAsync(ct))
            .Select(e => Parse(e.ThreadId, e.CreatedAt, e.PayloadJson))
            .OfType<FlowStateEvent>()
            .ToList();

        var usage = await db.Usage.AsNoTracking()
            .Where(u => ids.Contains(u.ThreadId))
            .Select(u => new FlowUsage(u.ThreadId, u.Phase, u.Model, u.Charged))
            .ToListAsync(ct);
        var runs = await db.Runs.AsNoTracking().Where(r => ids.Contains(r.ThreadId)).Select(r => new { r.Id, r.ThreadId, r.Kind }).ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var lessons = await db.RunLessons.AsNoTracking()
            .Where(s => runIds.Contains(s.RunId))
            .Select(s => new { s.RunId, s.Inclusion, s.Verdict })
            .ToListAsync(ct);

        // Open decisions are what waits now, on any visible unfinished task, not only those created in the period.
        var visibleIds = visible.Where(t => !TaskLifecycle.IsTerminal(t.State)).Select(t => t.Id).ToList();
        var decisions = await db.Decisions.AsNoTracking()
            .Where(d => visibleIds.Contains(d.ThreadId) && d.Status == DecisionStatus.Open)
            .Select(d => new { d.ThreadId, d.Question, d.CreatedAt })
            .ToListAsync(ct);
        var titles = visible.ToDictionary(t => t.Id, t => t.Title);

        return new FlowData(
            threads,
            events,
            usage,
            [.. runs.Select(r => new FlowRun(r.ThreadId, r.Kind))],
            [.. decisions.Select(d => new FlowDecision(d.ThreadId, titles[d.ThreadId], d.Question, d.CreatedAt, true))],
            [.. lessons.Select(l => new FlowLessonUse(runs.First(r => r.Id == l.RunId).ThreadId, l.Inclusion, l.Verdict))]);
    }

    private static FlowStateEvent? Parse(Guid threadId, DateTimeOffset at, string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            return root.TryGetProperty("state", out var state) && root.TryGetProperty("stage", out var stage)
                ? new FlowStateEvent(threadId, at, state.GetString() ?? "", stage.GetString() ?? "",
                    root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
