using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// Lesson reflection (ReadMe_LitosSoftwareFactory_V1.md §23, m4-architecture.md §5). A job is
/// created with the acceptance; acceptance never waits for it, and nothing it does can undo it. Its
/// run is a Reflection run, charged to the accepted task's budget within the allowance. Its
/// proposals are stored as suggestions in the transaction that completes it.
/// </summary>
public sealed partial class EfFactoryStore
{
    internal const string DeferredMessage =
        "Task accepted. Lesson reflection is deferred; increase the allowance, add a lesson manually, or skip.";

    /// <summary>
    /// Inside the Accept transaction: the reflection for the accepted handoff, once. Queued as a run
    /// when the task has the allowance left under its cap, otherwise deferred, and the thread says so.
    /// </summary>
    private static async Task QueueReflectionAsync(FactoryDbContext db, TaskThread thread, Guid userId, long allowance, DateTimeOffset now, CancellationToken ct)
    {
        if (allowance <= 0)
            return;

        var handoffs = await db.Handoffs.AsNoTracking().Where(h => h.ThreadId == thread.Id).Select(h => new { h.Id, h.CreatedAt }).ToListAsync(ct);
        var handoffId = handoffs.OrderByDescending(h => h.CreatedAt).Select(h => (Guid?)h.Id).FirstOrDefault();
        if (await db.ReflectionJobs.AnyAsync(j => j.ThreadId == thread.Id && j.HandoffId == handoffId, ct))
            return;

        var job = new ReflectionJob { Id = Guid.NewGuid(), ThreadId = thread.Id, HandoffId = handoffId, CreatedAt = now, UpdatedAt = now };
        db.ReflectionJobs.Add(job);
        if (!HasRoomFor(thread, allowance))
        {
            job.Status = ReflectionStatus.DeferredBudget;
            AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status, DeferredMessage, now);
            return;
        }

