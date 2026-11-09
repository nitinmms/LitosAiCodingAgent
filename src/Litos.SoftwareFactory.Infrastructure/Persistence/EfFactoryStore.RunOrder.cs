using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Queue;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// A project's run order and "Blocked by" links (docs/software-factory/run-order-architecture.md).
/// The claim reads both (ClaimNextRunAsync); everything that changes them is here, each in one
/// write under the thread's row lock, audited when a person did it.
/// </summary>
public sealed partial class EfFactoryStore
{
    public const string OnlyQueuedTasksMove = "Only a task waiting in the queue can be moved.";
    public const string MembersSwapOwnTasks = "Only an Admin can put a task ahead of other people's. Move it by swapping it with another of your own tasks.";
    public const string OwnerOrAdminArranges = "Only the task's owner or an Admin can change where it runs or what it waits for.";
    public const string LinkOnlyBeforeStart = "A task can wait for another only before it starts.";
    public const string StaleView = "The task has changed since you last saw it. Look again, then try once more.";

    // ---- Reading ----

    public async Task<ProjectRunOrder> GetRunOrderAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var holderId = await db.Leases.AsNoTracking().Where(l => l.ProjectId == projectId).Select(l => (Guid?)l.ThreadId).FirstOrDefaultAsync(ct);
        var holder = holderId is null ? null : await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == holderId, ct);

        // A spec run waits for a slot only, so it is not in the order; nor is the holder's own run.
        var queued = await db.Threads.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.State == LifecycleState.Queued && t.Stage != Stage.Spec && t.Id != holderId)
            .ToListAsync(ct);
        // As the claim takes them: no rank first, then lowest rank.
        queued = [.. queued.OrderBy(t => t.QueueRank is null ? 0 : 1).ThenBy(t => t.QueueRank).ThenBy(t => t.CreatedAt)];

        var ids = queued.Select(t => t.Id).ToList();
        var waitsFor = await LinksAsync(db, l => l.ReleasedAt == null && ids.Contains(l.ThreadId), l => l.BlockerThreadId, ct);
        var waitedOn = await db.Blockers.AsNoTracking().Where(l => l.ReleasedAt == null && ids.Contains(l.BlockerThreadId))
            .GroupBy(l => l.BlockerThreadId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var reworks = (await db.Runs.AsNoTracking().Where(r => ids.Contains(r.ThreadId) && r.Status == RunStatus.Queued && r.Kind == RunKind.Rework)
            .Select(r => r.ThreadId).ToListAsync(ct)).ToHashSet();

        return new ProjectRunOrder(holder, queued.Select(t => new RunOrderEntry(
            t,
            waitsFor.Where(l => l.Link.ThreadId == t.Id).ToList(),
            waitedOn.GetValueOrDefault(t.Id),
            reworks.Contains(t.Id))).ToList());
    }

    public async Task<ThreadLinks> GetThreadLinksAsync(Guid threadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var waitsFor = await LinksAsync(db, l => l.ThreadId == threadId, l => l.BlockerThreadId, ct);
        var waitedOnBy = await LinksAsync(db, l => l.BlockerThreadId == threadId && l.ReleasedAt == null, l => l.ThreadId, ct);
        return new ThreadLinks(
            [.. waitsFor.OrderBy(l => l.Link.ReleasedAt is null ? 0 : 1).ThenBy(l => l.Link.CreatedAt)],
            [.. waitedOnBy.OrderBy(l => l.Link.CreatedAt)]);
    }

    private static async Task<List<BlockerLink>> LinksAsync(
        FactoryDbContext db, System.Linq.Expressions.Expression<Func<TaskBlocker, bool>> which,
        System.Linq.Expressions.Expression<Func<TaskBlocker, Guid>> otherEnd, CancellationToken ct)
    {
        var links = await db.Blockers.AsNoTracking().Where(which).ToListAsync(ct);
        var otherIds = links.AsQueryable().Select(otherEnd).Distinct().ToList();
        var others = await db.Threads.AsNoTracking().Where(t => otherIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var pick = otherEnd.Compile();
        return links.Select(l => new BlockerLink(l, others[pick(l)])).ToList();
    }

    // ---- Moving ----

    public async Task<TaskThread> MoveInQueueAsync(
        Guid threadId, Guid actorId, bool canArrangeAny, Placement placement, int? revision, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        if (revision is { } seen && seen != thread.Revision)
            throw new StoreConflictException(StaleView);
        if (thread.State != LifecycleState.Queued || thread.QueueRank is null)
            throw new StoreConflictException(OnlyQueuedTasksMove);
        RequireOwnerOrAdmin(thread, actorId, canArrangeAny);
        if (!canArrangeAny && placement.Kind != PlacementKind.SwapWith)
            throw new StoreForbiddenException(MembersSwapOwnTasks);

        if (placement.Kind is PlacementKind.After or PlacementKind.SwapWith)
        {
            if (placement.OtherThreadId == threadId)
                throw new StoreConflictException("A task cannot be placed against itself.");
            var other = await db.Threads.AsNoTracking().FirstOrDefaultAsync(
                t => t.Id == placement.OtherThreadId && t.ProjectId == thread.ProjectId && t.State == LifecycleState.Queued && t.QueueRank != null, ct)
                ?? throw new StoreConflictException("The task to place it against is no longer waiting in this project's queue.");
            if (!canArrangeAny && other.OwnerId != actorId)
                throw new StoreForbiddenException(MembersSwapOwnTasks);
        }

        var moved = await PlaceInQueueAsync(db, thread, placement, ct);
        Audit(db, actorId, AuditActions.ThreadQueueMove, AuditTargets.Thread, threadId, thread.ProjectId,
            new { Placement = placement.Kind.ToString(), placement.OtherThreadId, thread.QueueRank }, now);
        Touch(db, thread, now);
        foreach (var other in moved)
            Touch(db, other, now);
        await write.CommitAsync(ct);
        return thread;
    }

    private static void RequireOwnerOrAdmin(TaskThread thread, Guid actorId, bool canArrangeAny)
    {
        if (!canArrangeAny && thread.OwnerId != actorId)
            throw new StoreForbiddenException(OwnerOrAdminArranges);
    }

    // ---- Links ----

    public async Task<TaskThread> AddBlockerAsync(Guid threadId, Guid blockerThreadId, Guid actorId, bool canArrangeAny, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        RequireOwnerOrAdmin(thread, actorId, canArrangeAny);

        // A task that has started has its branch already, a rework included: waiting now would not
        // give it the other task's work.
        var started = thread.Branch is not null || await db.Runs.AnyAsync(
            r => r.ThreadId == threadId && r.Kind != RunKind.Chat && r.Kind != RunKind.Reflection && r.StartedAt != null && r.Status != RunStatus.Finished, ct);
        if (thread.State is not (LifecycleState.Draft or LifecycleState.Queued) || started)
            throw new StoreConflictException(LinkOnlyBeforeStart);

        if (blockerThreadId == threadId)
            throw new StoreConflictException("A task cannot wait for itself.");
        var blocker = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == blockerThreadId, ct);
        if (blocker is null || blocker.ProjectId != thread.ProjectId)
            throw new StoreConflictException("A task can wait only for another task of the same project.");
        if (blocker.State == LifecycleState.Cancelled)
            throw new StoreConflictException($"\"{blocker.Title}\" was cancelled: there is nothing to wait for.");
        if (blocker.PullRequestMergedAt is not null)
            throw new StoreConflictException($"\"{blocker.Title}\" is already merged: there is nothing to wait for.");
        if (blocker.PullRequestClosedAt is not null)
            throw new StoreConflictException($"\"{blocker.Title}\"'s pull request was closed without merging, so it would never be merged.");

        var open = await db.Blockers.AsNoTracking().Where(l => l.ProjectId == thread.ProjectId && l.ReleasedAt == null)
            .Select(l => new { l.ThreadId, l.BlockerThreadId }).ToListAsync(ct);
        if (open.Any(l => l.ThreadId == threadId && l.BlockerThreadId == blockerThreadId))
            throw new StoreConflictException($"This task already waits for \"{blocker.Title}\".");

        var waitsFor = open.ToLookup(l => l.ThreadId, l => l.BlockerThreadId);
        if (RunOrder.FindLoop(threadId, blockerThreadId, id => waitsFor[id]) is { } loop)
        {
            var titles = await db.Threads.AsNoTracking().Where(t => loop.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);
            throw new StoreConflictException(
                $"Not linked: that would make a loop. \"{thread.Title}\" would wait for " +
                string.Join(", which waits for ", loop.Select(id => $"\"{titles[id]}\"")) + ".");
        }

        db.Blockers.Add(new TaskBlocker
        {
            Id = Guid.NewGuid(),
            ProjectId = thread.ProjectId,
            ThreadId = threadId,
            BlockerThreadId = blockerThreadId,
            CreatedBy = actorId,
            CreatedAt = now,
        });
        Audit(db, actorId, AuditActions.ThreadBlockerAdd, AuditTargets.Thread, threadId, thread.ProjectId, new { BlockerThreadId = blockerThreadId, blocker.Title }, now);
        AddMessage(db, thread, MessageAuthor.User, actorId, MessageKind.Status, $"Waits for \"{blocker.Title}\" to be merged before it starts.", now);
        if (thread.State == LifecycleState.Queued)
            thread.StateReason = RunOrder.WaitingReason(new OpenBlocker(blocker.Title, blocker.State, blocker.PullRequestNumber, PullRequestClosed: false));
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return thread;
    }

    public async Task<TaskThread> ReleaseBlockerAsync(Guid threadId, Guid blockerThreadId, Guid actorId, bool canArrangeAny, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        RequireOwnerOrAdmin(thread, actorId, canArrangeAny);
        var link = await db.Blockers.FirstOrDefaultAsync(l => l.ThreadId == threadId && l.BlockerThreadId == blockerThreadId && l.ReleasedAt == null, ct)
            ?? throw new StoreConflictException("This task no longer waits for that one.");
        var blocker = await db.Threads.AsNoTracking().FirstAsync(t => t.Id == blockerThreadId, ct);

        Release(link, BlockerRelease.Manual, actorId, now);
        Audit(db, actorId, AuditActions.ThreadBlockerRelease, AuditTargets.Thread, threadId, thread.ProjectId, new { BlockerThreadId = blockerThreadId, blocker.Title }, now);
        AddMessage(db, thread, MessageAuthor.User, actorId, MessageKind.Status, $"Released from waiting for \"{blocker.Title}\". It runs without that work.", now);
        await AfterLinkReleasedAsync(db, thread, ct);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return thread;
    }

    private static void Release(TaskBlocker link, BlockerRelease reason, Guid? by, DateTimeOffset now)
    {
        link.ReleasedAt = now;
        link.ReleasedBy = by;
        link.ReleaseReason = reason;
    }

    /// <summary>
    /// A waiting task lost one of its links: whether a person must still act comes from the links
    /// left, and a reason about the released one would be stale (the claim gives the next one).
    /// </summary>
    private static async Task AfterLinkReleasedAsync(FactoryDbContext db, TaskThread thread, CancellationToken ct)
    {
        // Links released in this same write are still open in the database, so the tracked ones decide.
        var released = db.ChangeTracker.Entries<TaskBlocker>().Where(e => e.Entity.ReleasedAt is not null).Select(e => e.Entity.Id).ToHashSet();
        thread.BlockerNeedsYou = await (
            from l in db.Blockers
            where l.ThreadId == thread.Id && l.ReleasedAt == null && !released.Contains(l.Id)
            join b in db.Threads on l.BlockerThreadId equals b.Id
            select b.PullRequestClosedAt).AnyAsync(closed => closed != null, ct);
        if (thread.State == LifecycleState.Queued)
            thread.StateReason = null;
    }

    // ---- Merges ----

    public async Task<TaskThread> MarkMergedAsync(Guid threadId, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        if (thread.State != LifecycleState.Accepted)
            throw new StoreConflictException("Only an accepted task can be marked as merged.");
        if (thread.PullRequestMergedAt is not null)
            throw new StoreConflictException("This task is already merged.");

        thread.PullRequestMergedAt = now;
        Audit(db, actorId, AuditActions.ThreadMarkMerged, AuditTargets.Thread, threadId, thread.ProjectId, new { thread.PullRequestNumber }, now);
        AddMessage(db, thread, MessageAuthor.User, actorId, MessageKind.Status, "Marked as merged into the default branch.", now);
        await ReleaseWaitersAsync(db, thread, BlockerRelease.Merged, $"\"{thread.Title}\" was merged. This task no longer waits for it.", now, ct);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return thread;
    }

    public async Task<IReadOnlyList<AwaitedPullRequest>> ListAwaitedPullRequestsAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        // A closed one is still asked about: it may be reopened and merged.
        return await (
            from t in db.Threads.AsNoTracking()
            where t.State == LifecycleState.Accepted && t.PullRequestNumber != null && t.PullRequestMergedAt == null
                && db.Blockers.Any(l => l.BlockerThreadId == t.Id && l.ReleasedAt == null)
            join p in db.Projects.AsNoTracking() on t.ProjectId equals p.Id
            orderby t.UpdatedAt
            select new AwaitedPullRequest(t.Id, p.GitHubOwner, p.GitHubRepository, p.DefaultBranch, t.PullRequestNumber!.Value))
            .ToListAsync(ct);
    }

    public async Task RecordPullRequestOutcomeAsync(Guid threadId, PullRequestOutcome outcome, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        if (thread.PullRequestMergedAt is not null)
            return;

        var pr = thread.PullRequestNumber is { } number ? $"PR #{number}" : "Its pull request";
        if (outcome == PullRequestOutcome.Merged)
        {
            thread.PullRequestMergedAt = now;
            thread.PullRequestClosedAt = null;
            AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status, $"GitHub reports {pr} merged into the default branch.", now);
            await ReleaseWaitersAsync(db, thread, BlockerRelease.Merged, $"\"{thread.Title}\" was merged. This task no longer waits for it.", now, ct);
        }
        else
        {
            if (thread.PullRequestClosedAt is not null)
                return;
            thread.PullRequestClosedAt = now;
            AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status, $"GitHub reports {pr} closed without merging. Tasks that wait for it stay waiting until their owners decide.", now);

            var reason = RunOrder.WaitingReason(new OpenBlocker(thread.Title, thread.State, thread.PullRequestNumber, PullRequestClosed: true));
            foreach (var waiting in await WaitersAsync(db, threadId, ct))
            {
                waiting.BlockerNeedsYou = true;
                if (waiting.State == LifecycleState.Queued)
                    waiting.StateReason = reason;
                AddMessage(db, waiting, MessageAuthor.Factory, null, MessageKind.Status, reason, now);
                Touch(db, waiting, now);
            }
        }

        Touch(db, thread, now);
        await write.CommitAsync(ct);
    }

    /// <summary>The live tasks with an open link to <paramref name="blockerId"/>, locked in id order.</summary>
    private async Task<List<TaskThread>> WaitersAsync(FactoryDbContext db, Guid blockerId, CancellationToken ct)
    {
        var ids = await db.Blockers.Where(l => l.BlockerThreadId == blockerId && l.ReleasedAt == null).Select(l => l.ThreadId).Distinct().ToListAsync(ct);
        var waiters = new List<TaskThread>();
        foreach (var id in ids.Order())
            waiters.Add(await LockThreadAsync(db, id, ct));
        return waiters;
    }

    /// <summary>Releases every open link to <paramref name="blocker"/>, telling each waiting task why.</summary>
    private async Task ReleaseWaitersAsync(FactoryDbContext db, TaskThread blocker, BlockerRelease reason, string note, DateTimeOffset now, CancellationToken ct)
    {
        var links = await db.Blockers.Where(l => l.BlockerThreadId == blocker.Id && l.ReleasedAt == null).ToListAsync(ct);
        if (links.Count == 0)
            return;

        foreach (var link in links)
            Release(link, reason, null, now);
        foreach (var waiting in await WaitersAsync(db, blocker.Id, ct))
        {
            AddMessage(db, waiting, MessageAuthor.Factory, null, MessageKind.Status, note, now);
            await AfterLinkReleasedAsync(db, waiting, ct);
            Touch(db, waiting, now);
        }
    }

    /// <summary>
    /// A cancelled task leaves the queue; the tasks waiting for it stop waiting and are told so, and
    /// its own links close, so nothing reminds anyone about a task that will never run.
    /// </summary>
    private async Task OnCancelledAsync(FactoryDbContext db, TaskThread thread, DateTimeOffset now, CancellationToken ct)
    {
        thread.QueueRank = null;
        thread.BlockerNeedsYou = false;
        foreach (var own in await db.Blockers.Where(l => l.ThreadId == thread.Id && l.ReleasedAt == null).ToListAsync(ct))
            Release(own, BlockerRelease.ThreadCancelled, null, now);
        await ReleaseWaitersAsync(db, thread, BlockerRelease.BlockerCancelled,
            $"\"{thread.Title}\" was cancelled, so this task no longer waits for it. Check it still makes sense.", now, ct);
    }

    // ---- Reminders ----

    public async Task<int> RemindOfLongWaitsAsync(TimeSpan after, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var due = now - after;
        var links = await db.Blockers
            .Where(l => l.ReleasedAt == null && l.CreatedAt <= due && (l.RemindedAt == null || l.RemindedAt <= due))
            .ToListAsync(ct);
        if (links.Count == 0)
            return 0;

        var threadIds = links.SelectMany(l => new[] { l.ThreadId, l.BlockerThreadId }).Distinct().ToList();
        var titles = await db.Threads.AsNoTracking().Where(t => threadIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);
        var reminded = 0;
        foreach (var group in links.GroupBy(l => l.BlockerThreadId).OrderBy(g => g.Key))
        {
            var blocker = await LockThreadAsync(db, group.Key, ct);
            var waiting = group.OrderBy(l => l.CreatedAt).ToList();
            var days = (int)Math.Floor((now - waiting[0].CreatedAt).TotalDays);
            var names = string.Join(", ", waiting.Select(l => $"\"{titles[l.ThreadId]}\""));
            var text = waiting.Count == 1
                ? $"{names} has waited {days} days for this task to be merged."
                : $"{waiting.Count} tasks have waited for this task to be merged, the longest {days} days: {names}.";
            AddMessage(db, blocker, MessageAuthor.Factory, null, MessageKind.Status, text, now);
            Touch(db, blocker, now);
            foreach (var link in waiting)
                link.RemindedAt = now;
            reminded++;
        }

        await write.CommitAsync(ct);
        return reminded;
    }
}
