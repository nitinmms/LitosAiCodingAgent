using System.Text.Json;
using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// What runs are given of a project's lessons (m4-architecture.md §4): the candidates retrieval
/// chooses from, each run's snapshot of what it was given, people's verdicts on whether a lesson
/// helped, and the lessons a person picked for a task.
/// </summary>
public sealed partial class EfFactoryStore
{
    public async Task<IReadOnlyList<LessonCandidate>> ListRetrievableLessonsAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await (
            from l in db.Lessons
            join r in db.LessonRevisions on new { LessonId = l.Id, Revision = l.ApprovedRevision ?? 0 } equals new { r.LessonId, r.Revision }
            where l.ProjectId == projectId && l.Status == LessonStatus.Approved && l.Enabled
            select new { l.Id, l.Number, r.Revision, r.Rule, r.TopicsJson, r.PathsJson, r.ApprovedAt, l.UpdatedAt }).AsNoTracking().ToListAsync(ct);
        return
        [
            .. rows.Select(r => new LessonCandidate(
                r.Id, r.Number, r.Revision, r.Rule,
                JsonSerializer.Deserialize<List<string>>(r.TopicsJson) ?? [], JsonSerializer.Deserialize<List<string>>(r.PathsJson) ?? [],
                r.ApprovedAt ?? r.UpdatedAt)),
        ];
    }

    public async Task SaveRunLessonsAsync(Guid runId, IReadOnlyList<LessonPick> picks, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        // Once per run: a resumed run keeps the snapshot it started with.
        if (picks.Count == 0 || await db.RunLessons.AnyAsync(s => s.RunId == runId, ct))
            return;

        db.RunLessons.AddRange(picks.Select((pick, position) => new RunLessonSnapshot
        {
            RunId = runId,
            LessonId = pick.Lesson.LessonId,
            Number = pick.Lesson.Number,
            Revision = pick.Lesson.Revision,
            Rule = pick.Lesson.Rule,
            Position = position,
            Score = pick.Score,
            Inclusion = pick.Inclusion,
            Manual = pick.Manual,
            IncludedAt = now,
        }));
        await write.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<RunLessonSnapshot>> ListRunLessonsAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.RunLessons.AsNoTracking().Where(s => s.RunId == runId).OrderBy(s => s.Position).ToListAsync(ct);
    }

    public async Task<RunLessonSnapshot> SetRunLessonVerdictAsync(
        Guid runId, Guid lessonId, LessonVerdict? verdict, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var snapshot = await db.RunLessons.FirstOrDefaultAsync(s => s.RunId == runId && s.LessonId == lessonId, ct)
            ?? throw new StoreNotFoundException("The run was not given that lesson.");
        if (snapshot.Inclusion == LessonInclusion.NotIncluded)
            throw new StoreConflictException($"L-{snapshot.Number} was left out of this run, so it cannot have helped or misled it.");
        if (snapshot.Verdict == verdict)
            return snapshot;

        var projectId = await (from r in db.Runs join t in db.Threads on r.ThreadId equals t.Id where r.Id == runId select (Guid?)t.ProjectId).FirstOrDefaultAsync(ct);
        Audit(db, userId, AuditActions.RunLessonVerdict, AuditTargets.Lesson, lessonId, projectId,
            new { RunId = runId, snapshot.Number, From = snapshot.Verdict?.ToString(), To = verdict?.ToString() }, now);
        snapshot.Verdict = verdict;
        snapshot.VerdictBy = verdict is null ? null : userId;
        snapshot.VerdictAt = verdict is null ? null : now;
        await write.CommitAsync(ct);
        return snapshot;
    }

    public async Task<TaskThread> SetThreadLessonsAsync(Guid threadId, IReadOnlyList<Guid> lessonIds, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        var wanted = lessonIds.Distinct().ToList();
        var from = JsonSerializer.Deserialize<List<Guid>>(thread.ManualLessonsJson) ?? [];
        if (from.SequenceEqual(wanted))
            return thread;

        var lessons = await db.Lessons.Where(l => wanted.Contains(l.Id)).ToListAsync(ct);
        var unusable = wanted.Where(id => lessons.FirstOrDefault(l => l.Id == id) is not { Status: LessonStatus.Approved, Enabled: true } lesson
            || lesson.ProjectId != thread.ProjectId).ToList();
        if (unusable.Count > 0)
            throw new StoreConflictException("Only an approved, enabled lesson of this task's project can be picked for it.");

        var numbers = wanted.Select(id => $"L-{lessons.First(l => l.Id == id).Number}").ToList();
        Audit(db, userId, AuditActions.ThreadLessons, AuditTargets.Thread, threadId, thread.ProjectId, new { From = from, To = wanted }, now);
        thread.ManualLessonsJson = JsonSerializer.Serialize(wanted);
        AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status,
            numbers.Count == 0
                ? "No lessons are picked for this task any more. Its next run is given the lessons that match it."
                : $"Lessons picked for this task: {string.Join(", ", numbers)}. Its next run is given them first, as they fit.",
            now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return thread;
    }
}
