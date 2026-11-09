using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>A project's run order and "Blocked by" links (docs/software-factory/run-order-architecture.md).</summary>
public abstract partial class FactoryStoreContract
{
    private async Task<TaskThread> DraftAsync(Project project, string title, bool runFirst = false, Guid? runAfter = null, Guid? owner = null) =>
        await Store.AddThreadAsync(new TaskThread
        {
            ProjectId = project.Id,
            OwnerId = owner ?? Admin,
            Title = title,
            SessionId = Guid.NewGuid().ToString("n"),
            Provider = "openrouter",
            Model = "deepseek/deepseek-v4.1-flash",
            RunFirst = runFirst,
            RunAfterThreadId = runAfter,
            CreatedAt = T0,
            UpdatedAt = T0,
        }, default);

    private async Task<TaskThread> DelegatedAsync(Project project, string title, DateTimeOffset? at = null, bool runFirst = false, Guid? runAfter = null, Guid? owner = null)
    {
        var thread = await DraftAsync(project, title, runFirst, runAfter, owner);
        var result = await Store.DispatchAsync(thread.Id, Admin, Guid.NewGuid().ToString(), $"Do {title}.", at ?? T0, default);
        Assert.Equal(DispatchOutcome.Queued, result.Outcome);
        return await ThreadAsync(thread.Id);
    }

    /// <summary>Claims and hands off, so the project is free again and the claimed thread awaits testing.</summary>
    private async Task<ClaimedRun> ClaimAndHandOffAsync(int slotCap = 5)
    {
        var claimed = (await Store.ClaimNextRunAsync(slotCap, T0, default))!;
        await Store.StopRunAsync(Stop(claimed.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff) with { Stage = Stage.Handoff }, T0, default);
        return claimed;
    }

