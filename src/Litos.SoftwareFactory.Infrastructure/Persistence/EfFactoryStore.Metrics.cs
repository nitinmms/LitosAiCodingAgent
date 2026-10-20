using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Settings;
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

    public async Task<LessonData> GetLessonMetricsDataAsync(IReadOnlyCollection<Guid>? projectIds, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var lessons = await db.Lessons.AsNoTracking().Where(l => projectIds == null || projectIds.Contains(l.ProjectId)).ToListAsync(ct);
        var lessonIds = lessons.Select(l => l.Id).ToList();
        var revisions = (await db.LessonRevisions.AsNoTracking().Where(r => lessonIds.Contains(r.LessonId)).ToListAsync(ct))
            .ToDictionary(r => (r.LessonId, r.Revision));

        // Dates filtered after reading, as in GetFlowDataAsync: SQLite cannot compare a DateTimeOffset in a query.
        var threadRows = await db.Threads.AsNoTracking()
            .Where(t => projectIds == null || projectIds.Contains(t.ProjectId))
            .Select(t => new { t.Id, t.ProjectId, t.Title, t.State, t.CreatedAt })
            .ToListAsync(ct);
        var visibleIds = threadRows.Select(t => t.Id).ToList();
        var runs = await db.Runs.AsNoTracking().Where(r => visibleIds.Contains(r.ThreadId)).Select(r => new { r.Id, r.ThreadId, r.Kind }).ToListAsync(ct);
        var threadOfRun = runs.ToDictionary(r => r.Id, r => r.ThreadId);
        var runIds = runs.Select(r => r.Id).ToList();
        var snapshots = await db.RunLessons.AsNoTracking()
            .Where(s => runIds.Contains(s.RunId))
            .Select(s => new { s.RunId, s.LessonId, s.Inclusion, s.Position, s.Verdict, s.IncludedAt })
            .ToListAsync(ct);

        var threads = threadRows.Where(t => t.CreatedAt >= since).Select(t => new FlowThread(t.Id, t.ProjectId, t.Title, t.State, t.CreatedAt)).ToList();
        var inPeriod = threads.Select(t => t.Id).ToHashSet();

        return new LessonData(
            [.. lessons.Select(l =>
            {
                // A lesson's figures describe the revision runs are given; one never approved shows its newest.
                var shown = revisions[(l.Id, l.ApprovedRevision ?? l.CurrentRevision)];
                return new LessonRow(
                    l.Id, l.ProjectId, l.Number, l.Status,
                    l.Status == Core.Lessons.LessonStatus.Approved && l.Enabled && l.SupersededById is null && l.ApprovedRevision is not null,
                    shown.Rule, shown.SourceKind, shown.Revision, l.ApprovedRevision, l.ProposedByJobId is not null, l.CreatedAt,
                    l.ApprovedRevision is { } approved ? revisions[(l.Id, approved)].ApprovedAt : null);
            })],
            [.. snapshots.Where(s => s.IncludedAt >= since)
                .Select(s => new LessonGiven(s.LessonId, threadOfRun[s.RunId], s.Inclusion, s.Position, s.Verdict, s.IncludedAt))],
            threads,
            [.. runs.Where(r => inPeriod.Contains(r.ThreadId)).Select(r => new FlowRun(r.ThreadId, r.Kind))]);
    }

    public async Task RecordSkillLoadAsync(Guid runId, string name, DateTimeOffset at, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var trimmed = name.Trim();
        write.Db.RunSkillLoads.Add(new RunSkillLoad
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Name = trimmed.Length > RunSkillLoad.MaxNameChars ? trimmed[..RunSkillLoad.MaxNameChars] : trimmed,
            LoadedAt = at,
        });
        await write.CommitAsync(ct);
    }

    public async Task<SkillData> GetSkillMetricsDataAsync(IReadOnlyCollection<Guid>? projectIds, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        // Dates filtered after reading, as in GetFlowDataAsync: SQLite cannot compare a DateTimeOffset in a query.
        var threads = (await db.Threads.AsNoTracking()
                .Where(t => projectIds == null || projectIds.Contains(t.ProjectId))
                .Select(t => new { t.Id, t.ProjectId, t.TypeLabel, t.State, t.CreatedAt })
                .ToListAsync(ct))
            .Where(t => t.CreatedAt >= since)
            .ToList();
        var ids = threads.Select(t => t.Id).ToList();
        var runs = await db.Runs.AsNoTracking()
            .Where(r => ids.Contains(r.ThreadId))
            .Select(r => new { r.Id, r.ThreadId, r.Kind, r.CapabilitiesJson })
            .ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var calls = (await db.Usage.AsNoTracking()
                .Where(u => u.RunId != null && runIds.Contains(u.RunId.Value))
                .GroupBy(u => u.RunId)
                .Select(g => new { RunId = g.Key!.Value, Calls = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(c => c.RunId, c => c.Calls);
        var loads = await db.RunSkillLoads.AsNoTracking()
            .Where(l => runIds.Contains(l.RunId))
            .Select(l => new SkillLoad(l.RunId, l.Name))
            .ToListAsync(ct);
        var repository = await db.RepositorySkills.AsNoTracking()
            .Where(s => projectIds == null || projectIds.Contains(s.ProjectId))
            .Select(s => new { s.ProjectId, s.Name, Chars = s.Content.Length, s.LastFoundAt, Approved = s.ApprovedAt != null })
            .ToListAsync(ct);

        return new SkillData(
            [.. threads.Select(t => new SkillThread(t.Id, t.ProjectId, t.TypeLabel, t.State))],
            [.. runs.Select(r => SkillRunOf(r.Id, r.ThreadId, r.Kind, calls.GetValueOrDefault(r.Id), r.CapabilitiesJson))],
            loads,
            [.. repository.Select(s => new RepositorySkillVersion(s.ProjectId, s.Name, s.Chars, s.LastFoundAt, s.Approved))],
            new Dictionary<string, int>());
    }

    /// <summary>What a run was given of the skills, from the capabilities it recorded when it started.</summary>
    private static SkillRun SkillRunOf(Guid runId, Guid threadId, RunKind kind, int calls, string? capabilitiesJson)
    {
        RunCapabilities? capabilities = null;
        if (capabilitiesJson is { Length: > 0 })
        {
            try
            {
                capabilities = JsonSerializer.Deserialize<RunCapabilities>(capabilitiesJson, FactoryWire.Json);
            }
            catch (JsonException)
            {
                // A run whose record cannot be read counts as given nothing.
            }
        }

        if (capabilities is null)
            return new SkillRun(runId, threadId, kind, calls, [], [], new HashSet<string>());
        var fromRepository = (capabilities.RepositorySkills ?? []).Where(s => s.Used).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        return new SkillRun(
            runId, threadId, kind, calls,
            [.. capabilities.TaskSkills.Select(s => new GivenSkill(s.Name, s.Text.Length))],
            [.. capabilities.LibrarySkills.Select(s => s.Name).Concat(fromRepository).Distinct(StringComparer.Ordinal)],
            fromRepository);
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