        StartAttempt(db, thread, job, userId, allowance, now);
    }

    /// <summary>Whether the task's budget has the allowance left under its cap.</summary>
    private static bool HasRoomFor(TaskThread thread, long allowance) =>
        thread.BudgetCap is not { } cap || cap - thread.TokensUsed - thread.TokensReserved >= allowance;

    /// <summary>A Reflection run, queued, whose own cap is the allowance; the job's next attempt.</summary>
    private static void StartAttempt(FactoryDbContext db, TaskThread thread, ReflectionJob job, Guid userId, long allowance, DateTimeOffset now)
    {
        var runId = AddRun(db, thread, userId, RunKind.Reflection, "Propose lessons from this accepted task.", now);
        db.Runs.Local.First(r => r.Id == runId).ChatBudgetCap = allowance;
        job.RunId = runId;
        job.Status = ReflectionStatus.Pending;
        job.Attempts++;
        job.LastError = null;
        job.UpdatedAt = now;
    }

    public async Task<ReflectionOutcome> CompleteReflectionAsync(Guid runId, IReadOnlyList<LessonProposal> proposals, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var job = await db.ReflectionJobs.FirstOrDefaultAsync(j => j.RunId == runId, ct)
            ?? throw new StoreNotFoundException("No reflection has that run.");
        // Completed already: a retried completion returns what the first one stored.
        if (job.Status == ReflectionStatus.Completed)
            return JsonSerializer.Deserialize<ReflectionOutcome>(job.OutcomeJson!, FactoryWire.Json)!;

        var thread = await LockThreadAsync(db, job.ThreadId, ct);
        await LockProjectAsync(db, thread.ProjectId, ct);
        var number = await db.Lessons.Where(l => l.ProjectId == thread.ProjectId).MaxAsync(l => (long?)l.Number, ct) ?? 0;
        var stored = new List<long>();
        var refused = new List<RefusedProposal>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposal in proposals.Take(3))
        {
            var why = await CheckProposalAsync(db, thread, proposal, hashes, ct);
            if (why.Problem is { } problem)
            {
                refused.Add(new RefusedProposal(proposal.Rule ?? "", problem));
                continue;
            }

            var lesson = new Lesson
            {
                Id = Guid.NewGuid(),
                ProjectId = thread.ProjectId,
                Number = ++number,
                ProposedByJobId = job.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Lessons.Add(lesson);
            db.LessonRevisions.Add(Revision(lesson.Id, 1, why.Draft!, thread.Id, why.Hash!, null, now, null));
            stored.Add(lesson.Number);
        }

        var outcome = new ReflectionOutcome(stored, refused);
        job.Status = ReflectionStatus.Completed;
        job.OutcomeJson = JsonSerializer.Serialize(outcome, FactoryWire.Json);
        job.UpdatedAt = now;
        await FinishReflectionRunAsync(db, runId, now, ct);
        AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status, ReflectionNote(outcome), now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return outcome;
    }

    internal static string ReflectionNote(ReflectionOutcome outcome)
    {
        var lessons = outcome.Lessons.Select(n => $"L-{n}").ToList();
        var said = lessons.Count switch
        {
            0 => "Lesson reflection found nothing a later task needs, so it proposed no lessons.",
            1 => $"Lesson reflection proposed a lesson, {lessons[0]}. An Admin can remember it for the project below, or on the Lessons page.",
            _ => $"Lesson reflection proposed {lessons.Count} lessons: {string.Join(", ", lessons)}. An Admin can remember them for the project below, or on the Lessons page.",
        };
        return outcome.Refused.Count == 0
            ? said
            : $"{said} {(outcome.Refused.Count == 1 ? "One proposal was" : $"{outcome.Refused.Count} proposals were")} refused: "
              + string.Join("; ", outcome.Refused.Select(r => r.Why)) + ".";
    }

    /// <summary>Why a proposal cannot be stored, or the draft and hash to store it with.</summary>
    private static async Task<(string? Problem, LessonDraft? Draft, string? Hash)> CheckProposalAsync(
        FactoryDbContext db, TaskThread thread, LessonProposal proposal, HashSet<string> hashes, CancellationToken ct)
    {
        if (!Enum.TryParse<LessonSource>(proposal.Source, ignoreCase: true, out var source) || !Enum.IsDefined(source) || source == LessonSource.Manual)
            return ($"\"{proposal.Source}\" is not a source a reflection can cite", null, null);
        if (!Guid.TryParse(proposal.SourceRef, out var sourceRef))
            return ("it does not name its source by an id the brief gave", null, null);

        var draft = LessonRules.Clean(new LessonDraft(proposal.Rule ?? "", proposal.Topics ?? [], proposal.Paths ?? [], source, sourceRef, proposal.Reason ?? ""));
        if (LessonRules.Validate(draft) is { Count: > 0 } errors)
            return (string.Join(" ", errors).TrimEnd('.'), null, null);

        try
        {
            if (await SourceThreadAsync(db, thread.ProjectId, draft, ct) != thread.Id)
                return ("what it rests on is not in this task", null, null);
            var hash = LessonRules.Normalize(draft.Rule);
            if (!hashes.Add(hash))
                return ("it repeats another proposal", null, null);
            await CheckNotDuplicateAsync(db, thread.ProjectId, hash, null, ct);
            return (null, draft, hash);
        }
        catch (StoreConflictException ex)
        {
            return (ex.Message.TrimEnd('.'), null, null);
        }
    }

    public async Task FailReflectionAsync(Guid runId, string error, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var job = await db.ReflectionJobs.FirstOrDefaultAsync(j => j.RunId == runId, ct)
            ?? throw new StoreNotFoundException("No reflection has that run.");
        if (job.Status is ReflectionStatus.Completed or ReflectionStatus.Failed or ReflectionStatus.Skipped)
            return;

        var thread = await LockThreadAsync(db, job.ThreadId, ct);
        job.Status = ReflectionStatus.Failed;
        job.LastError = error;
        job.UpdatedAt = now;
        await FinishReflectionRunAsync(db, runId, now, ct);
        AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status,
            $"Lesson reflection failed: {error.TrimEnd('.')}. The task stays accepted. Retry the reflection, add a lesson by hand, or skip it.", now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
    }

    public async Task<ReflectionJob> RetryReflectionAsync(Guid threadId, long allowance, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        var job = await LatestJobAsync(db, threadId, ct) ?? throw new StoreNotFoundException("This task has no lesson reflection.");
        if (job.Status is not (ReflectionStatus.DeferredBudget or ReflectionStatus.Failed))
            throw new StoreConflictException($"A reflection that is {job.Status} cannot be retried.");
        if (allowance <= 0)
            throw new StoreConflictException("Lesson reflection is turned off in the factory's Budgets settings.");
        if (!HasRoomFor(thread, allowance))
            throw new StoreConflictException($"The task has less than the reflection allowance of {allowance:N0} tokens left. Raise its cap first, add a lesson manually, or skip.");

        StartAttempt(db, thread, job, userId, allowance, now);
        Audit(db, userId, AuditActions.ReflectionRetry, AuditTargets.Thread, threadId, thread.ProjectId, new { job.Attempts }, now);
        AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status, "Lesson reflection is queued again.", now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return job;
    }

    public async Task<ReflectionJob> SkipReflectionAsync(Guid threadId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        var job = await LatestJobAsync(db, threadId, ct) ?? throw new StoreNotFoundException("This task has no lesson reflection.");
        if (job.Status == ReflectionStatus.Skipped)
            return job;
        if (job.Status is not (ReflectionStatus.DeferredBudget or ReflectionStatus.Failed))
            throw new StoreConflictException($"A reflection that is {job.Status} cannot be skipped.");

        job.Status = ReflectionStatus.Skipped;
        job.UpdatedAt = now;
        Audit(db, userId, AuditActions.ReflectionSkip, AuditTargets.Thread, threadId, thread.ProjectId, null, now);
        AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status, "Lesson reflection was skipped.", now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return job;
    }

    public async Task<IReadOnlyList<ReflectionJob>> ListReflectionsAsync(ReflectionStatus? status, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ReflectionJobs.AsNoTracking().Where(j => status == null || j.Status == status).ToListAsync(ct);
    }

    private static async Task<ReflectionJob?> LatestJobAsync(FactoryDbContext db, Guid threadId, CancellationToken ct) =>
        (await db.ReflectionJobs.Where(j => j.ThreadId == threadId).ToListAsync(ct)).OrderByDescending(j => j.CreatedAt).FirstOrDefault();

    private static async Task FinishReflectionRunAsync(FactoryDbContext db, Guid runId, DateTimeOffset now, CancellationToken ct)
    {
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is { Status: not RunStatus.Finished })
        {
            run.Status = RunStatus.Finished;
            run.EndedAt = now;
        }
    }

    /// <summary>The thread's latest reflection and the lessons its reflections proposed, for the thread view.</summary>
    private static async Task<(ReflectionJob? Job, IReadOnlyList<LessonDetails> Proposed)> ReflectionOfAsync(FactoryDbContext db, Guid threadId, CancellationToken ct)
    {
        var jobs = await db.ReflectionJobs.AsNoTracking().Where(j => j.ThreadId == threadId).ToListAsync(ct);
        if (jobs.Count == 0)
            return (null, []);

        var ids = jobs.Select(j => j.Id).ToList();
        var lessons = await db.Lessons.AsNoTracking().Where(l => l.ProposedByJobId != null && ids.Contains(l.ProposedByJobId.Value)).ToListAsync(ct);
        var lessonIds = lessons.Select(l => l.Id).ToList();
        var revisions = await db.LessonRevisions.AsNoTracking().Where(r => lessonIds.Contains(r.LessonId)).ToListAsync(ct);
        return (jobs.OrderByDescending(j => j.CreatedAt).First(), [.. lessons.OrderBy(l => l.Number).Select(l => Details(l, revisions))]);
    }
}
