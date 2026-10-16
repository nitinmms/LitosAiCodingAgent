using System.Text.Json;
using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// Lessons (m4-architecture.md §3). A revision is written once and never changed; a lesson's row
/// says which revision is newest and which is approved. Every command on an existing lesson takes
/// the revision its caller read and refuses a stale one, and every person's action is audited.
/// </summary>
public sealed partial class EfFactoryStore
{
    public async Task<IReadOnlyList<LessonDetails>> ListLessonsAsync(Guid projectId, LessonStatus? status, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var lessons = await db.Lessons.AsNoTracking()
            .Where(l => l.ProjectId == projectId && (status == null || l.Status == status))
            .OrderByDescending(l => l.Number)
            .ToListAsync(ct);
        var ids = lessons.Select(l => l.Id).ToList();
        var revisions = await db.LessonRevisions.AsNoTracking().Where(r => ids.Contains(r.LessonId)).ToListAsync(ct);
        return [.. lessons.Select(l => Details(l, revisions))];
    }

    public async Task<LessonDetails?> GetLessonAsync(Guid lessonId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var lesson = await db.Lessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lessonId, ct);
        return lesson is null ? null : Details(lesson, await RevisionsAsync(db, lessonId, ct));
    }

    public async Task<LessonDetails> AddLessonAsync(
        Guid projectId, LessonDraft draft, Guid? userId, bool approve, Guid? proposedByJobId, DateTimeOffset now, CancellationToken ct)
    {
        if (approve && userId is null)
            throw new ArgumentException("Only a person can approve a lesson.", nameof(approve));
        draft = Checked(draft);

        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        await LockProjectAsync(db, projectId, ct);
        var sourceThread = await SourceThreadAsync(db, projectId, draft, ct);
        var hash = LessonRules.Normalize(draft.Rule);
        await CheckNotDuplicateAsync(db, projectId, hash, null, ct);

        var number = (await db.Lessons.Where(l => l.ProjectId == projectId).MaxAsync(l => (long?)l.Number, ct) ?? 0) + 1;
        var lesson = new Lesson
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Number = number,
            Status = approve ? LessonStatus.Approved : LessonStatus.Suggested,
            ApprovedRevision = approve ? 1 : null,
            ProposedByJobId = proposedByJobId,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var revision = Revision(lesson.Id, 1, draft, sourceThread, hash, userId, now, approve ? userId : null);
        db.Lessons.Add(lesson);
        db.LessonRevisions.Add(revision);
        // A reflection's proposal is the job's record; only a person's action is audited.
        if (userId is { } author)
            Audit(db, author, AuditActions.LessonCreate, AuditTargets.Lesson, lesson.Id, projectId, new { lesson.Number, Revision = 1, Approved = approve }, now);
        await write.CommitAsync(ct);
        return new LessonDetails(lesson, revision, approve ? revision : null);
    }

    public async Task<LessonDetails> ReviseLessonAsync(
        Guid lessonId, int expectedRevision, LessonDraft draft, Guid userId, bool approve, DateTimeOffset now, CancellationToken ct)
    {
        draft = Checked(draft);
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var lesson = await LockLessonAsync(db, lessonId, ct);
        if (lesson.Status == LessonStatus.Dismissed)
            throw new StoreConflictException($"L-{lesson.Number} was dismissed; write a new lesson instead.");
        RequireRevision(lesson, expectedRevision);
        var sourceThread = await SourceThreadAsync(db, lesson.ProjectId, draft, ct);
        var hash = LessonRules.Normalize(draft.Rule);
        await CheckNotDuplicateAsync(db, lesson.ProjectId, hash, lesson.Id, ct);

        var from = lesson.CurrentRevision;
        var revision = Revision(lesson.Id, from + 1, draft, sourceThread, hash, userId, now, approve ? userId : null);
        db.LessonRevisions.Add(revision);
        lesson.CurrentRevision = revision.Revision;
        if (approve)
        {
            lesson.Status = LessonStatus.Approved;
            lesson.ApprovedRevision = revision.Revision;
        }

        lesson.UpdatedAt = now;
        Audit(db, userId, AuditActions.LessonRevise, AuditTargets.Lesson, lesson.Id, lesson.ProjectId, new { lesson.Number, From = from, To = revision.Revision, Approved = approve }, now);
        await write.CommitAsync(ct);
        return Details(lesson, await RevisionsAsync(db, lesson.Id, ct));
    }

    public async Task<LessonDetails> ApproveLessonAsync(Guid lessonId, int expectedRevision, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var lesson = await LockLessonAsync(db, lessonId, ct);
        if (lesson.Status == LessonStatus.Dismissed)
            throw new StoreConflictException($"L-{lesson.Number} was dismissed; write a new lesson instead.");

        // Approving what is already approved changes nothing: a retried request is safe.
        if (lesson.ApprovedRevision != expectedRevision)
        {
            RequireRevision(lesson, expectedRevision);
            var revision = await db.LessonRevisions.FirstAsync(r => r.LessonId == lesson.Id && r.Revision == expectedRevision, ct);
            revision.ApprovedBy = userId;
            revision.ApprovedAt = now;
            lesson.Status = LessonStatus.Approved;
            lesson.ApprovedRevision = expectedRevision;
            lesson.UpdatedAt = now;
            Audit(db, userId, AuditActions.LessonApprove, AuditTargets.Lesson, lesson.Id, lesson.ProjectId, new { lesson.Number, Revision = expectedRevision }, now);
            await write.CommitAsync(ct);
        }

        return Details(lesson, await RevisionsAsync(db, lesson.Id, ct));
    }

    public async Task<LessonDetails> DismissLessonAsync(Guid lessonId, int expectedRevision, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var lesson = await LockLessonAsync(db, lessonId, ct);
        if (lesson.Status == LessonStatus.Approved)
            throw new StoreConflictException($"L-{lesson.Number} is approved; disable it instead.");
        if (lesson.Status != LessonStatus.Dismissed)
        {
            RequireRevision(lesson, expectedRevision);
            lesson.Status = LessonStatus.Dismissed;
            lesson.UpdatedAt = now;
            Audit(db, userId, AuditActions.LessonDismiss, AuditTargets.Lesson, lesson.Id, lesson.ProjectId, new { lesson.Number, Revision = expectedRevision }, now);
            await write.CommitAsync(ct);
        }

        return Details(lesson, await RevisionsAsync(db, lesson.Id, ct));
    }

    public async Task<LessonDetails> SetLessonEnabledAsync(Guid lessonId, int expectedRevision, bool enabled, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var lesson = await LockLessonAsync(db, lessonId, ct);
        if (lesson.Status != LessonStatus.Approved)
            throw new StoreConflictException($"L-{lesson.Number} is not approved; only an approved lesson is enabled or disabled.");
        if (lesson.Enabled != enabled)
        {
            RequireRevision(lesson, expectedRevision);
            lesson.Enabled = enabled;
            // Enabling a superseded lesson brings it back on its own: it no longer points at a replacement.
            if (enabled)
                lesson.SupersededById = null;
            lesson.UpdatedAt = now;
            Audit(db, userId, enabled ? AuditActions.LessonEnable : AuditActions.LessonDisable, AuditTargets.Lesson, lesson.Id, lesson.ProjectId,
                new { lesson.Number }, now);
            await write.CommitAsync(ct);
        }

        return Details(lesson, await RevisionsAsync(db, lesson.Id, ct));
    }

    public async Task<LessonDetails> SupersedeLessonAsync(
        Guid lessonId, int expectedRevision, Guid replacementId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (lessonId == replacementId)
            throw new StoreConflictException("A lesson cannot replace itself.");

        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        // Both rows, in a fixed order, so two opposite supersessions cannot deadlock.
        var locked = new Dictionary<Guid, Lesson>();
        foreach (var id in new[] { lessonId, replacementId }.Order())
            locked[id] = await LockLessonAsync(db, id, ct);
        var (lesson, replacement) = (locked[lessonId], locked[replacementId]);

        if (lesson.Status != LessonStatus.Approved)
            throw new StoreConflictException($"L-{lesson.Number} is not approved; dismiss it instead.");
        if (replacement.ProjectId != lesson.ProjectId)
            throw new StoreNotFoundException("The replacement lesson does not exist.");
        if (replacement.Status != LessonStatus.Approved || !replacement.Enabled)
            throw new StoreConflictException($"L-{replacement.Number} must be approved and enabled to replace another lesson.");
        RequireRevision(lesson, expectedRevision);

        lesson.Enabled = false;
        lesson.SupersededById = replacement.Id;
        lesson.UpdatedAt = now;
        Audit(db, userId, AuditActions.LessonSupersede, AuditTargets.Lesson, lesson.Id, lesson.ProjectId,
            new { lesson.Number, ReplacedBy = replacement.Number }, now);
        await write.CommitAsync(ct);
        return Details(lesson, await RevisionsAsync(db, lesson.Id, ct));
    }

    // ---- Shared ----

    private static LessonDraft Checked(LessonDraft draft)
    {
        var cleaned = LessonRules.Clean(draft);
        var errors = LessonRules.Validate(cleaned);
        return errors.Count == 0 ? cleaned : throw new ArgumentException(string.Join(" ", errors), nameof(draft));
    }

    private static void RequireRevision(Lesson lesson, int expectedRevision)
    {
        if (lesson.CurrentRevision != expectedRevision)
            throw new StoreConflictException(
                $"L-{lesson.Number} has changed since you read it: it is at revision {lesson.CurrentRevision}, not {expectedRevision}. Reload it and try again.");
    }

    private static LessonRevision Revision(
        Guid lessonId, int number, LessonDraft draft, Guid? sourceThread, string hash, Guid? author, DateTimeOffset now, Guid? approvedBy) => new()
    {
        Id = Guid.NewGuid(),
        LessonId = lessonId,
        Revision = number,
        Rule = draft.Rule,
        TopicsJson = JsonSerializer.Serialize(draft.Topics),
        PathsJson = JsonSerializer.Serialize(draft.Paths),
        SourceKind = draft.Source,
        SourceRef = draft.Source == LessonSource.Manual ? null : draft.SourceRef,
        SourceThreadId = sourceThread,
        Reason = draft.Reason,
        NormalizedHash = hash,
        AuthorId = author,
        CreatedAt = now,
        ApprovedBy = approvedBy,
        ApprovedAt = approvedBy is null ? null : now,
    };

    private static LessonDetails Details(Lesson lesson, IReadOnlyCollection<LessonRevision> revisions)
    {
        var own = revisions.Where(r => r.LessonId == lesson.Id).ToList();
        return new LessonDetails(
            lesson,
            own.First(r => r.Revision == lesson.CurrentRevision),
            lesson.ApprovedRevision is { } approved ? own.First(r => r.Revision == approved) : null);
    }

    private static async Task<List<LessonRevision>> RevisionsAsync(FactoryDbContext db, Guid lessonId, CancellationToken ct) =>
        await db.LessonRevisions.AsNoTracking().Where(r => r.LessonId == lessonId).ToListAsync(ct);

    private static async Task<Lesson> LockLessonAsync(FactoryDbContext db, Guid lessonId, CancellationToken ct)
    {
        var lesson = db.Database.IsNpgsql()
            ? await db.Lessons.FromSqlInterpolated($"SELECT * FROM lessons WHERE \"Id\" = {lessonId} FOR UPDATE").FirstOrDefaultAsync(ct)
            : await db.Lessons.FirstOrDefaultAsync(l => l.Id == lessonId, ct);
        return lesson ?? throw new StoreNotFoundException("The lesson does not exist.");
    }

    /// <summary>Held while a lesson is numbered and checked for duplicates, so two lessons written at
    /// once can neither share a number nor both pass as the first of the same rule.</summary>
    private static async Task LockProjectAsync(FactoryDbContext db, Guid projectId, CancellationToken ct)
    {
        var found = db.Database.IsNpgsql()
            ? await db.Projects.FromSqlInterpolated($"SELECT * FROM projects WHERE \"Id\" = {projectId} FOR UPDATE").AnyAsync(ct)
            : await db.Projects.AnyAsync(p => p.Id == projectId, ct);
        if (!found)
            throw new StoreNotFoundException("The project does not exist.");
    }

    /// <summary>A live lesson (not dismissed) of the project whose newest or approved revision says the same.</summary>
    private static async Task CheckNotDuplicateAsync(FactoryDbContext db, Guid projectId, string hash, Guid? except, CancellationToken ct)
    {
        var duplicate = await (
            from r in db.LessonRevisions
            join l in db.Lessons on r.LessonId equals l.Id
            where l.ProjectId == projectId && l.Status != LessonStatus.Dismissed && l.Id != except && r.NormalizedHash == hash
                && (r.Revision == l.CurrentRevision || r.Revision == l.ApprovedRevision)
            select (long?)l.Number).FirstOrDefaultAsync(ct);
        if (duplicate is { } number)
            throw new StoreConflictException($"This repeats L-{number}, which says the same.");
    }

    /// <summary>The thread what a lesson rests on belongs to, which must be one of the same project's;
    /// null for a manual lesson.</summary>
    private static async Task<Guid?> SourceThreadAsync(FactoryDbContext db, Guid projectId, LessonDraft draft, CancellationToken ct)
    {
        if (draft.Source == LessonSource.Manual || draft.SourceRef is not { } id)
            return null;

        var source = draft.Source switch
        {
            LessonSource.HumanCorrection => await (
                from m in db.Messages join t in db.Threads on m.ThreadId equals t.Id where m.Id == id select new { t.Id, t.ProjectId }).FirstOrDefaultAsync(ct),
            LessonSource.Decision => await (
                from d in db.Decisions join t in db.Threads on d.ThreadId equals t.Id where d.Id == id select new { t.Id, t.ProjectId }).FirstOrDefaultAsync(ct),
            LessonSource.ReviewFinding => await (
                from f in db.ReviewFindings join r in db.Runs on f.RunId equals r.Id join t in db.Threads on r.ThreadId equals t.Id
                where f.Id == id select new { t.Id, t.ProjectId }).FirstOrDefaultAsync(ct),
            LessonSource.Evidence => await (
                from h in db.Handoffs join t in db.Threads on h.ThreadId equals t.Id where h.Id == id select new { t.Id, t.ProjectId }).FirstOrDefaultAsync(ct),
            _ => null,
        };
        return source is not null && source.ProjectId == projectId
            ? source.Id
            : throw new StoreConflictException("What the lesson rests on is not in this project.");
    }
}