    /// <summary>A link written straight to the table; the store's own way of adding one is tested with it.</summary>
    private async Task LinkAsync(TaskThread thread, TaskThread blocker, DateTimeOffset? at = null, DateTimeOffset? releasedAt = null)
    {
        await using var db = Contexts.CreateDbContext();
        db.Blockers.Add(new TaskBlocker
        {
            Id = Guid.NewGuid(),
            ProjectId = thread.ProjectId,
            ThreadId = thread.Id,
            BlockerThreadId = blocker.Id,
            CreatedBy = Admin,
            CreatedAt = at ?? T0,
            ReleasedAt = releasedAt,
            ReleaseReason = releasedAt is null ? null : BlockerRelease.Manual,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> ClaimOrderAsync(int count)
    {
        var titles = new List<string>();
        for (var i = 0; i < count; i++)
            titles.Add((await ClaimAndHandOffAsync()).Thread.Title);
        return titles;
    }

    // ---- Ranks ----

    /// <summary>With nobody arranging anything, a project's queue is first in, first out.</summary>
    [SkippableFact]
    public async Task Delegating_PutsEachTaskAtTheEndOfItsProjectsQueue()
    {
        var project = await AddProjectAsync();
        var a = await DelegatedAsync(project, "a");
        var b = await DelegatedAsync(project, "b", T0.AddMinutes(1));
        var c = await DelegatedAsync(project, "c", T0.AddMinutes(2));

        Assert.Equal([1000, 2000, 3000], new[] { a, b, c }.Select(t => t.QueueRank!.Value));
        Assert.Equal(["a", "b", "c"], await ClaimOrderAsync(3));
    }

    [SkippableFact]
    public async Task EachProject_HasAQueueOfItsOwn()
    {
        var p = await AddProjectAsync("p");
        var q = await AddProjectAsync("q");
        Assert.Equal(1000, (await DelegatedAsync(p, "p1")).QueueRank);
        Assert.Equal(1000, (await DelegatedAsync(q, "q1")).QueueRank);
        Assert.Equal(2000, (await DelegatedAsync(p, "p2")).QueueRank);
    }

    [SkippableFact]
    public async Task ADraftThatAskedToRunFirst_GoesAheadOfTasksDelegatedBeforeIt()
    {
        var project = await AddProjectAsync();
        await DelegatedAsync(project, "a");
        await DelegatedAsync(project, "b", T0.AddMinutes(1));
        var urgent = await DelegatedAsync(project, "urgent", T0.AddMinutes(2), runFirst: true);

        Assert.Equal(0, urgent.QueueRank);
        Assert.False(urgent.RunFirst); // used up once it is placed
        Assert.Equal(["urgent", "a", "b"], await ClaimOrderAsync(3));
    }

    [SkippableFact]
    public async Task ADraftThatAskedToRunAfterAQueuedTask_GoesStraightAfterIt()
    {
        var project = await AddProjectAsync();
        var a = await DelegatedAsync(project, "a");
        await DelegatedAsync(project, "b", T0.AddMinutes(1));
        var follower = await DelegatedAsync(project, "after a", T0.AddMinutes(2), runAfter: a.Id);

        Assert.Equal(1500, follower.QueueRank);
        Assert.Null(follower.RunAfterThreadId);
        Assert.Equal(["a", "after a", "b"], await ClaimOrderAsync(3));
    }

    /// <summary>The task it asked to follow has started ahead of it, so following it means going first.</summary>
    [SkippableFact]
    public async Task RunAfterATaskThatHasLeftTheQueue_GoesFirst()
    {
        var project = await AddProjectAsync();
        var a = await DelegatedAsync(project, "a");
        await DelegatedAsync(project, "b", T0.AddMinutes(1));
        var follower = await DraftAsync(project, "after a", runAfter: a.Id);
        await ClaimAndHandOffAsync(); // a starts, and is handed off

        await Store.DispatchAsync(follower.Id, Admin, "m-f", "Do it.", T0.AddMinutes(2), default);

        Assert.Equal(["after a", "b"], await ClaimOrderAsync(2));
    }

    /// <summary>Following a task that is still a draft has nothing to follow yet: the end of the queue.</summary>
    [SkippableFact]
    public async Task RunAfterADraft_GoesToTheEnd()
    {
        var project = await AddProjectAsync();
        var draft = await DraftAsync(project, "still a draft");
        await DelegatedAsync(project, "a");
        var follower = await DelegatedAsync(project, "after the draft", T0.AddMinutes(1), runAfter: draft.Id);

        Assert.Equal(2000, follower.QueueRank);
    }

    [SkippableFact]
    public async Task RunAfterATaskOfAnotherProject_IsIgnored()
    {
        var p = await AddProjectAsync("p");
        var q = await AddProjectAsync("q");
        var elsewhere = await DelegatedAsync(q, "elsewhere");
        await DelegatedAsync(p, "a");
        var follower = await DelegatedAsync(p, "follower", T0.AddMinutes(1), runAfter: elsewhere.Id);

        Assert.Equal(2000, follower.QueueRank);
    }

    [SkippableFact]
    public async Task ClaimingATask_TakesItOutOfTheQueue()
    {
        var project = await AddProjectAsync();
        var a = await DelegatedAsync(project, "a");

        await Store.ClaimNextRunAsync(5, T0, default);

        Assert.Null((await ThreadAsync(a.Id)).QueueRank);
    }

    /// <summary>Work handed off and coming back with changes goes ahead of new work.</summary>
    [SkippableFact]
    public async Task ARework_GoesToTheFrontOfTheQueue()
    {
        var project = await AddProjectAsync();
        var a = await DelegatedAsync(project, "a");
        await DelegatedAsync(project, "b", T0.AddMinutes(1));
        await DelegatedAsync(project, "c", T0.AddMinutes(2));
        await ClaimAndHandOffAsync(); // a awaits testing; b and c wait

        var rework = await Store.RequestChangesAsync(a.Id, Admin, "m-rw", "The button is the wrong colour.", 0.25, T0.AddMinutes(3), default);
        Assert.Equal(DispatchOutcome.Queued, rework.Outcome);
        Assert.Equal(1000, (await ThreadAsync(a.Id)).QueueRank); // b was 2000, so the front is 1000

        Assert.Equal(["a", "b", "c"], await ClaimOrderAsync(3));
    }

    // ---- The claim ----

    /// <summary>A project keeps its turn among projects from when its work arrived; its ranks decide which of its tasks takes that turn.</summary>
    [SkippableFact]
    public async Task Claim_TakesAProjectsFirstTaskInItsTurn_AmongOtherProjects()
    {
        var p = await AddProjectAsync("p");
        var q = await AddProjectAsync("q");
        await DelegatedAsync(p, "p-old", T0);
        await DelegatedAsync(q, "q1", T0.AddMinutes(1));
        await DelegatedAsync(p, "p-urgent", T0.AddMinutes(2), runFirst: true);

        // p's turn came first, and p-urgent takes it; then q's.
        Assert.Equal("p-urgent", (await Store.ClaimNextRunAsync(5, T0.AddMinutes(3), default))!.Thread.Title);
        Assert.Equal("q1", (await Store.ClaimNextRunAsync(5, T0.AddMinutes(3), default))!.Thread.Title);
        // p-old waits for p, held by p-urgent.
        Assert.Null(await Store.ClaimNextRunAsync(5, T0.AddMinutes(3), default));
    }

    /// <summary>A task queued before ranks existed has none, and keeps its first-in, first-out place ahead of ranked work.</summary>
    [SkippableFact]
    public async Task Claim_ATaskWithoutARank_GoesFirst()
    {
        var project = await AddProjectAsync();
        await DelegatedAsync(project, "ranked");
        var legacy = await DelegatedAsync(project, "legacy", T0.AddMinutes(1));
        await using (var db = Contexts.CreateDbContext())
        {
            await db.Threads.Where(t => t.Id == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.QueueRank, (double?)null));
        }

        Assert.Equal(["legacy", "ranked"], await ClaimOrderAsync(2));
    }

    [SkippableFact]
    public async Task Claim_SkipsABlockedTask_AndSaysWhatItWaitsFor_AndTakesTheNext()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Add scheduled_at column");
        var blocked = await DelegatedAsync(project, "Schedule stories");
        await DelegatedAsync(project, "Retry failures", T0.AddMinutes(1));
        await LinkAsync(blocked, blocker);

        Assert.Equal("Retry failures", (await Store.ClaimNextRunAsync(5, T0, default))!.Thread.Title);

        var waiting = await ThreadAsync(blocked.Id);
        Assert.Equal(LifecycleState.Queued, waiting.State);
        Assert.Equal("Waiting for \"Add scheduled_at column\" to be finished and merged.", waiting.StateReason);
        Assert.Equal(1000, waiting.QueueRank); // it keeps its place
    }

    /// <summary>Blocked is the most specific reason: it is given even while the repository is held.</summary>
    [SkippableFact]
    public async Task Claim_ABlockedTask_SaysItsBlocker_EvenWhenTheRepositoryIsHeld()
    {
        var project = await AddProjectAsync();
        var blocker = await DelegatedAsync(project, "First");
        var blocked = await DelegatedAsync(project, "Second", T0.AddMinutes(1));
        await LinkAsync(blocked, blocker);
        await Store.ClaimNextRunAsync(5, T0, default); // First runs and holds the project

        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default));
        Assert.Equal("Waiting for \"First\" to be finished and merged.", (await ThreadAsync(blocked.Id)).StateReason);
    }

    [SkippableFact]
    public async Task Claim_EveryTaskBlocked_StartsNothing()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Not delegated");
        var blocked = await DelegatedAsync(project, "Waits");
        await LinkAsync(blocked, blocker);

        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default));
        Assert.Equal(LifecycleState.Queued, (await ThreadAsync(blocked.Id)).State);
    }

    [SkippableFact]
    public async Task Claim_AReleasedLink_NoLongerBlocks()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Not delegated");
        var blocked = await DelegatedAsync(project, "Waits");
        await LinkAsync(blocked, blocker, releasedAt: T0);

        Assert.Equal(blocked.Id, (await Store.ClaimNextRunAsync(5, T0, default))!.Thread.Id);
    }

    /// <summary>A specification written before the work it depends on is merged would describe code without it.</summary>
    [SkippableFact]
    public async Task Claim_ASpecRun_WaitsForItsBlockersToo()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Add column");
        var blocked = await DraftAsync(project, "Use the column");
        await LinkAsync(blocked, blocker);
        var spec = await Store.RequestSpecAsync(blocked.Id, Admin, "m-spec", "Use the new column.", T0, default);
        Assert.Equal(DispatchOutcome.Queued, spec.Outcome);

        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default));
        Assert.Equal("Waiting for \"Add column\" to be finished and merged.", (await ThreadAsync(blocked.Id)).StateReason);
    }

    /// <summary>Of several blockers, a closed pull request is named first: it is the one a person must act on.</summary>
    [SkippableFact]
    public async Task Claim_OfSeveralBlockers_NamesTheClosedPullRequestFirst()
    {
        var project = await AddProjectAsync();
        var running = await DraftAsync(project, "Still being written");
        var closed = await DraftAsync(project, "Abandoned");
        await using (var db = Contexts.CreateDbContext())
        {
            await db.Threads.Where(t => t.Id == closed.Id).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.State, LifecycleState.Accepted)
                .SetProperty(t => t.PullRequestNumber, (int?)44)
                .SetProperty(t => t.PullRequestClosedAt, (DateTimeOffset?)T0));
        }

        var blocked = await DelegatedAsync(project, "Waits");
        await LinkAsync(blocked, running, T0);
        await LinkAsync(blocked, closed, T0.AddMinutes(1));

        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default));
        Assert.StartsWith("\"Abandoned\"'s pull request (PR #44) was closed without merging.", (await ThreadAsync(blocked.Id)).StateReason);
    }

    // ---- Helpers for the arranging tests ----

    /// <summary>An accepted task with an open pull request, as a handoff and acceptance would leave it.</summary>
    private async Task<TaskThread> AcceptedAsync(Project project, string title, int? pullRequest = 44)
    {
        var thread = await DraftAsync(project, title);
        await using var db = Contexts.CreateDbContext();
        await db.Threads.Where(t => t.Id == thread.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.State, LifecycleState.Accepted)
            .SetProperty(t => t.Stage, Stage.Done)
            .SetProperty(t => t.PullRequestNumber, pullRequest));
        return await ThreadAsync(thread.Id);
    }

    private async Task<List<TaskBlocker>> LinksOfAsync(Guid threadId)
    {
        await using var db = Contexts.CreateDbContext();
        return await db.Blockers.AsNoTracking().Where(l => l.ThreadId == threadId).ToListAsync();
    }

    private async Task<List<string>> AuditActionsAsync(Guid threadId)
    {
        await using var db = Contexts.CreateDbContext();
        return await db.AuditEvents.AsNoTracking().Where(a => a.TargetId == threadId).OrderBy(a => a.CreatedAt).Select(a => a.Action).ToListAsync();
    }

    private async Task<List<string>> NotesAsync(Guid threadId) =>
        (await Store.GetThreadAsync(threadId, default))!.Messages.Where(m => m.Kind == MessageKind.Status).Select(m => m.Text).ToList();

    // ---- Moving ----

    [SkippableFact]
    public async Task AnAdmin_PutsATaskFirst_AndTheMoveIsAudited()
    {
        var project = await AddProjectAsync();
        await DelegatedAsync(project, "a");
        await DelegatedAsync(project, "b");
        var c = await DelegatedAsync(project, "c");

        var moved = await Store.MoveInQueueAsync(c.Id, Admin, canArrangeAny: true, Core.Queue.Placement.First, c.Revision, T0, default);

        Assert.Equal(0, moved.QueueRank);
        Assert.Contains(AuditActions.ThreadQueueMove, await AuditActionsAsync(c.Id));
        Assert.Equal(["c", "a", "b"], await ClaimOrderAsync(3));
    }

    [SkippableFact]
    public async Task AnAdmin_MovesATaskAfterAnother_OrToTheEnd()
    {
        var project = await AddProjectAsync();
        var a = await DelegatedAsync(project, "a");
        var b = await DelegatedAsync(project, "b");
        var c = await DelegatedAsync(project, "c");

        await Store.MoveInQueueAsync(c.Id, Admin, true, Core.Queue.Placement.After(a.Id), null, T0, default);
        await Store.MoveInQueueAsync(a.Id, Admin, true, Core.Queue.Placement.End, null, T0, default);

        Assert.Equal(["c", "b", "a"], await ClaimOrderAsync(3));
        _ = b;
    }

    [SkippableFact]
    public async Task AMove_FromAStaleView_IsAConflict()
    {
        var project = await AddProjectAsync();
        await DelegatedAsync(project, "a");
        var b = await DelegatedAsync(project, "b");

        var ex = await Assert.ThrowsAsync<StoreConflictException>(() =>
            Store.MoveInQueueAsync(b.Id, Admin, true, Core.Queue.Placement.First, b.Revision - 1, T0, default));
        Assert.Equal(Infrastructure.Persistence.EfFactoryStore.StaleView, ex.Message);
    }

    [SkippableFact]
    public async Task OnlyAQueuedTask_CanBeMoved()
    {
        var project = await AddProjectAsync();
        var draft = await DraftAsync(project, "draft");
        var running = await DelegatedAsync(project, "running");
        await Store.ClaimNextRunAsync(5, T0, default);

        foreach (var id in new[] { draft.Id, running.Id })
        {
            var ex = await Assert.ThrowsAsync<StoreConflictException>(() => Store.MoveInQueueAsync(id, Admin, true, Core.Queue.Placement.First, null, T0, default));
            Assert.Equal(Infrastructure.Persistence.EfFactoryStore.OnlyQueuedTasksMove, ex.Message);
        }
    }

    [SkippableFact]
    public async Task AMoveAfterATaskOutsideTheQueue_IsAConflict()
    {
        var p = await AddProjectAsync("p");
        var q = await AddProjectAsync("q");
        var a = await DelegatedAsync(p, "a");
        var elsewhere = await DelegatedAsync(q, "elsewhere");
        var draft = await DraftAsync(p, "draft");

        foreach (var other in new[] { elsewhere.Id, draft.Id, a.Id })
            await Assert.ThrowsAsync<StoreConflictException>(() => Store.MoveInQueueAsync(a.Id, Admin, true, Core.Queue.Placement.After(other), null, T0, default));
    }

    /// <summary>A Member moves only their own tasks, by swapping, so nobody else's task changes place.</summary>
    [SkippableFact]
    public async Task AMember_SwapsTwoOfTheirOwnTasks_AndNobodyElsesMoves()
    {
        var project = await AddProjectAsync();
        var mine1 = await DelegatedAsync(project, "ben 1", owner: Ben);
        var admins = await DelegatedAsync(project, "asha");
        var mine2 = await DelegatedAsync(project, "ben 2", owner: Ben);

        await Store.MoveInQueueAsync(mine2.Id, Ben, canArrangeAny: false, Core.Queue.Placement.SwapWith(mine1.Id), null, T0, default);

        Assert.Equal(2000, (await ThreadAsync(admins.Id)).QueueRank);
        Assert.Equal(["ben 2", "asha", "ben 1"], await ClaimOrderAsync(3));
    }

    [SkippableFact]
    public async Task AMember_CannotPutATaskFirst_MoveSomeoneElses_OrSwapWithSomeoneElses()
    {
        var project = await AddProjectAsync();
        var admins = await DelegatedAsync(project, "asha");
        var mine = await DelegatedAsync(project, "ben", owner: Ben);

        var first = await Assert.ThrowsAsync<StoreForbiddenException>(() => Store.MoveInQueueAsync(mine.Id, Ben, false, Core.Queue.Placement.First, null, T0, default));
        Assert.Equal(Infrastructure.Persistence.EfFactoryStore.MembersSwapOwnTasks, first.Message);
        await Assert.ThrowsAsync<StoreForbiddenException>(() => Store.MoveInQueueAsync(mine.Id, Ben, false, Core.Queue.Placement.After(admins.Id), null, T0, default));
        await Assert.ThrowsAsync<StoreForbiddenException>(() => Store.MoveInQueueAsync(mine.Id, Ben, false, Core.Queue.Placement.SwapWith(admins.Id), null, T0, default));
        var others = await Assert.ThrowsAsync<StoreForbiddenException>(() => Store.MoveInQueueAsync(admins.Id, Ben, false, Core.Queue.Placement.SwapWith(mine.Id), null, T0, default));
        Assert.Equal(Infrastructure.Persistence.EfFactoryStore.OwnerOrAdminArranges, others.Message);
    }

    // ---- Links ----

    [SkippableFact]
    public async Task AddingABlocker_MakesTheTaskWait_SaysSo_AndIsAudited()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");

        var thread = await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);

        Assert.Equal("Waiting for \"Add column\" to be finished and merged.", thread.StateReason);
        Assert.Single(await LinksOfAsync(waiting.Id));
        Assert.Contains("Waits for \"Add column\" to be merged before it starts.", await NotesAsync(waiting.Id));
        Assert.Contains(AuditActions.ThreadBlockerAdd, await AuditActionsAsync(waiting.Id));
        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default));
    }

    [SkippableFact]
    public async Task ADraft_CanBeGivenABlocker_BeforeItIsDelegated()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Add column");
        var draft = await DraftAsync(project, "Use column");

        await Store.AddBlockerAsync(draft.Id, blocker.Id, Admin, true, T0, default);
        await Store.DispatchAsync(draft.Id, Admin, "m-d", "Go.", T0, default);

        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default));
    }

    [SkippableFact]
    public async Task ABlocker_MustBeAnotherLiveUnmergedTaskOfTheSameProject_AndOnlyOnce()
    {
        var p = await AddProjectAsync("p");
        var q = await AddProjectAsync("q");
        var waiting = await DraftAsync(p, "waiting");
        var elsewhere = await DraftAsync(q, "elsewhere");
        var cancelled = await DraftAsync(p, "cancelled");
        await Store.ApplyUserActionAsync(cancelled.Id, Admin, LifecycleTrigger.Cancel, T0, default);
        var merged = await AcceptedAsync(p, "merged");
        await Store.MarkMergedAsync(merged.Id, Admin, T0, default);
        var closed = await AcceptedAsync(p, "closed", 45);
        var watcher = await DraftAsync(p, "keeps the closed one watched");
        await LinkAsync(watcher, closed);
        await Store.RecordPullRequestOutcomeAsync(closed.Id, PullRequestOutcome.Closed, T0, default);
        var fine = await DraftAsync(p, "fine");
        await Store.AddBlockerAsync(waiting.Id, fine.Id, Admin, true, T0, default);

        foreach (var (blocker, expected) in new (Guid, string)[]
        {
            (waiting.Id, "A task cannot wait for itself."),
            (elsewhere.Id, "A task can wait only for another task of the same project."),
            (cancelled.Id, "\"cancelled\" was cancelled: there is nothing to wait for."),
            (merged.Id, "\"merged\" is already merged: there is nothing to wait for."),
            (closed.Id, "\"closed\"'s pull request was closed without merging, so it would never be merged."),
            (fine.Id, "This task already waits for \"fine\"."),
        })
        {
            var ex = await Assert.ThrowsAsync<StoreConflictException>(() => Store.AddBlockerAsync(waiting.Id, blocker, Admin, true, T0, default));
            Assert.Equal(expected, ex.Message);
        }
    }

    [SkippableFact]
    public async Task ALinkThatWouldMakeALoop_IsRefused_NamingTheLoop()
    {
        var project = await AddProjectAsync();
        var a = await DraftAsync(project, "A");
        var b = await DraftAsync(project, "B");
        var c = await DraftAsync(project, "C");
        await Store.AddBlockerAsync(b.Id, a.Id, Admin, true, T0, default); // B waits for A
        await Store.AddBlockerAsync(c.Id, b.Id, Admin, true, T0, default); // C waits for B

        var ex = await Assert.ThrowsAsync<StoreConflictException>(() => Store.AddBlockerAsync(a.Id, c.Id, Admin, true, T0, default));

        Assert.Equal("Not linked: that would make a loop. \"A\" would wait for \"C\", which waits for \"B\", which waits for \"A\".", ex.Message);
        Assert.Empty(await LinksOfAsync(a.Id));
    }

    [SkippableFact]
    public async Task AReleasedLink_DoesNotCountTowardsALoop()
    {
        var project = await AddProjectAsync();
        var a = await DraftAsync(project, "A");
        var b = await DraftAsync(project, "B");
        await Store.AddBlockerAsync(b.Id, a.Id, Admin, true, T0, default);
        await Store.ReleaseBlockerAsync(b.Id, a.Id, Admin, true, T0, default);

        await Store.AddBlockerAsync(a.Id, b.Id, Admin, true, T0, default);
        Assert.Single(await LinksOfAsync(a.Id));
    }

    [SkippableFact]
    public async Task ATaskThatHasStarted_CannotBeMadeToWait()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "blocker");
        var running = await DelegatedAsync(project, "running");
        await Store.ClaimNextRunAsync(5, T0, default);

        var ex = await Assert.ThrowsAsync<StoreConflictException>(() => Store.AddBlockerAsync(running.Id, blocker.Id, Admin, true, T0, default));
        Assert.Equal(Infrastructure.Persistence.EfFactoryStore.LinkOnlyBeforeStart, ex.Message);
    }

    /// <summary>A rework continues its own branch, so waiting for another merge would not give it that work.</summary>
    [SkippableFact]
    public async Task AQueuedRework_CannotBeMadeToWait()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "blocker");
        var task = await DelegatedAsync(project, "task");
        await ClaimAndHandOffAsync();
        await using (var db = Contexts.CreateDbContext())
            await db.Threads.Where(t => t.Id == task.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Branch, "litos/task"));
        await Store.RequestChangesAsync(task.Id, Admin, "m-rw", "Again.", 0.25, T0, default);

        await Assert.ThrowsAsync<StoreConflictException>(() => Store.AddBlockerAsync(task.Id, blocker.Id, Admin, true, T0, default));
    }

    [SkippableFact]
    public async Task OnlyTheOwnerOrAnAdmin_LinksOrReleasesATask()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "blocker");
        var admins = await DraftAsync(project, "asha's");
        var bens = await DraftAsync(project, "ben's", owner: Ben);

        await Assert.ThrowsAsync<StoreForbiddenException>(() => Store.AddBlockerAsync(admins.Id, blocker.Id, Ben, false, T0, default));
        await Store.AddBlockerAsync(bens.Id, blocker.Id, Ben, false, T0, default);
        await Store.AddBlockerAsync(admins.Id, blocker.Id, Admin, false, T0, default);
        await Assert.ThrowsAsync<StoreForbiddenException>(() => Store.ReleaseBlockerAsync(admins.Id, blocker.Id, Ben, false, T0, default));
        await Store.ReleaseBlockerAsync(bens.Id, blocker.Id, Admin, true, T0, default);
    }

    [SkippableFact]
    public async Task ReleaseNow_LetsTheTaskRun_AndRecordsWhoAndWhy()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);
        await Store.ClaimNextRunAsync(5, T0, default);

        var released = await Store.ReleaseBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0.AddMinutes(1), default);

        Assert.Null(released.StateReason);
        var link = Assert.Single(await LinksOfAsync(waiting.Id));
        Assert.Equal((T0.AddMinutes(1), (Guid?)Admin, (BlockerRelease?)BlockerRelease.Manual), (link.ReleasedAt!.Value, link.ReleasedBy, link.ReleaseReason));
        Assert.Contains("Released from waiting for \"Add column\". It runs without that work.", await NotesAsync(waiting.Id));
        Assert.Contains(AuditActions.ThreadBlockerRelease, await AuditActionsAsync(waiting.Id));
        Assert.Equal(waiting.Id, (await Store.ClaimNextRunAsync(5, T0, default))!.Thread.Id);
    }

    [SkippableFact]
    public async Task ReleasingALinkThatIsNotThere_IsAConflict()
    {
        var project = await AddProjectAsync();
        var a = await DraftAsync(project, "A");
        var b = await DraftAsync(project, "B");
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.ReleaseBlockerAsync(a.Id, b.Id, Admin, true, T0, default));
    }

    // ---- Merges ----

    [SkippableFact]
    public async Task MarkMerged_ReleasesEveryTaskThatWaits_AndTellsThem()
    {
        var project = await AddProjectAsync();
        var blocker = await AcceptedAsync(project, "Add column");
        var first = await DelegatedAsync(project, "first");
        var second = await DelegatedAsync(project, "second");
        await Store.AddBlockerAsync(first.Id, blocker.Id, Admin, true, T0, default);
        await Store.AddBlockerAsync(second.Id, blocker.Id, Admin, true, T0, default);

        var merged = await Store.MarkMergedAsync(blocker.Id, Ben, T0.AddHours(1), default);

        Assert.Equal(T0.AddHours(1), merged.PullRequestMergedAt);
        Assert.Contains(AuditActions.ThreadMarkMerged, await AuditActionsAsync(blocker.Id));
        foreach (var waiting in new[] { first, second })
        {
            Assert.Equal(BlockerRelease.Merged, Assert.Single(await LinksOfAsync(waiting.Id)).ReleaseReason);
            Assert.Contains("\"Add column\" was merged. This task no longer waits for it.", await NotesAsync(waiting.Id));
        }

        Assert.Equal(["first", "second"], await ClaimOrderAsync(2));
    }

    [SkippableFact]
    public async Task MarkMerged_IsOnlyForAnAcceptedTask_AndOnlyOnce()
    {
        var project = await AddProjectAsync();
        var draft = await DraftAsync(project, "draft");
        var accepted = await AcceptedAsync(project, "accepted");

        await Assert.ThrowsAsync<StoreConflictException>(() => Store.MarkMergedAsync(draft.Id, Admin, T0, default));
        await Store.MarkMergedAsync(accepted.Id, Admin, T0, default);
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.MarkMergedAsync(accepted.Id, Admin, T0, default));
    }

    [SkippableFact]
    public async Task AwaitedPullRequests_AreTheAcceptedOnesThatSomeTaskWaitsFor()
    {
        var project = await AddProjectAsync();
        var awaited = await AcceptedAsync(project, "awaited", 44);
        await AcceptedAsync(project, "nobody waits", 45);
        var noPr = await AcceptedAsync(project, "no pull request", null);
        var waiting = await DraftAsync(project, "waiting");
        await Store.AddBlockerAsync(waiting.Id, awaited.Id, Admin, true, T0, default);
        await Store.AddBlockerAsync(waiting.Id, noPr.Id, Admin, true, T0, default);

        var list = await Store.ListAwaitedPullRequestsAsync(default);

        var only = Assert.Single(list);
        Assert.Equal((awaited.Id, "acme", "salesapp", "main", 44), (only.ThreadId, only.Owner, only.Repository, only.DefaultBranch, only.Number));
    }

    [SkippableFact]
    public async Task GitHubReportingAMerge_ReleasesTheWaitingTasks_AndSayingItAgainChangesNothing()
    {
        var project = await AddProjectAsync();
        var blocker = await AcceptedAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);

        await Store.RecordPullRequestOutcomeAsync(blocker.Id, PullRequestOutcome.Merged, T0, default);
        var revision = (await ThreadAsync(blocker.Id)).Revision;
        await Store.RecordPullRequestOutcomeAsync(blocker.Id, PullRequestOutcome.Merged, T0.AddMinutes(2), default);

        Assert.Equal(revision, (await ThreadAsync(blocker.Id)).Revision);
        Assert.Contains("GitHub reports PR #44 merged into the default branch.", await NotesAsync(blocker.Id));
        Assert.Empty(await Store.ListAwaitedPullRequestsAsync(default));
        Assert.Equal(waiting.Id, (await Store.ClaimNextRunAsync(5, T0, default))!.Thread.Id);
    }

    /// <summary>A closed pull request leaves the task waiting and puts the decision with its owner: release it or cancel it.</summary>
    [SkippableFact]
    public async Task APullRequestClosedWithoutMerging_LeavesTheTaskWaiting_AndAwaitingItsOwner()
    {
        var project = await AddProjectAsync();
        var blocker = await AcceptedAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);

        await Store.RecordPullRequestOutcomeAsync(blocker.Id, PullRequestOutcome.Closed, T0, default);

        var after = await ThreadAsync(waiting.Id);
        Assert.True(after.BlockerNeedsYou);
        Assert.Equal(TurnLabel.AwaitingYou, TurnLabels.For(after.State, after.Stage, after.BlockerNeedsYou));
        Assert.StartsWith("\"Add column\"'s pull request (PR #44) was closed without merging.", after.StateReason);
        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default));
        // Still asked about: it may be reopened and merged.
        Assert.Single(await Store.ListAwaitedPullRequestsAsync(default));

        // The owner releases it: nobody needs to act any more, and it runs.
        var released = await Store.ReleaseBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);
        Assert.False(released.BlockerNeedsYou);
        Assert.Equal(waiting.Id, (await Store.ClaimNextRunAsync(5, T0, default))!.Thread.Id);
    }

    [SkippableFact]
    public async Task APullRequestReopenedAndMerged_AfterBeingClosed_ReleasesTheTask()
    {
        var project = await AddProjectAsync();
        var blocker = await AcceptedAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);
        await Store.RecordPullRequestOutcomeAsync(blocker.Id, PullRequestOutcome.Closed, T0, default);

        await Store.RecordPullRequestOutcomeAsync(blocker.Id, PullRequestOutcome.Merged, T0.AddHours(1), default);

        var after = await ThreadAsync(waiting.Id);
        Assert.False(after.BlockerNeedsYou);
        Assert.Null((await ThreadAsync(blocker.Id)).PullRequestClosedAt);
        Assert.Equal(waiting.Id, (await Store.ClaimNextRunAsync(5, T0, default))!.Thread.Id);
    }

    /// <summary>Of two blockers, releasing the closed one leaves nobody needing to act, though the task still waits.</summary>
    [SkippableFact]
    public async Task BlockerNeedsYou_FollowsTheLinksStillOpen()
    {
        var project = await AddProjectAsync();
        var closed = await AcceptedAsync(project, "closed", 44);
        var open = await AcceptedAsync(project, "open", 45);
        var waiting = await DelegatedAsync(project, "waiting");
        await Store.AddBlockerAsync(waiting.Id, closed.Id, Admin, true, T0, default);
        await Store.AddBlockerAsync(waiting.Id, open.Id, Admin, true, T0, default);
        await Store.RecordPullRequestOutcomeAsync(closed.Id, PullRequestOutcome.Closed, T0, default);
        Assert.True((await ThreadAsync(waiting.Id)).BlockerNeedsYou);

        var released = await Store.ReleaseBlockerAsync(waiting.Id, closed.Id, Admin, true, T0, default);

        Assert.False(released.BlockerNeedsYou);
        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default));
        Assert.Equal("Waiting for \"open\" to be merged (PR #45).", (await ThreadAsync(waiting.Id)).StateReason);
    }

    // ---- Cancelling ----

    [SkippableFact]
    public async Task CancellingABlocker_ReleasesTheTasksThatWait_AndTellsThem()
    {
        var project = await AddProjectAsync();
        var blocker = await DelegatedAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);

        await Store.ApplyUserActionAsync(blocker.Id, Admin, LifecycleTrigger.Cancel, T0, default);

        Assert.Null((await ThreadAsync(blocker.Id)).QueueRank);
        Assert.Equal(BlockerRelease.BlockerCancelled, Assert.Single(await LinksOfAsync(waiting.Id)).ReleaseReason);
        Assert.Contains("\"Add column\" was cancelled, so this task no longer waits for it. Check it still makes sense.", await NotesAsync(waiting.Id));
        Assert.Equal(waiting.Id, (await Store.ClaimNextRunAsync(5, T0, default))!.Thread.Id);
    }

    /// <summary>A running blocker is cancelled through its run, and the same happens.</summary>
    [SkippableFact]
    public async Task CancellingARunningBlocker_ThroughItsRun_ReleasesTheTasksThatWait()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);
        await Store.DispatchAsync(blocker.Id, Admin, "m-b", "Go.", T0, default);
        var claimed = (await Store.ClaimNextRunAsync(5, T0, default))!;
        Assert.Equal(blocker.Id, claimed.Thread.Id);

        await Store.StopRunAsync(Stop(claimed.Run.Id, LifecycleTrigger.Cancel, StopReason.Cancelled), T0, default);

        Assert.Equal(BlockerRelease.BlockerCancelled, Assert.Single(await LinksOfAsync(waiting.Id)).ReleaseReason);
        Assert.Equal(waiting.Id, (await Store.ClaimNextRunAsync(5, T0, default))!.Thread.Id);
    }

    [SkippableFact]
    public async Task CancellingAWaitingTask_ClosesItsOwnLinks_SoNobodyIsRemindedOfIt()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);

        await Store.ApplyUserActionAsync(waiting.Id, Admin, LifecycleTrigger.Cancel, T0, default);

        Assert.Equal(BlockerRelease.ThreadCancelled, Assert.Single(await LinksOfAsync(waiting.Id)).ReleaseReason);
        Assert.Equal(0, await Store.RemindOfLongWaitsAsync(TimeSpan.FromDays(2), T0.AddDays(3), default));
    }

    // ---- Reminders ----

    [SkippableFact]
    public async Task ALongWait_RemindsTheBlockersOwner_OncePerInterval()
    {
        var project = await AddProjectAsync();
        var blocker = await AcceptedAsync(project, "Add column");
        var first = await DelegatedAsync(project, "first");
        var second = await DelegatedAsync(project, "second");
        await Store.AddBlockerAsync(first.Id, blocker.Id, Admin, true, T0, default);
        await Store.AddBlockerAsync(second.Id, blocker.Id, Admin, true, T0.AddHours(1), default);
        var after = TimeSpan.FromDays(2);

        Assert.Equal(0, await Store.RemindOfLongWaitsAsync(after, T0.AddDays(1), default)); // not long enough yet
        Assert.Equal(1, await Store.RemindOfLongWaitsAsync(after, T0.AddDays(3), default));
        Assert.Contains("2 tasks have waited for this task to be merged, the longest 3 days: \"first\", \"second\".", await NotesAsync(blocker.Id));
        Assert.Equal(0, await Store.RemindOfLongWaitsAsync(after, T0.AddDays(4), default)); // reminded a day ago
        Assert.Equal(1, await Store.RemindOfLongWaitsAsync(after, T0.AddDays(5), default));
    }

    [SkippableFact]
    public async Task ALongWait_ForOneTask_IsSaidOfThatTask()
    {
        var project = await AddProjectAsync();
        var blocker = await AcceptedAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await Store.AddBlockerAsync(waiting.Id, blocker.Id, Admin, true, T0, default);

        await Store.RemindOfLongWaitsAsync(TimeSpan.FromDays(2), T0.AddDays(2), default);

        Assert.Contains("\"Use column\" has waited 2 days for this task to be merged.", await NotesAsync(blocker.Id));
    }

    // ---- Reading ----

    [SkippableFact]
    public async Task TheRunOrder_ListsTheHolder_ThenTheQueueInOrder_WithLinks()
    {
        var project = await AddProjectAsync();
        var running = await DelegatedAsync(project, "running");
        await Store.ClaimNextRunAsync(5, T0, default);
        var blocker = await DraftAsync(project, "Add column");
        var waits = await DelegatedAsync(project, "waits");
        var next = await DelegatedAsync(project, "next", runFirst: true);
        var specced = await DraftAsync(project, "being specified");
        await Store.RequestSpecAsync(specced.Id, Admin, "m-s", "Spec it.", T0, default);
        await Store.AddBlockerAsync(waits.Id, blocker.Id, Admin, true, T0, default);
        await Store.AddBlockerAsync(next.Id, waits.Id, Admin, true, T0, default);

        var order = await Store.GetRunOrderAsync(project.Id, default);

        Assert.Equal(running.Id, order.Holder!.Id);
        Assert.Equal(["next", "waits"], order.Queue.Select(e => e.Thread.Title));
        var waitsEntry = order.Queue[1];
        Assert.Equal("Add column", Assert.Single(waitsEntry.WaitsFor).Other.Title);
        Assert.Equal(1, waitsEntry.WaitedOnBy);
        Assert.False(waitsEntry.IsRework);
        Assert.Equal("waits", Assert.Single(order.Queue[0].WaitsFor).Other.Title);
    }

    [SkippableFact]
    public async Task TheRunOrder_MarksAQueuedRework()
    {
        var project = await AddProjectAsync();
        var task = await DelegatedAsync(project, "task");
        await ClaimAndHandOffAsync();
        await Store.RequestChangesAsync(task.Id, Admin, "m-rw", "Again.", 0.25, T0, default);

        var order = await Store.GetRunOrderAsync(project.Id, default);

        Assert.Null(order.Holder);
        Assert.True(Assert.Single(order.Queue).IsRework);
    }

    [SkippableFact]
    public async Task AThreadsLinks_ShowWhatItWaitsFor_OpenFirst_AndWhatWaitsForIt()
    {
        var project = await AddProjectAsync();
        var a = await DraftAsync(project, "A");
        var b = await DraftAsync(project, "B");
        var c = await DraftAsync(project, "C");
        var d = await DraftAsync(project, "D");
        await Store.AddBlockerAsync(b.Id, a.Id, Admin, true, T0, default);
        await Store.ReleaseBlockerAsync(b.Id, a.Id, Admin, true, T0, default);
        await Store.AddBlockerAsync(b.Id, c.Id, Admin, true, T0.AddMinutes(1), default);
        await Store.AddBlockerAsync(d.Id, b.Id, Admin, true, T0, default);

        var links = await Store.GetThreadLinksAsync(b.Id, default);

        Assert.Equal(["C", "A"], links.WaitsFor.Select(l => l.Other.Title));
        Assert.Null(links.WaitsFor[0].Link.ReleasedAt);
        Assert.Equal("D", Assert.Single(links.WaitedOnBy).Other.Title);
    }

    [SkippableFact]
    public async Task Claim_ABlockedTasksReason_IsNotRewrittenWhenItIsTheSame()
    {
        var project = await AddProjectAsync();
        var blocker = await DraftAsync(project, "Not delegated");
        var blocked = await DelegatedAsync(project, "Waits");
        await LinkAsync(blocked, blocker);
        await Store.ClaimNextRunAsync(5, T0, default);
        var revision = (await ThreadAsync(blocked.Id)).Revision;

        await Store.ClaimNextRunAsync(5, T0.AddSeconds(5), default);

        Assert.Equal(revision, (await ThreadAsync(blocked.Id)).Revision);
    }
}
