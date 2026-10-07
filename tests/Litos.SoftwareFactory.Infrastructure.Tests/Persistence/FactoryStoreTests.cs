using System.Text.Json;
using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

internal sealed class TestContextFactory(DbContextOptions<FactoryDbContext> options) : IDbContextFactory<FactoryDbContext>
{
    public FactoryDbContext CreateDbContext() => new(options);
}

/// <summary>
/// The store's contract, run against whichever database a subclass provides. SQLite runs
/// everywhere and fast; PostgreSQL is the real target, with the real migration, row locks and
/// advisory lock, and is skipped with a message when no server is configured.
/// </summary>
public abstract class FactoryStoreContract : IAsyncLifetime
{
    protected static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    protected static readonly Guid Admin = Guid.Parse("11111111-1111-1111-1111-111111111111");
    protected static readonly BudgetPolicy Policy = new() { OutputAllowanceTokens = 4_000, Margin = 0.10 };

    protected IFactoryStore Store { get; private set; } = null!;

    protected IDbContextFactory<FactoryDbContext> Contexts { get; private set; } = null!;

    protected abstract Task<IDbContextFactory<FactoryDbContext>> CreateDatabaseAsync();

    protected abstract Task DropDatabaseAsync();

    public async Task InitializeAsync()
    {
        Contexts = await CreateDatabaseAsync();
        Store = new EfFactoryStore(Contexts);
    }

    public Task DisposeAsync() => DropDatabaseAsync();

    protected async Task<Project> AddProjectAsync(string repository = "salesapp") => await Store.AddProjectAsync(new Project
    {
        Name = repository,
        GitHubOwner = "acme",
        GitHubRepository = repository,
        DefaultBranch = "main",
        VerificationProfileJson = """{"profileVersion":2,"steps":[]}""",
        CreatedBy = Admin,
        CreatedAt = T0,
    }, default);

    protected async Task<TaskThread> AddThreadAsync(Project project, string title = "Add CSV export", long? cap = null) =>
        await Store.AddThreadAsync(new TaskThread
        {
            ProjectId = project.Id,
            OwnerId = Admin,
            Title = title,
            SessionId = Guid.NewGuid().ToString("n"),
            Provider = "openrouter",
            Model = "deepseek/deepseek-v4.1-flash",
            BudgetCap = cap,
            CreatedAt = T0,
            UpdatedAt = T0,
        }, default);

    protected async Task<(Project Project, TaskThread Thread, Guid RunId)> QueuedAsync(string repository = "salesapp", long? cap = null)
    {
        var project = await AddProjectAsync(repository);
        var thread = await AddThreadAsync(project, cap: cap);
        var dispatch = await Store.DispatchAsync(thread.Id, Admin, Guid.NewGuid().ToString(), "Add CSV export for Orders.", T0, default);
        return (project, thread, dispatch.RunId!.Value);
    }

    protected async Task<ClaimedRun> RunningAsync(string repository = "salesapp", long? cap = null)
    {
        await QueuedAsync(repository, cap);
        return (await Store.ClaimNextRunAsync(slotCap: 5, T0, default))!;
    }

    protected async Task<TaskThread> ThreadAsync(Guid id) => (await Store.GetThreadAsync(id, default))!.Thread;

    protected static StopRunCommand Stop(Guid runId, LifecycleTrigger trigger, StopReason reason, string message = "stopped") =>
        new(runId, trigger, reason, message);

    // ---- Projects and threads ----

    [SkippableFact]
    public async Task Project_RoundTrips_AndTheSameRepositoryCannotBeRegisteredTwice()
    {
        var project = await AddProjectAsync();

        var loaded = await Store.GetProjectAsync(project.Id, default);
        Assert.Equal("acme", loaded!.GitHubOwner);
        Assert.Equal(T0, loaded.CreatedAt);
        Assert.Single(await Store.ListProjectsAsync(default));

        await Assert.ThrowsAsync<StoreConflictException>(() => AddProjectAsync());
    }

    [SkippableFact]
    public async Task Thread_StartsAsADraftInDiscuss_AndNeedsAnExistingProject()
    {
        var project = await AddProjectAsync();
        var thread = await AddThreadAsync(project, cap: 150_000);

        var details = await Store.GetThreadAsync(thread.Id, default);
        Assert.Equal(LifecycleState.Draft, details!.Thread.State);
        Assert.Equal(Stage.Discuss, details.Thread.Stage);
        Assert.Equal(150_000, details.Thread.BudgetCap);
        Assert.Equal(project.Id, details.Project.Id);
        Assert.Empty(details.Messages);
        Assert.Null(details.LatestRun);

        await Assert.ThrowsAsync<StoreNotFoundException>(() => AddThreadAsync(new Project
        {
            Id = Guid.NewGuid(), Name = "x", GitHubOwner = "x", GitHubRepository = "x", DefaultBranch = "main", VerificationProfileJson = "{}",
        }));
        Assert.Null(await Store.GetThreadAsync(Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task ListThreads_FiltersByProject()
    {
        var a = await AddProjectAsync("a");
        var b = await AddProjectAsync("b");
        await AddThreadAsync(a);
        await AddThreadAsync(b);
        await AddThreadAsync(b);

        Assert.Equal(3, (await Store.ListThreadsAsync(null, default)).Count);
        Assert.Equal(2, (await Store.ListThreadsAsync(b.Id, default)).Count);
    }

    // ---- Dispatch ----

    [SkippableFact]
    public async Task Dispatch_FromADraft_RecordsTheMessage_QueuesOneRun_AndEmitsEvents()
    {
        var project = await AddProjectAsync();
        var thread = await AddThreadAsync(project);

        var result = await Store.DispatchAsync(thread.Id, Admin, "msg-1", "Add CSV export.", T0, default);

        Assert.Equal(DispatchOutcome.Queued, result.Outcome);
        var details = (await Store.GetThreadAsync(thread.Id, default))!;
        Assert.Equal(LifecycleState.Queued, details.Thread.State);
        Assert.Equal(Stage.Implement, details.Thread.Stage);
        var message = Assert.Single(details.Messages);
        Assert.Equal((1L, "Add CSV export.", MessageAuthor.User), (message.Sequence, message.Text, message.Author));
        Assert.Equal(result.RunId, details.LatestRun!.Id);
        Assert.Equal((RunKind.Implement, RunStatus.Queued, RunEntry.Start), (details.LatestRun.Kind, details.LatestRun.Status, details.LatestRun.Entry));
        Assert.False(string.IsNullOrEmpty(details.LatestRun.PromptRevision));

        var events = await Store.ReadEventsAsync(thread.Id, 0, 100, default);
        // The thread's creation, then the delegation's message and state.
        Assert.Equal([EventTypes.StateChanged, EventTypes.MessageAdded, EventTypes.StateChanged], events.Select(e => e.Type));
    }

    /// <summary>Acceptance scenario 19: a double-click or a network retry creates one assignment.</summary>
    [SkippableFact]
    public async Task Dispatch_SameMessageIdTwice_CreatesOneRunAndOneMessage()
    {
        var project = await AddProjectAsync();
        var thread = await AddThreadAsync(project);

        var first = await Store.DispatchAsync(thread.Id, Admin, "msg-1", "Add CSV export.", T0, default);
        var second = await Store.DispatchAsync(thread.Id, Admin, "msg-1", "Add CSV export.", T0, default);

        Assert.Equal(DispatchOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.RunId, second.RunId);
        Assert.Single((await Store.GetThreadAsync(thread.Id, default))!.Messages);
        await using var db = Contexts.CreateDbContext();
        Assert.Equal(1, await db.Runs.CountAsync());
    }

    [SkippableFact]
    public async Task Dispatch_WhileARunIsActive_IsAFollowUp_NotASecondRun()
    {
        var running = await RunningAsync();

        var result = await Store.DispatchAsync(running.Thread.Id, Admin, "msg-2", "Also handle empty results.", T0, default);

        Assert.Equal(DispatchOutcome.FollowUp, result.Outcome);
        Assert.Equal(running.Run.Id, result.RunId);
        Assert.Equal(2, (await Store.GetThreadAsync(running.Thread.Id, default))!.Messages.Count);
        await using var db = Contexts.CreateDbContext();
        Assert.Equal(1, await db.Runs.CountAsync());
    }

    /// <summary>§5: budget exhaustion cannot be bypassed by posting @factory again.</summary>
    [SkippableTheory]
    [InlineData(LifecycleTrigger.ExhaustBudget, "budget")]
    [InlineData(LifecycleTrigger.RequestDecision, "decision")]
    [InlineData(LifecycleTrigger.Block, "blocked")]
    [InlineData(LifecycleTrigger.Pause, "paused")]
    public async Task Dispatch_WhileStopped_IsRejectedWithTheReason_AndRecordsNothing(LifecycleTrigger stoppedBy, string expected)
    {
        var running = await RunningAsync();
        await Store.StopRunAsync(Stop(running.Run.Id, stoppedBy, StopReason.TurnFaulted), T0, default);
        var before = (await Store.GetThreadAsync(running.Thread.Id, default))!.Messages.Count;

        var result = await Store.DispatchAsync(running.Thread.Id, Admin, "msg-2", "Try again.", T0, default);

        Assert.Equal(DispatchOutcome.Rejected, result.Outcome);
        Assert.Contains(expected, result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, (await Store.GetThreadAsync(running.Thread.Id, default))!.Messages.Count);
    }

    [SkippableFact]
    public async Task Dispatch_AfterHandoff_StartsAReworkRunOnTheSameThread()
    {
        var running = await RunningAsync();
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff) with { Stage = Stage.Handoff }, T0, default);

        var result = await Store.DispatchAsync(running.Thread.Id, Admin, "msg-2", "CSV values containing commas are wrong.", T0, default);

        Assert.Equal(DispatchOutcome.Queued, result.Outcome);
        Assert.NotEqual(running.Run.Id, result.RunId);
        var rework = await Store.GetRunAsync(result.RunId!.Value, default);
        Assert.Equal((RunKind.Rework, RunStatus.Queued), (rework!.Kind, rework.Status));
        Assert.Equal("CSV values containing commas are wrong.", rework.Request);
        Assert.Equal(LifecycleState.Queued, (await ThreadAsync(running.Thread.Id)).State);
    }

    // ---- A change request's budget top-up (decided 2026-10-03) ----

    private async Task<ClaimedRun> HandedOffAsync(long? cap)
    {
        var running = await RunningAsync(cap: cap);
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff) with { Stage = Stage.Handoff }, T0, default);
        return running;
    }

    [SkippableFact]
    public async Task ANewThread_RemembersTheCapItWasCreatedWith()
    {
        var running = await RunningAsync(cap: 600_000);

        Assert.Equal(600_000, (await ThreadAsync(running.Thread.Id)).InitialBudgetCap);
    }

    [SkippableFact]
    public async Task AChangeRequest_AddsAShareOfTheOriginalCap_AndTheThreadSaysSo()
    {
        var handedOff = await HandedOffAsync(cap: 600_000);

        var result = await Store.DispatchAsync(handedOff.Thread.Id, Admin, "msg-2", "Handle a failed compaction.", T0, default, reworkTopUpShare: 0.5);

        Assert.Equal(DispatchOutcome.Queued, result.Outcome);
        var details = (await Store.GetThreadAsync(handedOff.Thread.Id, default))!;
        Assert.Equal((900_000L, 600_000L), (details.Thread.BudgetCap!.Value, details.Thread.InitialBudgetCap!.Value));
        var note = details.Messages.Last();
        Assert.Equal((MessageAuthor.Factory, MessageKind.Status), (note.Author, note.Kind));
        Assert.Contains("adds 300,000 tokens to the budget", note.Text);
        Assert.Contains("(50% of the original 600,000)", note.Text);
        Assert.Contains("The cap is now 900,000.", note.Text);
    }

    [SkippableFact]
    public async Task ACapRaisedByHand_DoesNotRaiseTheTopUp()
    {
        var handedOff = await HandedOffAsync(cap: 600_000);
        await Store.SetBudgetCapAsync(handedOff.Thread.Id, 1_000_000, Admin, T0, default);

        await Store.DispatchAsync(handedOff.Thread.Id, Admin, "msg-2", "Fix it.", T0, default, reworkTopUpShare: 0.5);

        Assert.Equal(1_300_000, (await ThreadAsync(handedOff.Thread.Id)).BudgetCap);
    }

    [SkippableFact]
    public async Task WithTopUpsOff_TheBudgetIsUnchanged()
    {
        var handedOff = await HandedOffAsync(cap: 600_000);

        await Store.DispatchAsync(handedOff.Thread.Id, Admin, "msg-2", "Fix it.", T0, default);

        Assert.Equal(600_000, (await ThreadAsync(handedOff.Thread.Id)).BudgetCap);
    }

    [SkippableFact]
    public async Task ATaskWithNoCap_StaysWithoutOne()
    {
        var handedOff = await HandedOffAsync(cap: null);

        await Store.DispatchAsync(handedOff.Thread.Id, Admin, "msg-2", "Fix it.", T0, default, reworkTopUpShare: 0.5);

        Assert.Null((await ThreadAsync(handedOff.Thread.Id)).BudgetCap);
        Assert.DoesNotContain((await Store.GetThreadAsync(handedOff.Thread.Id, default))!.Messages, m => m.Text.Contains("to the budget"));
    }

    [SkippableFact]
    public async Task AFirstDelegation_OrAFollowUp_GetsNoTopUp()
    {
        var running = await RunningAsync(cap: 600_000);

        await Store.DispatchAsync(running.Thread.Id, Admin, "msg-2", "Also handle empty results.", T0, default, reworkTopUpShare: 0.5);

        Assert.Equal(600_000, (await ThreadAsync(running.Thread.Id)).BudgetCap);
    }

    [SkippableFact]
    public async Task Dispatch_UnknownThread_Throws()
    {
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.DispatchAsync(Guid.NewGuid(), Admin, "m", "t", T0, default));
    }

    // ---- Claiming ----

    [SkippableFact]
    public async Task Claim_TakesTheLease_AndMovesTheThreadToRunning()
    {
        var (project, thread, runId) = await QueuedAsync();

        var claimed = await Store.ClaimNextRunAsync(slotCap: 1, T0.AddMinutes(1), default);

        Assert.Equal(runId, claimed!.Run.Id);
        Assert.Equal(project.Id, claimed.Project.Id);
        Assert.Equal(LifecycleState.Running, (await ThreadAsync(thread.Id)).State);
        Assert.Equal(RunStatus.Running, (await Store.GetRunAsync(runId, default))!.Status);
        await using var db = Contexts.CreateDbContext();
        var lease = await db.Leases.SingleAsync();
        Assert.Equal((thread.Id, runId), (lease.ThreadId, lease.RunId));
    }

    [SkippableFact]
    public async Task Claim_NothingQueued_ReturnsNull()
    {
        Assert.Null(await Store.ClaimNextRunAsync(1, T0, default));
    }

    /// <summary>A second queued run waits while the only slot is taken.</summary>
    [SkippableFact]
    public async Task Claim_AtTheSlotCap_ClaimsNothing()
    {
        await RunningAsync("a");
        await QueuedAsync("b");

        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 1, T0, default));
        Assert.NotNull(await Store.ClaimNextRunAsync(slotCap: 2, T0, default));
    }

    /// <summary>Acceptance scenario 3: two threads on the same repository never run together,
    /// and the waiting one says exactly what it waits for.</summary>
    [SkippableFact]
    public async Task Claim_SameRepositoryHeldByAnotherThread_Waits_AndSaysWhy()
    {
        var first = await RunningAsync();
        var second = await AddThreadAsync(first.Project, "Fix the footer");
        await Store.DispatchAsync(second.Id, Admin, "msg-b", "Fix the footer.", T0, default);

        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 5, T0, default));

        var waiting = await ThreadAsync(second.Id);
        Assert.Equal(LifecycleState.Queued, waiting.State);
        Assert.Equal("Waiting for salesapp — held by \"Add CSV export\" (running).", waiting.StateReason);
    }

    [SkippableFact]
    public async Task Claim_DifferentRepositories_RunInParallelUpToTheCap()
    {
        await RunningAsync("a");
        var (_, threadB, _) = await QueuedAsync("b");

        var claimed = await Store.ClaimNextRunAsync(slotCap: 5, T0, default);

        Assert.Equal(threadB.Id, claimed!.Thread.Id);
    }

    [SkippableFact]
    public async Task Claim_TakesTheOldestQueuedRunFirst()
    {
        var project = await AddProjectAsync("a");
        var other = await AddProjectAsync("b");
        var later = await AddThreadAsync(other, "later");
        var earlier = await AddThreadAsync(project, "earlier");
        await Store.DispatchAsync(earlier.Id, Admin, "m1", "first", T0, default);
        await Store.DispatchAsync(later.Id, Admin, "m2", "second", T0.AddMinutes(5), default);

        Assert.Equal(earlier.Id, (await Store.ClaimNextRunAsync(5, T0.AddMinutes(10), default))!.Thread.Id);
    }

    /// <summary>§6.3: a run stopped with uncommitted work keeps the repository; the thread that
    /// holds the lease is the one allowed back in.</summary>
    [SkippableFact]
    public async Task StoppedRun_KeepsItsLease_AndReclaimsItWhenResumed()
    {
        var first = await RunningAsync();
        await Store.StopRunAsync(Stop(first.Run.Id, LifecycleTrigger.Block, StopReason.TurnFaulted), T0, default);
        var second = await AddThreadAsync(first.Project, "Other task");
        await Store.DispatchAsync(second.Id, Admin, "msg-b", "Other.", T0, default);

        Assert.Null(await Store.ClaimNextRunAsync(5, T0, default)); // the blocked thread still holds it
        Assert.Contains("(blocked)", (await ThreadAsync(second.Id)).StateReason);

        await Store.ApplyUserActionAsync(first.Thread.Id, Admin, LifecycleTrigger.ResolveBlocker, T0, default);
        var reclaimed = await Store.ClaimNextRunAsync(5, T0, default);

        Assert.Equal(first.Run.Id, reclaimed!.Run.Id);
        Assert.Equal(RunEntry.Resume, reclaimed.Run.Entry);
    }

    /// <summary>Acceptance scenario 4: in clone mode a task awaiting human testing does not block
    /// a new task on the same repository.</summary>
    [SkippableFact]
    public async Task Handoff_ReleasesTheLease_SoAnotherThreadCanUseTheRepository()
    {
        var first = await RunningAsync();
        var second = await AddThreadAsync(first.Project, "Other task");
        await Store.DispatchAsync(second.Id, Admin, "msg-b", "Other.", T0, default);

        await Store.StopRunAsync(Stop(first.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff) with { Stage = Stage.Handoff }, T0, default);

        Assert.Equal(second.Id, (await Store.ClaimNextRunAsync(1, T0, default))!.Thread.Id);
    }

    // ---- Claiming with more than one slot (M2) ----

    [SkippableFact]
    public async Task Dispatch_QueuesTheRunAtTheTimeOfTheMessage()
    {
        var (_, _, runId) = await QueuedAsync();

        Assert.Equal(T0, (await Store.GetRunAsync(runId, default))!.QueuedAt);
    }

    /// <summary>With every slot busy the queue is still scanned, so each waiting thread says why,
    /// and saying it again changes nothing.</summary>
    [SkippableFact]
    public async Task Claim_AtTheSlotCap_TellsEveryQueuedThreadItWaitsForASlot()
    {
        await RunningAsync("a");
        var (_, b, _) = await QueuedAsync("b");
        var (_, c, _) = await QueuedAsync("c");

        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 1, T0, default));

        Assert.Equal("Waiting for a free slot (1 of 1 busy).", (await ThreadAsync(b.Id)).StateReason);
        Assert.Equal("Waiting for a free slot (1 of 1 busy).", (await ThreadAsync(c.Id)).StateReason);

        var revision = (await ThreadAsync(b.Id)).Revision;
        var events = (await Store.ReadEventsAsync(b.Id, 0, 100, default)).Count;
        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 1, T0.AddSeconds(5), default));
        Assert.Equal(revision, (await ThreadAsync(b.Id)).Revision);
        Assert.Equal(events, (await Store.ReadEventsAsync(b.Id, 0, 100, default)).Count);
    }

    /// <summary>The held repository is the more specific reason, so it wins over the busy slots.</summary>
    [SkippableFact]
    public async Task Claim_RepositoryHeld_AndSlotsBusy_SaysTheRepository()
    {
        var first = await RunningAsync();
        var second = await AddThreadAsync(first.Project, "Fix the footer");
        await Store.DispatchAsync(second.Id, Admin, "msg-b", "Fix the footer.", T0, default);

        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 1, T0, default));

        Assert.Equal("Waiting for salesapp — held by \"Add CSV export\" (running).", (await ThreadAsync(second.Id)).StateReason);
    }

    /// <summary>A reason that is no longer true is replaced when the claim next looks, and
    /// cleared when the thread starts.</summary>
    [SkippableFact]
    public async Task Claim_StaleReason_IsReplaced_ThenClearedWhenTheThreadStarts()
    {
        var holder = await RunningAsync("a");
        var other = await RunningAsync("b");
        var waiting = await AddThreadAsync(holder.Project, "Fix the footer");
        await Store.DispatchAsync(waiting.Id, Admin, "msg-w", "Fix the footer.", T0, default);
        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 5, T0, default));
        Assert.StartsWith("Waiting for a — held by", (await ThreadAsync(waiting.Id)).StateReason);

        // The repository is free now, but the one slot is still taken by the other task.
        await Store.StopRunAsync(Stop(holder.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff) with { Stage = Stage.Handoff }, T0, default);
        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 1, T0, default));
        Assert.Equal("Waiting for a free slot (1 of 1 busy).", (await ThreadAsync(waiting.Id)).StateReason);

        await Store.StopRunAsync(Stop(other.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff) with { Stage = Stage.Handoff }, T0, default);
        Assert.Equal(waiting.Id, (await Store.ClaimNextRunAsync(slotCap: 1, T0, default))!.Thread.Id);
        Assert.Null((await ThreadAsync(waiting.Id)).StateReason);
    }

    /// <summary>The host's registry, not the database, says which slots are busy: a run that has
    /// stopped but is still tearing down holds its slot.</summary>
    [SkippableFact]
    public async Task Claim_RunsTheHostStillHolds_FillTheSlots()
    {
        var (_, waiting, runId) = await QueuedAsync("b");
        var tearingDown = new HashSet<Guid> { Guid.NewGuid() };

        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 1, T0, default, tearingDown));
        Assert.Equal("Waiting for a free slot (1 of 1 busy).", (await ThreadAsync(waiting.Id)).StateReason);

        Assert.Equal(runId, (await Store.ClaimNextRunAsync(slotCap: 2, T0, default, tearingDown))!.Run.Id);
    }

    /// <summary>The resume race: a run paused and resumed before its executor finished tearing
    /// down must not be claimed a second time while that executor still holds it.</summary>
    [SkippableFact]
    public async Task Claim_ARunTheHostStillHolds_IsNotClaimedAgain_UntilItIsReleased()
    {
        var running = await RunningAsync();
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Pause, StopReason.PausedByUser), T0, default);
        await Store.ApplyUserActionAsync(running.Thread.Id, Admin, LifecycleTrigger.Resume, T0, default);
        var held = new HashSet<Guid> { running.Run.Id };

        Assert.Null(await Store.ClaimNextRunAsync(slotCap: 5, T0, default, held));
        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal(LifecycleState.Queued, thread.State);
        Assert.Null(thread.StateReason);

        Assert.Equal(running.Run.Id, (await Store.ClaimNextRunAsync(slotCap: 5, T0, default, new HashSet<Guid>()))!.Run.Id);
    }

    /// <summary>A resumed run waits behind work queued before it was resumed, whichever was
    /// created first.</summary>
    [SkippableTheory]
    [InlineData(LifecycleTrigger.Pause, LifecycleTrigger.Resume)]
    [InlineData(LifecycleTrigger.Block, LifecycleTrigger.ResolveBlocker)]
    [InlineData(LifecycleTrigger.ExhaustBudget, LifecycleTrigger.RaiseBudgetAndResume)]
    [InlineData(LifecycleTrigger.Interrupt, LifecycleTrigger.Recover)]
    public async Task Claim_IsFirstInFirstOut_ByWhenARunWasLastQueued(LifecycleTrigger stop, LifecycleTrigger resume)
    {
        var older = await RunningAsync("a");
        await Store.StopRunAsync(Stop(older.Run.Id, stop, StopReason.PausedByUser), T0.AddMinutes(1), default);
        var newer = await AddThreadAsync(await AddProjectAsync("b"), "newer");
        await Store.DispatchAsync(newer.Id, Admin, "m-newer", "newer", T0.AddMinutes(5), default);

        await Store.ApplyUserActionAsync(older.Thread.Id, Admin, resume, T0.AddMinutes(10), default);

        Assert.Equal(T0.AddMinutes(10), (await Store.GetRunAsync(older.Run.Id, default))!.QueuedAt);
        Assert.Equal(newer.Id, (await Store.ClaimNextRunAsync(slotCap: 5, T0.AddMinutes(11), default))!.Thread.Id);
        Assert.Equal(older.Run.Id, (await Store.ClaimNextRunAsync(slotCap: 5, T0.AddMinutes(11), default))!.Run.Id);
    }

    [SkippableFact]
    public async Task Claim_AnAnsweredDecision_JoinsTheBackOfTheQueue()
    {
        var older = await RunningAsync("a");
        var decision = await Store.OpenDecisionAsync(older.Run.Id, new DecisionSubmission("Q?", "w", ["a", "b"]), T0, default);
        await Store.StopRunAsync(Stop(older.Run.Id, LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded), T0, default);
        var newer = await AddThreadAsync(await AddProjectAsync("b"), "newer");
        await Store.DispatchAsync(newer.Id, Admin, "m-newer", "newer", T0.AddMinutes(5), default);

        await Store.AnswerDecisionAsync(decision.Id, Admin, "a", T0.AddMinutes(10), default);

        Assert.Equal(T0.AddMinutes(10), (await Store.GetRunAsync(older.Run.Id, default))!.QueuedAt);
        Assert.Equal(newer.Id, (await Store.ClaimNextRunAsync(slotCap: 5, T0.AddMinutes(11), default))!.Thread.Id);
    }

    // ---- Stopping, decisions and user actions ----

    [SkippableFact]
    public async Task StopRun_Handoff_FinishesTheRun_AndPostsTheHandoffMessage()
    {
        var running = await RunningAsync();

        await Store.StopRunAsync(
            Stop(running.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff, "Ready for human testing.") with
            {
                Stage = Stage.Handoff, MessageKind = MessageKind.Handoff, MessagePayloadJson = """{"commit":"1c9e2b4"}""", StateJson = """{"kind":"Implement"}""",
            },
            T0.AddMinutes(9), default);

        var details = (await Store.GetThreadAsync(running.Thread.Id, default))!;
        Assert.Equal((LifecycleState.AwaitingHumanTesting, Stage.Handoff), (details.Thread.State, details.Thread.Stage));
        Assert.Null(details.Thread.StateReason);
        Assert.Equal((RunStatus.Finished, StopReason.HandedOff), (details.LatestRun!.Status, details.LatestRun.StopReason));
        Assert.Equal(T0.AddMinutes(9), details.LatestRun.EndedAt);
        var handoff = details.Messages[^1];
        Assert.Equal((MessageAuthor.Factory, MessageKind.Handoff, "Ready for human testing."), (handoff.Author, handoff.Kind, handoff.Text));
        Assert.Contains("1c9e2b4", handoff.PayloadJson);
    }

    [SkippableFact]
    public async Task StopRun_Block_SuspendsTheRun_KeepsItsState_AndGivesTheReason()
    {
        var running = await RunningAsync();

        await Store.StopRunAsync(
            Stop(running.Run.Id, LifecycleTrigger.Block, StopReason.RepairCyclesExhausted, "Still failing after 2 repair cycles.") with
            {
                Stage = Stage.Verify, StateJson = """{"kind":"Implement","repairCyclesUsed":2}""",
            },
            T0, default);

        var details = (await Store.GetThreadAsync(running.Thread.Id, default))!;
        Assert.Equal(LifecycleState.Blocked, details.Thread.State);
        Assert.Equal("Still failing after 2 repair cycles.", details.Thread.StateReason);
        Assert.Equal((RunStatus.Suspended, RunEntry.Resume), (details.LatestRun!.Status, details.LatestRun.Entry));
        Assert.Contains("repairCyclesUsed", details.LatestRun.StateJson);
    }

    [SkippableFact]
    public async Task StopRun_TransitionNotLegalFromTheThreadsState_IsAConflict()
    {
        var (_, _, runId) = await QueuedAsync(); // queued, never claimed

        await Assert.ThrowsAsync<StoreConflictException>(
            () => Store.StopRunAsync(Stop(runId, LifecycleTrigger.Handoff, StopReason.HandedOff), T0, default));
    }

    [SkippableFact]
    public async Task Decision_Opened_ThenAnswered_RequeuesTheSameRunWithTheAnswer()
    {
        var running = await RunningAsync();
        var decision = await Store.OpenDecisionAsync(
            running.Run.Id, new DecisionSubmission("All rows or the current page?", "The request does not say.", ["All rows", "Current page"], "All rows"), T0, default);
        await Store.StopRunAsync(
            Stop(running.Run.Id, LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded, decision.Question) with
            {
                MessageKind = MessageKind.Decision, DecisionId = decision.Id,
            },
            T0, default);
        Assert.Equal(LifecycleState.AwaitingDecision, (await ThreadAsync(running.Thread.Id)).State);

        await Store.AnswerDecisionAsync(decision.Id, Admin, "All filtered rows.", T0.AddMinutes(3), default);

        var details = (await Store.GetThreadAsync(running.Thread.Id, default))!;
        Assert.Equal(LifecycleState.Queued, details.Thread.State);
        Assert.Equal((RunStatus.Queued, RunEntry.DecisionAnswered, "All filtered rows."), (details.LatestRun!.Status, details.LatestRun.Entry, details.LatestRun.EntryAnswer));
        var stored = Assert.Single(details.Decisions);
        Assert.Equal((DecisionStatus.Answered, "All filtered rows.", (Guid?)Admin), (stored.Status, stored.Answer, stored.AnsweredBy));
        Assert.Contains("All rows", stored.OptionsJson);
        Assert.Equal(MessageKind.DecisionAnswer, details.Messages[^1].Kind);
        Assert.Equal(decision.Id, details.Messages[^1].DecisionId);

        Assert.Equal(running.Run.Id, (await Store.ClaimNextRunAsync(1, T0, default))!.Run.Id);
    }

    [SkippableFact]
    public async Task Decision_AnsweredTwice_IsAConflict()
    {
        var running = await RunningAsync();
        var decision = await Store.OpenDecisionAsync(running.Run.Id, new DecisionSubmission("Q?", "w", ["a", "b"]), T0, default);
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded), T0, default);
        await Store.AnswerDecisionAsync(decision.Id, Admin, "a", T0, default);

        await Assert.ThrowsAsync<StoreConflictException>(() => Store.AnswerDecisionAsync(decision.Id, Admin, "b", T0, default));
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.AnswerDecisionAsync(Guid.NewGuid(), Admin, "b", T0, default));
    }

    /// <summary>Acceptance scenario 15: Accept records approval and nothing else.</summary>
    [SkippableFact]
    public async Task Accept_AfterHandoff_MarksTheThreadAcceptedAndDone()
    {
        var running = await RunningAsync();
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff), T0, default);

        var thread = await Store.ApplyUserActionAsync(running.Thread.Id, Admin, LifecycleTrigger.Accept, T0, default);

        Assert.Equal((LifecycleState.Accepted, Stage.Done), (thread.State, thread.Stage));
    }

    [SkippableFact]
    public async Task Accept_BeforeHandoff_IsAConflict()
    {
        var (_, thread, _) = await QueuedAsync();

        await Assert.ThrowsAsync<StoreConflictException>(() => Store.ApplyUserActionAsync(thread.Id, Admin, LifecycleTrigger.Accept, T0, default));
    }

    [SkippableFact]
    public async Task Cancel_AWaitingTask_FinishesItsRun_AndReleasesTheRepository()
    {
        var running = await RunningAsync();
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Block, StopReason.TurnFaulted), T0, default);

        var thread = await Store.ApplyUserActionAsync(running.Thread.Id, Admin, LifecycleTrigger.Cancel, T0.AddMinutes(1), default);

        Assert.Equal(LifecycleState.Cancelled, thread.State);
        var run = await Store.GetRunAsync(running.Run.Id, default);
        Assert.Equal((RunStatus.Finished, StopReason.Cancelled), (run!.Status, run.StopReason));
        await using var db = Contexts.CreateDbContext();
        Assert.Empty(await db.Leases.ToListAsync());
    }

    [SkippableFact]
    public async Task UserAction_OnARunningTask_IsLeftToTheCoordinator()
    {
        var running = await RunningAsync();

        var ex = await Assert.ThrowsAsync<StoreConflictException>(
            () => Store.ApplyUserActionAsync(running.Thread.Id, Admin, LifecycleTrigger.Cancel, T0, default));

        Assert.Contains("coordinator", ex.Message);
    }

    [SkippableFact]
    public async Task PauseAQueuedTask_ThenResume_PutsItBackInTheQueue_StillAtItsStart()
    {
        var (_, thread, runId) = await QueuedAsync();

        await Store.ApplyUserActionAsync(thread.Id, Admin, LifecycleTrigger.Pause, T0, default);
        Assert.Null(await Store.ClaimNextRunAsync(1, T0, default));

        await Store.ApplyUserActionAsync(thread.Id, Admin, LifecycleTrigger.Resume, T0, default);
        var claimed = await Store.ClaimNextRunAsync(1, T0, default);

        Assert.Equal(runId, claimed!.Run.Id);
        Assert.Equal(RunEntry.Start, claimed.Run.Entry);
    }

    [SkippableFact]
    public async Task UserAction_ThatIsNotAUsersToTake_IsRejected()
    {
        var (_, thread, _) = await QueuedAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Store.ApplyUserActionAsync(thread.Id, Admin, LifecycleTrigger.Claim, T0, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ApplyUserActionAsync(thread.Id, Admin, LifecycleTrigger.Handoff, T0, default));
    }

    // ---- Checkpoints and evidence ----

    /// <summary>The first real run failed here. PostgreSQL's jsonb does not keep key order, so
    /// what comes back is not byte-for-byte what was saved; the checkpoint must still be read.</summary>
    [SkippableFact]
    public async Task StopRun_TheCheckpointReadBackFromTheStore_StillDeserializes_WithItsResumePoint()
    {
        var running = await RunningAsync();
        var resumeAt = new StartTurnStep(Litos.SoftwareFactory.Contracts.TurnKind.Implement, BriefKind.Resume);
        var state = RunOrchestrator.NewRun(RunKind.Implement) with
        {
            Phase = RunPhase.Stopped,
            ResumePoint = resumeAt,
            LastStop = new StopStep(LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted, "Out of budget."),
        };

        await Store.StopRunAsync(
            Stop(running.Run.Id, LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted, "Out of budget.") with
            {
                Stage = Stage.Implement, StateJson = RunStateJson.Serialize(state),
            },
            T0, default);

        var stored = (await Store.GetRunAsync(running.Run.Id, default))!.StateJson!;
        var back = RunStateJson.Deserialize(stored);
        Assert.Equal(resumeAt, back.ResumePoint);
        Assert.Equal(StopReason.BudgetExhausted, back.LastStop!.Reason);
    }

    [SkippableFact]
    public async Task Checkpoint_SavesTheState_AndEmitsAnEventOnlyWhenTheStageChanges()
    {
        var running = await RunningAsync();
        var before = (await Store.ReadEventsAsync(running.Thread.Id, 0, 100, default)).Count;

        await Store.SaveCheckpointAsync(running.Run.Id, """{"phase":"Turn"}""", Stage.Implement, T0.AddMinutes(1), default);
        var sameStage = (await Store.ReadEventsAsync(running.Thread.Id, 0, 100, default)).Count;
        await Store.SaveCheckpointAsync(running.Run.Id, """{"phase":"Verify"}""", Stage.Verify, T0.AddMinutes(2), default);
        var newStage = (await Store.ReadEventsAsync(running.Thread.Id, 0, 100, default)).Count;

        Assert.Equal(before, sameStage);
        Assert.Equal(before + 1, newStage);
        var run = await Store.GetRunAsync(running.Run.Id, default);
        Assert.Contains("Verify", run!.StateJson);
        Assert.Equal(T0.AddMinutes(2), run.HeartbeatAt);
        Assert.Equal(Stage.Verify, (await ThreadAsync(running.Thread.Id)).Stage);
    }

    /// <summary>The working copy is recorded with each checkpoint and with the stop, so a resumed
    /// run can compare (§16). A step that could not take a snapshot keeps the last one.</summary>
    [SkippableFact]
    public async Task Checkpoint_AndStop_KeepTheLatestWorkspaceSnapshot()
    {
        var running = await RunningAsync();
        const string First = """{"branch":"factory/x","head":"aaa","files":{"src/A.cs":"11"},"truncated":false}""";
        const string AtStop = """{"branch":"factory/x","head":"bbb","files":{},"truncated":false}""";

        await Store.SaveCheckpointAsync(running.Run.Id, """{"phase":"Turn"}""", Stage.Implement, T0, default, First);
        await Store.SaveCheckpointAsync(running.Run.Id, """{"phase":"Verify"}""", Stage.Verify, T0, default);
        Assert.Contains("src/A.cs", (await Store.GetRunAsync(running.Run.Id, default))!.WorkspaceSnapshotJson);

        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Pause, StopReason.PausedByUser) with { WorkspaceSnapshotJson = AtStop }, T0, default);
        var stored = (await Store.GetRunAsync(running.Run.Id, default))!.WorkspaceSnapshotJson!;
        Assert.Contains("bbb", stored);
        Assert.DoesNotContain("src/A.cs", stored);
    }

    [SkippableFact]
    public async Task RunDetails_WorkerCommitsAndBranch_AreStored()
    {
        var running = await RunningAsync();

        await Store.SetRunWorkerAsync(running.Run.Id, 4242, T0, default);
        await Store.SetRunCommitsAsync(running.Run.Id, "base123", null, "review-session", default);
        await Store.SetRunCommitsAsync(running.Run.Id, null, "head456", null, default);
        await Store.SetThreadBranchAsync(running.Thread.Id, "factory/7f3a-csv-export", "base123", default);

        var run = await Store.GetRunAsync(running.Run.Id, default);
        Assert.Equal((4242, T0, "base123", "head456", "review-session"), (run!.WorkerProcessId, run.WorkerStartTime, run.BaselineCommit, run.HeadCommit, run.ReviewSessionId));
        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal(("factory/7f3a-csv-export", "base123"), (thread.Branch, thread.BaseCommit));
        Assert.Equal(running.Run.Id, Assert.Single(await Store.ListRunningAsync(default)).Run.Id);
    }

    [SkippableFact]
    public async Task Baseline_IsFoundByProjectCommitAndProfileRevision()
    {
        var project = await AddProjectAsync();
        await Store.SaveVerificationAsync(new VerificationRecord
        {
            ProjectId = project.Id, Kind = VerificationKind.Baseline, Commit = "abc", ProfileRevision = 1,
            Build = BuildStatus.Passed, UnitTests = UnitTestStatus.Passed, PassedCount = 40, OutcomeJson = """{"tests":[]}""", CreatedAt = T0,
        }, default);

        Assert.Equal(40, (await Store.FindBaselineAsync(project.Id, "abc", 1, default))!.PassedCount);
        Assert.Null(await Store.FindBaselineAsync(project.Id, "abc", 2, default));       // the profile changed
        Assert.Null(await Store.FindBaselineAsync(project.Id, "other", 1, default));     // a different commit
        Assert.Null(await Store.FindBaselineAsync(Guid.NewGuid(), "abc", 1, default));   // a different project
    }

    /// <summary>The next call asks for the upstream that served the last one: the most recent
    /// settled call a router reported, never the provider's own name.</summary>
    [SkippableFact]
    public async Task LastServedBy_IsTheMostRecentRoutedUpstream_AndIgnoresTheProvidersOwnName()
    {
        var running = await RunningAsync(cap: 100_000);
        Assert.Null(await Store.LastServedByAsync(running.Thread.Id, default));

        await Store.ReserveAsync(Reserve(running, "k1"), Policy, T0, default);
        await Store.SettleAsync("k1", new UsageInfo(1_000, 100) { ServedBy = "AtlasCloud" }, 1_100, T0, default);
        await Store.ReserveAsync(Reserve(running, "k2"), Policy, T0, default);
        await Store.SettleAsync("k2", new UsageInfo(1_000, 100) { ServedBy = "Together" }, 1_100, T0.AddSeconds(5), default);
        await Store.ReserveAsync(Reserve(running, "k3"), Policy, T0, default);
        await Store.SettleAsync("k3", new UsageInfo(1_000, 100), 1_100, T0.AddSeconds(9), default);   // recorded as the provider itself

        Assert.Equal("Together", await Store.LastServedByAsync(running.Thread.Id, default));
    }

    /// <summary>Review yield is measured from a person's verdict on each finding.</summary>
    [SkippableFact]
    public async Task AFindingsVerdict_IsRecordedWithWhoAndWhen_AndCanBeCleared()
    {
        var running = await RunningAsync();
        await Store.SaveFindingsAsync(running.Run.Id, [new ReviewFinding(FindingSeverity.Minor, "src/Orders.cs", 7, "Resets the user's choice.")], default);
        var finding = Assert.Single(await Store.ListFindingsAsync(running.Thread.Id, default));
        var before = (await ThreadAsync(running.Thread.Id)).Revision;

        var thread = await Store.SetFindingVerdictAsync(finding.Id, FindingVerdict.Real, Admin, T0, default);

        var judged = Assert.Single(await Store.ListFindingsAsync(running.Thread.Id, default));
        Assert.Equal((FindingVerdict.Real, Admin, T0), (judged.Verdict, judged.VerdictBy, judged.VerdictAt));
        Assert.True(thread.Revision > before, "A verdict is an event on the thread, so open pages refresh.");

        await Store.SetFindingVerdictAsync(finding.Id, null, Admin, T0, default);

        var cleared = Assert.Single(await Store.ListFindingsAsync(running.Thread.Id, default));
        Assert.Equal((null, null, null), (cleared.Verdict, cleared.VerdictBy, cleared.VerdictAt));
    }

    [SkippableFact]
    public async Task AVerdictOnAFindingThatDoesNotExist_IsNotFound()
    {
        await RunningAsync();

        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.SetFindingVerdictAsync(Guid.NewGuid(), FindingVerdict.Wrong, Admin, T0, default));
    }

    [SkippableFact]
    public async Task FindingsVerificationAndHandoff_AppearInTheThreadDetails()
    {
        var running = await RunningAsync();
        await Store.SaveFindingsAsync(running.Run.Id,
        [
            new ReviewFinding(FindingSeverity.Blocking, "src/Orders.cs", 42, "Crashes on an empty list."),
            new ReviewFinding(FindingSeverity.Minor, "src/Orders.cs", null, "Leftover debug output."),
        ], default);
        await Store.SetFindingsStatusAsync(running.Run.Id, FindingSeverity.Blocking, FindingStatus.Fixed, default);
        await Store.SaveVerificationAsync(new VerificationRecord
        {
            ProjectId = running.Project.Id, RunId = running.Run.Id, Kind = VerificationKind.Run, Commit = "head", ProfileRevision = 1,
            Build = BuildStatus.Passed, UnitTests = UnitTestStatus.Passed, Coverage = CoverageStatus.Met, PassedCount = 18,
            ChangedLineCoveragePercent = 91, OutcomeJson = "{}", CreatedAt = T0,
        }, default);
        await Store.SaveHandoffAsync(new HandoffRecord
        {
            RunId = running.Run.Id, ThreadId = running.Thread.Id, Branch = "factory/x", CommitSha = "1c9e2b4",
            PullRequestNumber = 212, PullRequestUrl = "https://github.com/acme/salesapp/pull/212", EvidenceJson = "{}", CreatedAt = T0,
        }, default);

        var details = (await Store.GetThreadAsync(running.Thread.Id, default))!;

        Assert.Equal(2, details.Findings.Count);
        Assert.Equal(FindingStatus.Fixed, details.Findings.Single(f => f.Severity == FindingSeverity.Blocking).Status);
        Assert.Equal(FindingStatus.Open, details.Findings.Single(f => f.Severity == FindingSeverity.Minor).Status);
        Assert.Equal((18, 91.0), (details.LatestVerification!.PassedCount, details.LatestVerification.ChangedLineCoveragePercent!.Value));
        Assert.Equal("1c9e2b4", details.LatestHandoff!.CommitSha);
        Assert.Equal((212, "https://github.com/acme/salesapp/pull/212"), (details.Thread.PullRequestNumber, details.Thread.PullRequestUrl));
    }

    // ---- Budget ----

    private ReserveCommand Reserve(ClaimedRun run, string key, long estimate = 15_000) =>
        new(key, run.Thread.Id, run.Run.Id, Admin, "openrouter", "deepseek/deepseek-v4.1-flash", estimate, estimate);

    /// <summary>The blueprint's §9.2 example, through the store: refused before spending anything.</summary>
    [SkippableFact]
    public async Task Reserve_ThatDoesNotFitTheCap_IsRefused_AndChangesNothing()
    {
        var running = await RunningAsync(cap: 20_000);

        var result = await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default);

        var refused = Assert.IsType<Refused>(result.Decision);
        Assert.Equal((RefusalReason.TaskBudget, 20_500L, 20_000L), (refused.Reason, refused.Needed, refused.Remaining));
        Assert.Equal(0, (await ThreadAsync(running.Thread.Id)).TokensReserved);
        Assert.Empty(await Store.ListUsageAsync(running.Thread.Id, default));
    }

    [SkippableFact]
    public async Task Reserve_ThenSettle_ChargesActualUsage_AndReleasesTheRest()
    {
        var running = await RunningAsync(cap: 100_000);

        var admitted = Assert.IsType<Admitted>((await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default)).Decision);
        Assert.Equal(20_500, (await ThreadAsync(running.Thread.Id)).TokensReserved);

        var usage = new UsageInfo(200, 900, 3_000, 9_000, ReasoningTokens: 640);
        await Store.SettleAsync("key-1", usage, BudgetLedger.ChargeFor(usage, cachedInputWeight: 1), T0.AddSeconds(30), default);

        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((13_100L, 0L), (thread.TokensUsed, thread.TokensReserved));
        var entry = Assert.Single(await Store.ListUsageAsync(running.Thread.Id, default));
        Assert.Equal(UsageStatus.Settled, entry.Status);
        Assert.Equal((12_200L, 12_000L, 900L, 640L, 13_100L, admitted.Reserved), (entry.ActualInput, entry.ActualCachedInput, entry.ActualOutput, entry.ActualReasoning, entry.Charged, entry.Reserved));
        Assert.Equal(T0.AddSeconds(30), entry.SettledAt);
        Assert.Equal(entry.Provider, entry.ServedBy);
    }

    /// <summary>R2 lost a third of its tokens to calls that missed the cache; which upstream
    /// served each call is what shows whether a router sent it elsewhere.</summary>
    [SkippableFact]
    public async Task Settle_RecordsWhoServedTheCall_WhenTheProviderSaysSo()
    {
        var running = await RunningAsync(cap: 100_000);
        await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default);

        await Store.SettleAsync("key-1", new UsageInfo(9_000, 500) { ServedBy = "DeepSeek" }, 9_500, T0, default);

        Assert.Equal("DeepSeek", Assert.Single(await Store.ListUsageAsync(running.Thread.Id, default)).ServedBy);
    }

    /// <summary>A unique request key per call prevents double-charging on repeated callbacks.</summary>
    [SkippableFact]
    public async Task SameRequestKey_IsReservedOnce_AndSettledOnce()
    {
        var running = await RunningAsync(cap: 100_000);

        await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default);
        var again = await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default);
        await Store.SettleAsync("key-1", new UsageInfo(10_000, 1_000), 11_000, T0, default);
        await Store.SettleAsync("key-1", new UsageInfo(10_000, 1_000), 11_000, T0, default);

        Assert.True(again.AlreadyKnown);
        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((11_000L, 0L), (thread.TokensUsed, thread.TokensReserved));
        Assert.Single(await Store.ListUsageAsync(running.Thread.Id, default));
    }

    /// <summary>Acceptance scenario 8: a call with unknown usage cannot silently refund the allowance.</summary>
    [SkippableFact]
    public async Task UnknownUsage_KeepsTheReservationCharged_UntilItIsReconciled()
    {
        var running = await RunningAsync(cap: 30_000);
        await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default);

        await Store.MarkUsageUnknownAsync("key-1", default);

        Assert.Equal(20_500, (await ThreadAsync(running.Thread.Id)).TokensReserved);
        Assert.Equal(UsageStatus.Unknown, Assert.Single(await Store.ListUsageAsync(running.Thread.Id, default)).Status);
        Assert.IsType<Refused>((await Store.ReserveAsync(Reserve(running, "key-2"), Policy, T0, default)).Decision);

        // Reconciled later with what the provider finally reported.
        await Store.SettleAsync("key-1", new UsageInfo(9_000, 500), 9_500, T0, default);
        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((9_500L, 0L), (thread.TokensUsed, thread.TokensReserved));
    }

    /// <summary>What each call was for, so a task's cost can be split into implementation and review.</summary>
    [SkippableFact]
    public async Task Reserve_RecordsThePhaseOfTheCall()
    {
        var running = await RunningAsync(cap: 900_000);

        await Store.ReserveAsync(Reserve(running, "implement") with { Phase = "Implement" }, Policy, T0, default);
        await Store.ReserveAsync(Reserve(running, "review") with { Phase = "LightReview" }, Policy, T0.AddSeconds(1), default);
        await Store.ReserveAsync(Reserve(running, "outside"), Policy, T0.AddSeconds(2), default);

        Assert.Equal(["Implement", "LightReview", null], (await Store.ListUsageAsync(running.Thread.Id, default)).Select(u => u.Phase));
    }

    // ---- A call is reserved for about what it will cost ----

    private static readonly BudgetPolicy Real = new() { OutputAllowanceTokens = 32_768, MinimumOutputTokens = 4_096, Margin = 0.10 };

    [SkippableFact]
    public async Task Reserve_InputExpectedFromTheCache_IsHeldAtTheCachedWeight()
    {
        var running = await RunningAsync(cap: 300_000);
        var command = Reserve(running, "key-1", estimate: 23_500) with { ExpectedCachedInput = 22_800 };

        var admitted = Assert.IsType<Admitted>((await Store.ReserveAsync(command, Real, T0, default)).Decision);

        // 700 new + 2,280 for the cached part, plus 10%, plus the output allowance.
        Assert.Equal(3_278 + 32_768, admitted.Reserved);
        Assert.Equal(admitted.Reserved, (await ThreadAsync(running.Thread.Id)).TokensReserved);
        // The ledger still records the whole estimate: that is what the estimator is judged on.
        Assert.Equal(23_500, Assert.Single(await Store.ListUsageAsync(running.Thread.Id, default)).EstimatedInput);
    }

    [SkippableFact]
    public async Task Reserve_NearTheCap_IsAdmittedWithItsOutputLimitedToWhatRemains_AndNeverHoldsMoreThanIsLeft()
    {
        var running = await RunningAsync(cap: 30_000);

        var admitted = Assert.IsType<Admitted>((await Store.ReserveAsync(Reserve(running, "key-1", estimate: 10_000), Real, T0, default)).Decision);

        Assert.Equal(19_000, admitted.MaxOutputTokens); // 30,000 less 11,000 for the input
        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal(30_000, thread.TokensReserved);
        // Nothing is left for a second call while this one is in flight.
        Assert.IsType<Refused>((await Store.ReserveAsync(Reserve(running, "key-2", estimate: 10), Real, T0, default)).Decision);
    }

    // ---- Reconciling calls whose usage will never be reported ----

    [SkippableFact]
    public async Task Reconcile_ForARun_ChargesItsUnknownCallsTheirInputEstimate_AndReleasesTheRest()
    {
        var running = await RunningAsync(cap: 300_000);
        await Store.ReserveAsync(Reserve(running, "cut-off", estimate: 23_900), Real, T0, default);
        await Store.MarkUsageUnknownAsync("cut-off", default);
        Assert.Equal(26_290 + 32_768, (await ThreadAsync(running.Thread.Id)).TokensReserved);
        var events = (await Store.ReadEventsAsync(running.Thread.Id, 0, 500, default)).Count;

        var reconciled = await Store.ReconcileUsageAsync(running.Run.Id, T0.AddMinutes(5), default);

        Assert.Equal(1, reconciled);
        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((23_900L, 0L), (thread.TokensUsed, thread.TokensReserved));
        var entry = Assert.Single(await Store.ListUsageAsync(running.Thread.Id, default));
        Assert.Equal((UsageStatus.Estimated, 23_900L, T0.AddMinutes(5)), (entry.Status, entry.Charged, entry.SettledAt!.Value));
        // The provider reported nothing, and nothing is recorded as if it had.
        Assert.Equal((0L, 0L), (entry.ActualInput, entry.ActualOutput));
        // The budget figures changed, so clients are told.
        var after = await Store.ReadEventsAsync(running.Thread.Id, 0, 500, default);
        Assert.Equal(events + 1, after.Count);
        Assert.Equal(EventTypes.UsageChanged, after[^1].Type);
    }

    /// <summary>A call still in flight for a run must not be touched when the run's dead calls
    /// are reconciled: its usage may yet be reported.</summary>
    [SkippableFact]
    public async Task Reconcile_ForARun_LeavesReservedAndSettledCallsAlone_AndOtherRunsToo()
    {
        var running = await RunningAsync(cap: 900_000);
        var other = await RunningAsync("other-repo", cap: 900_000);
        await Store.ReserveAsync(Reserve(running, "in-flight", estimate: 10_000), Real, T0, default);
        await Store.ReserveAsync(Reserve(running, "settled", estimate: 10_000), Real, T0, default);
        await Store.SettleAsync("settled", new UsageInfo(9_000, 500), 9_500, T0, default);
        await Store.ReserveAsync(Reserve(other, "other-unknown", estimate: 10_000), Real, T0, default);
        await Store.MarkUsageUnknownAsync("other-unknown", default);

        Assert.Equal(0, await Store.ReconcileUsageAsync(running.Run.Id, T0, default));

        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((9_500L, 11_000L + 32_768L), (thread.TokensUsed, thread.TokensReserved));
        Assert.Equal(UsageStatus.Unknown, Assert.Single(await Store.ListUsageAsync(other.Thread.Id, default)).Status);
    }

    /// <summary>For a run the liveness sweep found with no executor: nothing can report its
    /// calls any more, so those still Reserved are charged their estimate too. Other runs'
    /// calls, which may still be in flight, are left alone.</summary>
    [SkippableFact]
    public async Task Reconcile_ForARunWithNoExecutor_AlsoTakesItsReservedCalls_ButNoOtherRunsCalls()
    {
        var orphan = await RunningAsync(cap: 900_000);
        var live = await RunningAsync("other-repo", cap: 900_000);
        await Store.ReserveAsync(Reserve(orphan, "in-flight", estimate: 10_000), Real, T0, default);
        await Store.ReserveAsync(Reserve(orphan, "unknown", estimate: 5_000), Real, T0, default);
        await Store.MarkUsageUnknownAsync("unknown", default);
        await Store.ReserveAsync(Reserve(orphan, "settled", estimate: 10_000), Real, T0, default);
        await Store.SettleAsync("settled", new UsageInfo(9_000, 500), 9_500, T0, default);
        await Store.ReserveAsync(Reserve(live, "live-in-flight", estimate: 7_000), Real, T0, default);

        Assert.Equal(2, await Store.ReconcileUsageAsync(orphan.Run.Id, T0, default, includeInFlight: true));

        var thread = await ThreadAsync(orphan.Thread.Id);
        Assert.Equal((9_500L + 10_000L + 5_000L, 0L), (thread.TokensUsed, thread.TokensReserved));
        Assert.Equal(UsageStatus.Reserved, Assert.Single(await Store.ListUsageAsync(live.Thread.Id, default)).Status);
    }

    /// <summary>What a starting host does. The first real run left a call Reserved for ever when
    /// its host was stopped mid-call: 52,577 tokens of the task's budget that nothing would
    /// ever release.</summary>
    [SkippableFact]
    public async Task Reconcile_AtStartup_AlsoTakesCallsAPreviousHostLeftReserved_OnEveryThread()
    {
        var first = await RunningAsync(cap: 900_000);
        var second = await RunningAsync("other-repo", cap: 900_000);
        await Store.ReserveAsync(Reserve(first, "left-reserved", estimate: 23_900), Real, T0, default);
        await Store.ReserveAsync(Reserve(first, "left-unknown", estimate: 5_000), Real, T0, default);
        await Store.MarkUsageUnknownAsync("left-unknown", default);
        await Store.ReserveAsync(Reserve(first, "fine", estimate: 10_000), Real, T0, default);
        await Store.SettleAsync("fine", new UsageInfo(9_000, 500), 9_500, T0, default);
        await Store.ReserveAsync(Reserve(second, "elsewhere", estimate: 7_000), Real, T0, default);

        Assert.Equal(3, await Store.ReconcileUsageAsync(runId: null, T0.AddHours(1), default));

        var one = await ThreadAsync(first.Thread.Id);
        Assert.Equal((9_500L + 23_900L + 5_000L, 0L), (one.TokensUsed, one.TokensReserved));
        var two = await ThreadAsync(second.Thread.Id);
        Assert.Equal((7_000L, 0L), (two.TokensUsed, two.TokensReserved));
        Assert.Equal(
            [UsageStatus.Settled, UsageStatus.Estimated, UsageStatus.Estimated],
            (await Store.ListUsageAsync(first.Thread.Id, default)).Select(u => u.Status).Order());
        // Doing it again finds nothing: a call is reconciled once.
        Assert.Equal(0, await Store.ReconcileUsageAsync(null, T0.AddHours(2), default));
        Assert.Equal(one.TokensUsed, (await ThreadAsync(first.Thread.Id)).TokensUsed);
    }

    /// <summary>A call reserved at the cached rate held less than its full input estimate; it
    /// is charged what was held, never more.</summary>
    [SkippableFact]
    public async Task Reconcile_NeverChargesMoreThanWasReserved()
    {
        var running = await RunningAsync(cap: 5_000);
        var command = Reserve(running, "cached", estimate: 23_500) with { ExpectedCachedInput = 22_800 };
        var admitted = Assert.IsType<Admitted>((await Store.ReserveAsync(command, Real with { MinimumOutputTokens = 1_000 }, T0, default)).Decision);
        Assert.Equal(5_000, admitted.Reserved);
        await Store.MarkUsageUnknownAsync("cached", default);

        await Store.ReconcileUsageAsync(running.Run.Id, T0, default);

        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((5_000L, 0L), (thread.TokensUsed, thread.TokensReserved));
    }

    [SkippableFact]
    public async Task Reconcile_WithNothingToDo_ChangesNothing()
    {
        var running = await RunningAsync(cap: 100_000);
        var revision = (await ThreadAsync(running.Thread.Id)).Revision;

        Assert.Equal(0, await Store.ReconcileUsageAsync(null, T0, default));
        Assert.Equal(0, await Store.ReconcileUsageAsync(Guid.NewGuid(), T0, default));

        Assert.Equal(revision, (await ThreadAsync(running.Thread.Id)).Revision);
    }

    // ---- Withdrawing a change request ----

    /// <summary>A task that has been handed off and then asked for changes: a queued rework run.</summary>
    private async Task<(ClaimedRun First, Guid ReworkRunId)> ReworkQueuedAsync()
    {
        var running = await RunningAsync();
        await Store.SetThreadBranchAsync(running.Thread.Id, "factory/1a2b-add-csv-export", "base000", default);
        await Store.SaveHandoffAsync(new HandoffRecord
        {
            RunId = running.Run.Id, ThreadId = running.Thread.Id, Branch = "factory/1a2b-add-csv-export", CommitSha = "1c9e2b4",
            PullRequestNumber = 12, PullRequestUrl = "https://github.com/acme/salesapp/pull/12", EvidenceJson = "{}", CreatedAt = T0,
        }, default);
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff, "Ready for human testing.") with { Stage = Stage.Handoff, MessageKind = MessageKind.Handoff }, T0, default);
        var rework = await Store.DispatchAsync(running.Thread.Id, Admin, Guid.NewGuid().ToString(), "How do I set up manual tests?", T0.AddMinutes(1), default);
        Assert.Equal(DispatchOutcome.Queued, rework.Outcome);
        return (running, rework.RunId!.Value);
    }

    private async Task<int> LeasesAsync(Guid threadId)
    {
        await using var db = await Contexts.CreateDbContextAsync();
        return await db.Leases.CountAsync(l => l.ThreadId == threadId);
    }

    /// <summary>The first real run's dead end: a question sent with @factory became a rework run,
    /// and there was no way back to the handoff it had interrupted.</summary>
    [SkippableFact]
    public async Task Withdraw_AQueuedChangeRequest_EndsItsRun_AndReturnsTheTaskToItsHandoff()
    {
        var (first, reworkRunId) = await ReworkQueuedAsync();

        var result = await Store.WithdrawChangesAsync(first.Thread.Id, Admin, T0.AddMinutes(2), default);

        Assert.Equal((LifecycleState.AwaitingHumanTesting, Stage.Handoff), (result.Thread.State, result.Thread.Stage));
        Assert.Null(result.Thread.StateReason);
        Assert.Equal("salesapp", result.Project.GitHubRepository);
        // Never claimed, so it never held the repository or touched the working copy.
        Assert.False(result.HeldLease);

        var run = (await Store.GetRunAsync(reworkRunId, default))!;
        Assert.Equal((RunStatus.Finished, StopReason.Withdrawn, T0.AddMinutes(2)), (run.Status, run.StopReason!.Value, run.EndedAt!.Value));
        var details = (await Store.GetThreadAsync(first.Thread.Id, default))!;
        Assert.Equal("Change request withdrawn. The task is back at its last handoff.", details.Messages[^1].Text);
        Assert.Equal((MessageAuthor.User, MessageKind.Status), (details.Messages[^1].Author, details.Messages[^1].Kind));
        // The handoff it returns to is still the first run's.
        Assert.Equal("1c9e2b4", details.LatestHandoff!.CommitSha);
        // Nothing is left for the coordinator to pick up.
        Assert.Null(await Store.ClaimNextRunAsync(5, T0.AddMinutes(3), default));
    }

    [SkippableFact]
    public async Task Withdraw_ThenAccept_Works_AndSoDoesAskingForChangesAgain()
    {
        var (first, _) = await ReworkQueuedAsync();
        await Store.WithdrawChangesAsync(first.Thread.Id, Admin, T0.AddMinutes(2), default);

        var again = await Store.DispatchAsync(first.Thread.Id, Admin, Guid.NewGuid().ToString(), "Quote fields with commas.", T0.AddMinutes(3), default);
        Assert.Equal(DispatchOutcome.Queued, again.Outcome);
        await Store.WithdrawChangesAsync(first.Thread.Id, Admin, T0.AddMinutes(4), default);

        var accepted = await Store.ApplyUserActionAsync(first.Thread.Id, Admin, LifecycleTrigger.Accept, T0.AddMinutes(5), default);
        Assert.Equal(LifecycleState.Accepted, accepted.State);
    }

    /// <summary>A rework run that has started holds the repository and has edits in the working
    /// copy. The lease is kept so the caller can discard them before anyone else gets in.</summary>
    [SkippableFact]
    public async Task Withdraw_ASuspendedChangeRequest_KeepsTheLease_UntilItIsReleased()
    {
        var (first, reworkRunId) = await ReworkQueuedAsync();
        var claimed = (await Store.ClaimNextRunAsync(5, T0.AddMinutes(2), default))!;
        Assert.Equal(reworkRunId, claimed.Run.Id);
        await Store.StopRunAsync(Stop(reworkRunId, LifecycleTrigger.Block, StopReason.TurnFaulted, "A run in phase Turn cannot start.") with { Stage = Stage.Implement }, T0.AddMinutes(3), default);

        var result = await Store.WithdrawChangesAsync(first.Thread.Id, Admin, T0.AddMinutes(4), default);

        Assert.True(result.HeldLease);
        Assert.Equal(LifecycleState.AwaitingHumanTesting, result.Thread.State);
        Assert.Equal(1, await LeasesAsync(first.Thread.Id));

        await Store.ReleaseLeaseAsync(first.Thread.Id, default);
        Assert.Equal(0, await LeasesAsync(first.Thread.Id));
        // Releasing again, or for a thread with no lease, is harmless.
        await Store.ReleaseLeaseAsync(first.Thread.Id, default);
        await Store.ReleaseLeaseAsync(Guid.NewGuid(), default);
    }

    [SkippableTheory]
    [InlineData(LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted)]
    [InlineData(LifecycleTrigger.Pause, StopReason.PausedByUser)]
    [InlineData(LifecycleTrigger.Interrupt, StopReason.Interrupted)]
    [InlineData(LifecycleTrigger.Block, StopReason.NoProgress)]
    public async Task Withdraw_WorksFromEveryStoppedState(LifecycleTrigger stoppedBy, StopReason reason)
    {
        var (first, reworkRunId) = await ReworkQueuedAsync();
        await Store.ClaimNextRunAsync(5, T0.AddMinutes(2), default);
        await Store.StopRunAsync(Stop(reworkRunId, stoppedBy, reason) with { Stage = Stage.Implement }, T0.AddMinutes(3), default);

        var result = await Store.WithdrawChangesAsync(first.Thread.Id, Admin, T0.AddMinutes(4), default);

        Assert.Equal(LifecycleState.AwaitingHumanTesting, result.Thread.State);
        Assert.Equal(StopReason.Withdrawn, (await Store.GetRunAsync(reworkRunId, default))!.StopReason);
    }

    [SkippableFact]
    public async Task Withdraw_WhileWaitingForADecision_ClosesTheQuestion()
    {
        var (first, reworkRunId) = await ReworkQueuedAsync();
        await Store.ClaimNextRunAsync(5, T0.AddMinutes(2), default);
        var decision = await Store.OpenDecisionAsync(
            reworkRunId, new Litos.SoftwareFactory.Contracts.DecisionSubmission("Which tests?", "Not stated.", ["Unit", "Manual"]), T0.AddMinutes(3), default);
        await Store.StopRunAsync(
            Stop(reworkRunId, LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded, "Which tests?") with { Stage = Stage.Implement, DecisionId = decision.Id, MessageKind = MessageKind.Decision },
            T0.AddMinutes(3), default);

        await Store.WithdrawChangesAsync(first.Thread.Id, Admin, T0.AddMinutes(4), default);

        var closed = Assert.Single((await Store.GetThreadAsync(first.Thread.Id, default))!.Decisions);
        Assert.Equal(DecisionStatus.Answered, closed.Status);
        Assert.Equal("Not answered: the change request was withdrawn.", closed.Answer);
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.AnswerDecisionAsync(decision.Id, Admin, "Unit", T0.AddMinutes(5), default));
    }

    [SkippableFact]
    public async Task Withdraw_ARunningChangeRequest_IsRefused_UntilItIsPaused()
    {
        var (first, _) = await ReworkQueuedAsync();
        await Store.ClaimNextRunAsync(5, T0.AddMinutes(2), default);

        var refusal = await Assert.ThrowsAsync<StoreConflictException>(() => Store.WithdrawChangesAsync(first.Thread.Id, Admin, T0.AddMinutes(3), default));

        Assert.Contains("Pause the task, then withdraw it", refusal.Message);
        Assert.Equal(LifecycleState.Running, (await ThreadAsync(first.Thread.Id)).State);
    }

    /// <summary>The original work is not a change request: there is no handoff to go back to,
    /// and stopping it is what Cancel is for.</summary>
    [SkippableFact]
    public async Task Withdraw_TheOriginalRun_IsRefused()
    {
        var (_, thread, runId) = await QueuedAsync();

        var queued = await Assert.ThrowsAsync<StoreConflictException>(() => Store.WithdrawChangesAsync(thread.Id, Admin, T0, default));
        Assert.Contains("no change request to withdraw", queued.Message);

        await Store.ClaimNextRunAsync(5, T0, default);
        await Store.StopRunAsync(Stop(runId, LifecycleTrigger.Block, StopReason.TurnFaulted) with { Stage = Stage.Implement }, T0, default);
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.WithdrawChangesAsync(thread.Id, Admin, T0, default));
        Assert.Equal(LifecycleState.Blocked, (await ThreadAsync(thread.Id)).State);
        Assert.Equal(RunStatus.Suspended, (await Store.GetRunAsync(runId, default))!.Status);
    }

    [SkippableFact]
    public async Task Withdraw_WhenNothingWasRequested_OrTheTaskIsClosed_IsRefused()
    {
        var running = await RunningAsync();
        await Store.SaveHandoffAsync(new HandoffRecord { RunId = running.Run.Id, ThreadId = running.Thread.Id, Branch = "factory/x", EvidenceJson = "{}", CreatedAt = T0 }, default);
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Handoff, StopReason.HandedOff) with { Stage = Stage.Handoff }, T0, default);

        // Waiting for testing, with no change request made.
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.WithdrawChangesAsync(running.Thread.Id, Admin, T0, default));
        await Store.ApplyUserActionAsync(running.Thread.Id, Admin, LifecycleTrigger.Accept, T0, default);
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.WithdrawChangesAsync(running.Thread.Id, Admin, T0, default));
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.WithdrawChangesAsync(Guid.NewGuid(), Admin, T0, default));
    }

    [SkippableFact]
    public async Task ReleaseReservation_ReturnsItInFull_ForACallThatWasNeverSent()
    {
        var running = await RunningAsync(cap: 100_000);
        await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default);

        await Store.ReleaseReservationAsync("key-1", default);

        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((0L, 0L), (thread.TokensUsed, thread.TokensReserved));
    }

    /// <summary>Acceptance scenario 7: raising a cap preserves prior usage.</summary>
    [SkippableFact]
    public async Task RaisingTheCap_KeepsWhatWasUsed_AndLetsTheNextCallIn()
    {
        var running = await RunningAsync(cap: 30_000);
        await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default);
        await Store.SettleAsync("key-1", new UsageInfo(15_000, 3_000), 18_000, T0, default);
        Assert.IsType<Refused>((await Store.ReserveAsync(Reserve(running, "key-2"), Policy, T0, default)).Decision);

        var thread = await Store.SetBudgetCapAsync(running.Thread.Id, 100_000, Admin, T0, default);

        Assert.Equal((100_000L, 18_000L), (thread.BudgetCap!.Value, thread.TokensUsed));
        Assert.IsType<Admitted>((await Store.ReserveAsync(Reserve(running, "key-2"), Policy, T0, default)).Decision);
    }

    [SkippableFact]
    public async Task NoCap_AdmitsEverything()
    {
        var running = await RunningAsync(cap: null);

        Assert.IsType<Admitted>((await Store.ReserveAsync(Reserve(running, "key-1", estimate: 5_000_000), Policy, T0, default)).Decision);
    }

    /// <summary>Two calls for one task must not each see the full remainder. Run concurrently,
    /// exactly as many are admitted as fit.</summary>
    [SkippableFact]
    public async Task ConcurrentReservations_NeverAdmitMoreThanFits()
    {
        var running = await RunningAsync(cap: 50_000); // each call reserves 20,500: two fit, a third does not

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => Store.ReserveAsync(Reserve(running, $"key-{i}"), Policy, T0, default))));

        Assert.Equal(2, results.Count(r => r.Decision is Admitted));
        Assert.Equal(41_000, (await ThreadAsync(running.Thread.Id)).TokensReserved);
    }

    [SkippableFact]
    public async Task Settle_UnknownRequestKey_Throws()
    {
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.SettleAsync("no-such-key", new UsageInfo(1, 1), 2, T0, default));
    }

    // ---- Events ----

    /// <summary>§12: a reconnecting client replays everything after its last seen event.</summary>
    [SkippableFact]
    public async Task Events_HaveIncreasingSequences_AndCanBeReplayedFromAnyPoint()
    {
        var running = await RunningAsync();
        await Store.AddFactoryMessageAsync(running.Thread.Id, MessageKind.Status, "Inspecting authorization.", null, T0, default);
        await Store.AddFactoryMessageAsync(running.Thread.Id, MessageKind.Status, "Running unit tests.", null, T0, default);

        var all = await Store.ReadEventsAsync(running.Thread.Id, 0, 100, default);
        var sequences = all.Select(e => e.Sequence).ToList();
        Assert.Equal(sequences.Order(), sequences);
        Assert.Equal(sequences.Count, sequences.Distinct().Count());

        var replay = await Store.ReadEventsAsync(running.Thread.Id, all[^3].Sequence, 100, default);
        Assert.Equal(all.Skip(all.Count - 2).Select(e => e.Sequence), replay.Select(e => e.Sequence));
        Assert.Contains("Running unit tests.", replay[^1].PayloadJson);

        Assert.Single(await Store.ReadEventsAsync(running.Thread.Id, 0, 1, default));
        Assert.Empty(await Store.ReadEventsAsync(running.Thread.Id, all[^1].Sequence, 100, default));
    }

    [SkippableFact]
    public async Task Events_AreScopedToTheirThread()
    {
        var a = await RunningAsync("a");
        var b = await RunningAsync("b");
        await Store.AddFactoryMessageAsync(a.Thread.Id, MessageKind.Status, "only for a", null, T0, default);

        Assert.DoesNotContain(await Store.ReadEventsAsync(b.Thread.Id, 0, 100, default), e => e.PayloadJson.Contains("only for a"));
    }

    /// <summary>A board hears its projects' state and usage changes, and no messages.</summary>
    [SkippableFact]
    public async Task BoardEvents_AreTheStateAndUsageEventsOfTheGivenProjects()
    {
        var a = await RunningAsync("a");
        var b = await RunningAsync("b");
        await Store.AddFactoryMessageAsync(a.Thread.Id, MessageKind.Status, "a note", null, T0, default);
        await Store.SetBudgetCapAsync(a.Thread.Id, 400_000, Admin, T0, default);

        var onlyA = await Store.ReadBoardEventsAsync(new HashSet<Guid> { a.Project.Id }, 0, 100, default);
        var all = await Store.ReadBoardEventsAsync(null, 0, 100, default);

        Assert.All(onlyA, e => Assert.Equal(a.Project.Id, e.ProjectId));
        Assert.Contains(onlyA, e => e.Type == EventTypes.UsageChanged);
        Assert.DoesNotContain(all, e => e.Type == EventTypes.MessageAdded);
        Assert.Contains(all, e => e.ProjectId == b.Project.Id);
        Assert.Equal(all.Select(e => e.Sequence).Order(), all.Select(e => e.Sequence));
        Assert.Empty(await Store.ReadBoardEventsAsync(new HashSet<Guid>(), 0, 100, default));
        Assert.Empty(await Store.ReadBoardEventsAsync(null, all[^1].Sequence, 100, default));
    }

    /// <summary>A state event says whose move it is and what the thread is called, so a client
    /// showing the thread never derives the label or misses a rename.</summary>
    [SkippableFact]
    public async Task StateEvents_CarryTheTurnLabelTitleAndType()
    {
        var (_, thread, _) = await QueuedAsync();
        await Store.EditThreadAsync(thread.Id, "Export orders as CSV", "bug", Admin, T0, default);

        var last = JsonDocument.Parse((await Store.ReadEventsAsync(thread.Id, 0, 100, default))[^1].PayloadJson).RootElement;

        Assert.Equal("AwaitingAgent", last.GetProperty("turn").GetString());
        Assert.Equal("Export orders as CSV", last.GetProperty("title").GetString());
        Assert.Equal("bug", last.GetProperty("typeLabel").GetString());
    }

    [SkippableFact]
    public async Task ANewThread_IsAnnounced_SoBoardsSeeIt()
    {
        var project = await AddProjectAsync();
        var thread = await AddThreadAsync(project);

        var announced = Assert.Single(await Store.ReadBoardEventsAsync(null, 0, 100, default));

        Assert.Equal((thread.Id, EventTypes.StateChanged), (announced.ThreadId, announced.Type));
        Assert.Equal(announced.Sequence, await Store.LastEventSequenceAsync(default));
    }

    [SkippableFact]
    public async Task FindThread_ReturnsTheRowAlone_OrNull()
    {
        var project = await AddProjectAsync();
        var thread = await AddThreadAsync(project);

        Assert.Equal(thread.Title, (await Store.FindThreadAsync(thread.Id, default))!.Title);
        Assert.Null(await Store.FindThreadAsync(Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task MessageSequences_AreGaplessPerThread()
    {
        var running = await RunningAsync();
        for (var i = 0; i < 4; i++)
            await Store.AddFactoryMessageAsync(running.Thread.Id, MessageKind.Status, $"note {i}", null, T0, default);

        var messages = (await Store.GetThreadAsync(running.Thread.Id, default))!.Messages;

        Assert.Equal([1L, 2L, 3L, 4L, 5L], messages.Select(m => m.Sequence));
    }

    // ---- People and access (M2, m2-architecture.md §4) ----

    protected static readonly Guid Ben = Guid.Parse("22222222-2222-2222-2222-222222222222");
    protected static readonly Guid Erin = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private async Task<Invitation> InviteAsync(string userName = "erin", DateTimeOffset? expires = null, params Guid[] projects) =>
        await Store.AddInvitationAsync(new Invitation
        {
            UserName = userName,
            TokenHash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + new string('0', 32),
            ProjectIdsJson = JsonSerializer.Serialize(projects),
            ExpiresAt = expires ?? T0.AddDays(7),
            CreatedBy = Admin,
            CreatedAt = T0,
        }, default);

    /// <summary>The audit log, without the rows registering the test's projects wrote.</summary>
    private async Task<IReadOnlyList<AuditEvent>> AuditAsync(Guid? projectId = null) =>
        [.. (await Store.ListAuditAsync(projectId, 100, default)).Where(r => r.Action != AuditActions.ProjectRegister)];

    /// <summary>The project behind an id the API is given, for the membership check.</summary>
    [SkippableFact]
    public async Task FindProjectId_OfAThreadADecisionAndAFinding_IsTheirProject()
    {
        var running = await RunningAsync();
        var decision = await Store.OpenDecisionAsync(running.Run.Id, new DecisionSubmission("Q?", "w", ["a", "b"]), T0, default);
        await Store.SaveFindingsAsync(running.Run.Id, [new ReviewFinding(FindingSeverity.Minor, "src/A.cs", 1, "x")], default);
        var finding = Assert.Single(await Store.ListFindingsAsync(running.Thread.Id, default));

        Assert.Equal(running.Project.Id, await Store.FindProjectIdAsync(ProjectScoped.Thread, running.Thread.Id, default));
        Assert.Equal(running.Project.Id, await Store.FindProjectIdAsync(ProjectScoped.Decision, decision.Id, default));
        Assert.Equal(running.Project.Id, await Store.FindProjectIdAsync(ProjectScoped.Finding, finding.Id, default));
    }

    [SkippableTheory]
    [InlineData(ProjectScoped.Thread)]
    [InlineData(ProjectScoped.Decision)]
    [InlineData(ProjectScoped.Finding)]
    public async Task FindProjectId_OfSomethingThatDoesNotExist_IsNull(ProjectScoped kind)
    {
        await RunningAsync();

        Assert.Null(await Store.FindProjectIdAsync(kind, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task Project_ItsCreatorIsAMember()
    {
        var project = await AddProjectAsync();

        Assert.True(await Store.IsMemberAsync(project.Id, Admin, default));
        Assert.Equal([project.Id], await Store.ListMemberProjectIdsAsync(Admin, default));
    }

    [SkippableFact]
    public async Task AddMember_RecordsTheMembershipAndItsAuditRow_AndASecondAddChangesNothing()
    {
        var project = await AddProjectAsync();

        Assert.True(await Store.AddMemberAsync(project.Id, Ben, Admin, T0.AddMinutes(1), default));
        Assert.False(await Store.AddMemberAsync(project.Id, Ben, Admin, T0.AddMinutes(2), default));

        var member = Assert.Single(await Store.ListMembersAsync(project.Id, default), m => m.UserId == Ben);
        Assert.Equal((Admin, T0.AddMinutes(1)), (member.CreatedBy, member.CreatedAt));
        var row = Assert.Single(await AuditAsync());
        Assert.Equal((Admin, AuditActions.MemberAdd, AuditTargets.User, (Guid?)Ben, (Guid?)project.Id), (row.ActorId, row.Action, row.TargetType, row.TargetId, row.ProjectId));
    }

    [SkippableFact]
    public async Task AddMember_ToAProjectThatDoesNotExist_IsNotFound()
    {
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.AddMemberAsync(Guid.NewGuid(), Ben, Admin, T0, default));
        Assert.Empty(await AuditAsync());
    }

    [SkippableFact]
    public async Task RemoveMember_EndsTheMembershipAndRecordsIt_OnlyOnce()
    {
        var project = await AddProjectAsync();
        await Store.AddMemberAsync(project.Id, Ben, Admin, T0, default);

        Assert.True(await Store.RemoveMemberAsync(project.Id, Ben, Admin, T0.AddMinutes(1), default));
        Assert.False(await Store.RemoveMemberAsync(project.Id, Ben, Admin, T0.AddMinutes(2), default));

        Assert.False(await Store.IsMemberAsync(project.Id, Ben, default));
        Assert.Equal([AuditActions.MemberRemove, AuditActions.MemberAdd], (await AuditAsync()).Select(a => a.Action));
    }

    [SkippableFact]
    public async Task Membership_IsPerProject()
    {
        var a = await AddProjectAsync("a");
        var b = await AddProjectAsync("b");
        await Store.AddMemberAsync(a.Id, Ben, Admin, T0, default);

        Assert.Equal([a.Id], await Store.ListMemberProjectIdsAsync(Ben, default));
        Assert.True(await Store.IsMemberAsync(a.Id, Ben, default));
        Assert.False(await Store.IsMemberAsync(b.Id, Ben, default));
    }

    [SkippableFact]
    public async Task Invitation_IsFoundByItsTokenHash_AndItsCreationIsRecorded()
    {
        var project = await AddProjectAsync();
        var invitation = await InviteAsync(projects: project.Id);

        var found = await Store.FindInvitationAsync(invitation.TokenHash, default);

        Assert.Equal(invitation.Id, found!.Id);
        Assert.Equal(AccountRole.Member, found.Role);
        Assert.Null(await Store.FindInvitationAsync(new string('f', 64), default));
        var row = Assert.Single(await AuditAsync());
        Assert.Equal((Admin, AuditActions.InvitationCreate, (Guid?)invitation.Id), (row.ActorId, row.Action, row.TargetId));
        Assert.Contains("erin", row.DetailsJson);
        Assert.DoesNotContain(invitation.TokenHash, row.DetailsJson); // the audit never holds the token's hash
    }

    [SkippableFact]
    public async Task Invitations_AreListedNewestFirst()
    {
        var older = await InviteAsync("older");
        var newer = await Store.AddInvitationAsync(new Invitation
        {
            UserName = "newer", TokenHash = new string('a', 64), ExpiresAt = T0.AddDays(8), CreatedBy = Admin, CreatedAt = T0.AddDays(1),
        }, default);

        Assert.Equal([newer.Id, older.Id], (await Store.ListInvitationsAsync(default)).Select(i => i.Id));
    }

    [SkippableFact]
    public async Task Accept_UsesTheInvitation_JoinsItsProjects_AndRecordsBoth()
    {
        var a = await AddProjectAsync("a");
        var b = await AddProjectAsync("b");
        var invitation = await InviteAsync(projects: [a.Id, b.Id]);

        await Store.AcceptInvitationAsync(invitation.Id, Erin, T0.AddDays(1), default);

        var used = await Store.FindInvitationAsync(invitation.TokenHash, default);
        Assert.Equal((T0.AddDays(1), (Guid?)Erin), (used!.AcceptedAt, used.AcceptedUserId));
        Assert.Equal(new[] { a.Id, b.Id }.Order(), (await Store.ListMemberProjectIdsAsync(Erin, default)).Order());
        var rows = (await AuditAsync()).Where(r => r.ActorId == Erin).ToList();
        Assert.Equal(1, rows.Count(r => r.Action == AuditActions.InvitationAccept));
        Assert.Equal(new[] { a.Id, b.Id }.Order(), rows.Where(r => r.Action == AuditActions.MemberAdd).Select(r => r.ProjectId!.Value).Order());
    }

    /// <summary>A link works once: a second account cannot be made from it.</summary>
    [SkippableFact]
    public async Task Accept_Twice_IsAConflict_AndTheSecondAccountJoinsNothing()
    {
        var project = await AddProjectAsync();
        var invitation = await InviteAsync(projects: project.Id);
        await Store.AcceptInvitationAsync(invitation.Id, Erin, T0.AddDays(1), default);

        var conflict = await Assert.ThrowsAsync<StoreConflictException>(() => Store.AcceptInvitationAsync(invitation.Id, Ben, T0.AddDays(1), default));

        Assert.Equal("This invitation has already been used.", conflict.Message);
        Assert.False(await Store.IsMemberAsync(project.Id, Ben, default));
        Assert.Equal((Guid?)Erin, (await Store.FindInvitationAsync(invitation.TokenHash, default))!.AcceptedUserId);
    }

    [SkippableFact]
    public async Task Accept_AfterItExpired_IsAConflict_SayingSo()
    {
        var invitation = await InviteAsync(expires: T0.AddDays(7));

        var conflict = await Assert.ThrowsAsync<StoreConflictException>(() => Store.AcceptInvitationAsync(invitation.Id, Erin, T0.AddDays(7), default));

        Assert.Contains("expired", conflict.Message);
        Assert.Null((await Store.FindInvitationAsync(invitation.TokenHash, default))!.AcceptedAt);
    }

    [SkippableFact]
    public async Task Accept_AfterItWasRevoked_IsAConflict_SayingSo()
    {
        var invitation = await InviteAsync();
        await Store.RevokeInvitationAsync(invitation.Id, Admin, T0.AddHours(1), default);

        var conflict = await Assert.ThrowsAsync<StoreConflictException>(() => Store.AcceptInvitationAsync(invitation.Id, Erin, T0.AddHours(2), default));

        Assert.Contains("revoked", conflict.Message);
    }

    [SkippableFact]
    public async Task Accept_AnUnknownInvitation_IsNotFound()
    {
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.AcceptInvitationAsync(Guid.NewGuid(), Erin, T0, default));
    }

    /// <summary>A project removed after the invitation was made is skipped, not an error.</summary>
    [SkippableFact]
    public async Task Accept_SkipsAProjectThatNoLongerExists()
    {
        var project = await AddProjectAsync();
        var invitation = await InviteAsync(projects: [project.Id, Guid.NewGuid()]);

        await Store.AcceptInvitationAsync(invitation.Id, Erin, T0.AddDays(1), default);

        Assert.Equal([project.Id], await Store.ListMemberProjectIdsAsync(Erin, default));
    }

    /// <summary>Two people opening the same link at the same moment: one account, never two.</summary>
    [SkippableFact]
    public async Task Accept_AtTheSameMomentTwice_SucceedsExactlyOnce()
    {
        var project = await AddProjectAsync();
        var invitation = await InviteAsync(projects: project.Id);

        var results = await Task.WhenAll(
            Task.Run(() => TryAcceptAsync(invitation.Id, Erin)),
            Task.Run(() => TryAcceptAsync(invitation.Id, Ben)));

        Assert.Equal(1, results.Count(accepted => accepted));
        Assert.Single(await Store.ListMembersAsync(project.Id, default), m => m.UserId == Erin || m.UserId == Ben);
    }

    private async Task<bool> TryAcceptAsync(Guid invitationId, Guid userId)
    {
        try
        {
            await Store.AcceptInvitationAsync(invitationId, userId, T0.AddDays(1), default);
            return true;
        }
        catch (StoreConflictException)
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task Revoke_StopsTheLink_RecordsWhoDidIt_AndCannotBeRepeated()
    {
        var invitation = await InviteAsync();

        await Store.RevokeInvitationAsync(invitation.Id, Admin, T0.AddHours(1), default);

        Assert.Equal(T0.AddHours(1), (await Store.FindInvitationAsync(invitation.TokenHash, default))!.RevokedAt);
        Assert.Equal(AuditActions.InvitationRevoke, (await AuditAsync())[0].Action);
        var again = await Assert.ThrowsAsync<StoreConflictException>(() => Store.RevokeInvitationAsync(invitation.Id, Admin, T0.AddHours(2), default));
        Assert.Contains("already been revoked", again.Message);
    }

    [SkippableFact]
    public async Task Revoke_AnInvitationAlreadyUsed_IsAConflict_AndChangesNothing()
    {
        var invitation = await InviteAsync();
        await Store.AcceptInvitationAsync(invitation.Id, Erin, T0.AddHours(1), default);

        var conflict = await Assert.ThrowsAsync<StoreConflictException>(() => Store.RevokeInvitationAsync(invitation.Id, Admin, T0.AddHours(2), default));

        Assert.Contains("already been used", conflict.Message);
        Assert.Null((await Store.FindInvitationAsync(invitation.TokenHash, default))!.RevokedAt);
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.RevokeInvitationAsync(Guid.NewGuid(), Admin, T0, default));
    }

    // ---- Every user action is audited in its own transaction (§13.3) ----

    private async Task<AuditEvent[]> RowsAsync(string action) =>
        [.. (await Store.ListAuditAsync(null, 500, default)).Where(r => r.Action == action)];

    private static T Details<T>(AuditEvent row, string property) =>
        JsonDocument.Parse(row.DetailsJson!).RootElement.EnumerateObject()
            .First(p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase)).Value.Deserialize<T>()!;

    [SkippableFact]
    public async Task RegisteringAProject_AndCreatingAThread_AreRecorded_ByWhoDidThem()
    {
        var project = await AddProjectAsync();
        var thread = await AddThreadAsync(project);

        var registered = Assert.Single(await RowsAsync(AuditActions.ProjectRegister));
        Assert.Equal((Admin, (Guid?)project.Id, (Guid?)project.Id), (registered.ActorId, registered.TargetId, registered.ProjectId));
        Assert.Equal("acme/salesapp", Details<string>(registered, "gitHub"));
        var created = Assert.Single(await RowsAsync(AuditActions.ThreadCreate));
        Assert.Equal((Admin, (Guid?)thread.Id, (Guid?)project.Id), (created.ActorId, created.TargetId, created.ProjectId));
    }

    [SkippableFact]
    public async Task Delegating_IsRecorded_ButAFollowUpOrARetriedMessageIsNot()
    {
        var (_, thread, runId) = await QueuedAsync();
        await Store.DispatchAsync(thread.Id, Ben, "follow-up", "Also handle an empty result.", T0, default);
        await Store.DispatchAsync(thread.Id, Ben, "follow-up", "Also handle an empty result.", T0, default);

        var row = Assert.Single(await RowsAsync(AuditActions.ThreadDelegate));
        Assert.Equal((Admin, (Guid?)thread.Id), (row.ActorId, row.TargetId));
        Assert.Equal(runId, Details<Guid>(row, "runId"));
        Assert.Empty(await RowsAsync(AuditActions.ThreadChangeRequest));
    }

    [SkippableFact]
    public async Task AChangeRequest_IsRecorded_WithItsBudgetTopUp()
    {
        var handedOff = await HandedOffAsync(cap: 600_000);

        await Store.DispatchAsync(handedOff.Thread.Id, Ben, "msg-2", "Handle a failed compaction.", T0, default, reworkTopUpShare: 0.5);

        var row = Assert.Single(await RowsAsync(AuditActions.ThreadChangeRequest));
        Assert.Equal(Ben, row.ActorId);
        Assert.Equal(300_000, Details<long>(row, "budgetTopUp"));
    }

    [SkippableFact]
    public async Task ARejectedMessage_IsNotRecorded()
    {
        var running = await RunningAsync();
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.Pause, StopReason.PausedByUser), T0, default);

        var result = await Store.DispatchAsync(running.Thread.Id, Ben, "m-2", "More.", T0, default);

        Assert.Equal(DispatchOutcome.Rejected, result.Outcome);
        Assert.Single(await RowsAsync(AuditActions.ThreadDelegate)); // the first delegation only
        Assert.Empty(await RowsAsync(AuditActions.ThreadChangeRequest));
    }

    [SkippableTheory]
    [InlineData(LifecycleTrigger.Pause, AuditActions.ThreadPause)]
    [InlineData(LifecycleTrigger.Cancel, AuditActions.ThreadCancel)]
    public async Task PausingOrCancellingAQueuedTask_IsRecorded_WithTheStatesItMovedBetween(LifecycleTrigger trigger, string action)
    {
        var (_, thread, _) = await QueuedAsync();

        await Store.ApplyUserActionAsync(thread.Id, Ben, trigger, T0.AddMinutes(1), default);

        var row = Assert.Single(await RowsAsync(action));
        Assert.Equal((Ben, T0.AddMinutes(1)), (row.ActorId, row.CreatedAt));
        Assert.Equal("Queued", Details<string>(row, "from"));
    }

    [SkippableTheory]
    [InlineData(LifecycleTrigger.Pause, LifecycleTrigger.Resume)]
    [InlineData(LifecycleTrigger.Block, LifecycleTrigger.ResolveBlocker)]
    [InlineData(LifecycleTrigger.ExhaustBudget, LifecycleTrigger.RaiseBudgetAndResume)]
    [InlineData(LifecycleTrigger.Interrupt, LifecycleTrigger.Recover)]
    public async Task EveryWayBackIntoTheQueue_IsRecordedAsAResume_NamingWhich(LifecycleTrigger stop, LifecycleTrigger resume)
    {
        var running = await RunningAsync();
        await Store.StopRunAsync(Stop(running.Run.Id, stop, StopReason.PausedByUser), T0, default);

        await Store.ApplyUserActionAsync(running.Thread.Id, Ben, resume, T0, default);

        var row = Assert.Single(await RowsAsync(AuditActions.ThreadResume));
        Assert.Equal(resume.ToString(), Details<string>(row, "trigger"));
        Assert.Equal("Queued", Details<string>(row, "to"));
    }

    [SkippableFact]
    public async Task Accepting_IsRecorded()
    {
        var handedOff = await HandedOffAsync(cap: null);

        await Store.ApplyUserActionAsync(handedOff.Thread.Id, Ben, LifecycleTrigger.Accept, T0, default);

        Assert.Equal(Ben, Assert.Single(await RowsAsync(AuditActions.ThreadAccept)).ActorId);
    }

    /// <summary>A refused action rolls back with its audit row: the log never claims what did not happen.</summary>
    [SkippableFact]
    public async Task ARefusedAction_LeavesNoAuditRow()
    {
        var (_, thread, _) = await QueuedAsync();

        await Assert.ThrowsAsync<StoreConflictException>(() => Store.ApplyUserActionAsync(thread.Id, Ben, LifecycleTrigger.Accept, T0, default));

        Assert.Empty(await RowsAsync(AuditActions.ThreadAccept));
    }

    /// <summary>M1 changed budgets without recording who.</summary>
    [SkippableFact]
    public async Task ChangingABudget_IsRecorded_WithTheOldAndNewCap()
    {
        var running = await RunningAsync(cap: 300_000);

        await Store.SetBudgetCapAsync(running.Thread.Id, 400_000, Ben, T0, default);
        await Store.SetBudgetCapAsync(running.Thread.Id, null, Ben, T0.AddMinutes(1), default);

        var rows = (await RowsAsync(AuditActions.ThreadBudget)).OrderBy(r => r.CreatedAt).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal((300_000L, 400_000L), (Details<long>(rows[0], "from"), Details<long>(rows[0], "to")));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(rows[1].DetailsJson!).RootElement.EnumerateObject()
            .First(p => string.Equals(p.Name, "to", StringComparison.OrdinalIgnoreCase)).Value.ValueKind);
        Assert.All(rows, r => Assert.Equal((Ben, (Guid?)running.Project.Id), (r.ActorId, r.ProjectId)));
    }

    [SkippableFact]
    public async Task EditingAThread_ChangesItsTitleOrType_AnnouncesIt_AndIsRecorded()
    {
        var project = await AddProjectAsync();
        var thread = await AddThreadAsync(project);
        var events = (await Store.ReadEventsAsync(thread.Id, 0, 100, default)).Count;

        var edited = await Store.EditThreadAsync(thread.Id, "Export orders as CSV", "bug", Ben, T0.AddMinutes(1), default);

        Assert.Equal(("Export orders as CSV", "bug"), (edited.Title, edited.TypeLabel));
        Assert.Equal(thread.Revision + 1, edited.Revision);
        Assert.Equal(events + 1, (await Store.ReadEventsAsync(thread.Id, 0, 100, default)).Count);
        var row = Assert.Single(await RowsAsync(AuditActions.ThreadEdit));
        Assert.Equal((Ben, (Guid?)thread.Id), (row.ActorId, row.TargetId));
        Assert.Contains("Add CSV export", row.DetailsJson);
        Assert.Contains("Export orders as CSV", row.DetailsJson);
    }

    [SkippableFact]
    public async Task EditingAThread_ToWhatItAlreadyIs_ChangesNothing()
    {
        var project = await AddProjectAsync();
        var thread = await AddThreadAsync(project);

        var same = await Store.EditThreadAsync(thread.Id, thread.Title, null, Ben, T0, default);

        Assert.Equal(thread.Revision, same.Revision);
        Assert.Empty(await RowsAsync(AuditActions.ThreadEdit));
    }

    [SkippableFact]
    public async Task EditingAThreadThatDoesNotExist_IsNotFound()
    {
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.EditThreadAsync(Guid.NewGuid(), "x", null, Ben, T0, default));
    }

    [SkippableFact]
    public async Task AnsweringADecision_IsRecorded_WithTheQuestionAndTheAnswer()
    {
        var running = await RunningAsync();
        var decision = await Store.OpenDecisionAsync(running.Run.Id, new DecisionSubmission("All rows?", "w", ["Yes", "No"]), T0, default);
        await Store.StopRunAsync(Stop(running.Run.Id, LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded), T0, default);

        await Store.AnswerDecisionAsync(decision.Id, Ben, "Yes", T0, default);

        var row = Assert.Single(await RowsAsync(AuditActions.DecisionAnswer));
        Assert.Equal((Ben, AuditTargets.Decision, (Guid?)decision.Id), (row.ActorId, row.TargetType, row.TargetId));
        Assert.Equal(("All rows?", "Yes"), (Details<string>(row, "question"), Details<string>(row, "answer")));
    }

    [SkippableFact]
    public async Task AFindingVerdict_IsRecorded_WithWhatItWasBefore()
    {
        var running = await RunningAsync();
        await Store.SaveFindingsAsync(running.Run.Id, [new ReviewFinding(FindingSeverity.Minor, "src/A.cs", 1, "x")], default);
        var finding = Assert.Single(await Store.ListFindingsAsync(running.Thread.Id, default));

        await Store.SetFindingVerdictAsync(finding.Id, FindingVerdict.Real, Ben, T0, default);
        await Store.SetFindingVerdictAsync(finding.Id, FindingVerdict.Wrong, Ben, T0.AddMinutes(1), default);

        var last = (await RowsAsync(AuditActions.FindingVerdict)).OrderBy(r => r.CreatedAt).Last();
        Assert.Equal(("Real", "Wrong"), (Details<string>(last, "from"), Details<string>(last, "to")));
    }

    [SkippableFact]
    public async Task WithdrawingAChangeRequest_IsRecorded()
    {
        var handedOff = await HandedOffAsync(cap: null);
        await Store.SaveHandoffAsync(new HandoffRecord
        {
            RunId = handedOff.Run.Id, ThreadId = handedOff.Thread.Id, Branch = "factory/x", CommitSha = "abc", EvidenceJson = "{}", CreatedAt = T0,
        }, default);
        await Store.DispatchAsync(handedOff.Thread.Id, Ben, "rework", "Change it.", T0, default);

        await Store.WithdrawChangesAsync(handedOff.Thread.Id, Ben, T0.AddMinutes(1), default);

        Assert.Equal(Ben, Assert.Single(await RowsAsync(AuditActions.ThreadWithdraw)).ActorId);
    }

    [SkippableFact]
    public async Task Audit_IsListedNewestFirst_ByProjectOrEverything_UpToTheLimit()
    {
        var a = await AddProjectAsync("a");
        var b = await AddProjectAsync("b");
        await Store.AddMemberAsync(a.Id, Ben, Admin, T0.AddMinutes(1), default);
        await Store.AddMemberAsync(b.Id, Ben, Admin, T0.AddMinutes(2), default);
        await Store.AddAuditAsync(new AuditEvent
        {
            ActorId = Admin, Action = "user.disable", TargetType = AuditTargets.User, TargetId = Ben,
            DetailsJson = """{"reason":"left the team"}""", CreatedAt = T0.AddMinutes(3),
        }, default);

        Assert.Equal(["user.disable", AuditActions.MemberAdd, AuditActions.MemberAdd], (await AuditAsync()).Select(r => r.Action));
        Assert.Equal([(Guid?)a.Id], (await AuditAsync(a.Id)).Select(r => r.ProjectId));
        Assert.Single(await Store.ListAuditAsync(null, 1, default));
        Assert.Contains("left the team", (await AuditAsync())[0].DetailsJson);
    }
}

public sealed class SqliteFactoryStoreTests : FactoryStoreContract
{
    private SqliteConnection? _connection;

    protected override async Task<IDbContextFactory<FactoryDbContext>> CreateDatabaseAsync()
    {
        // A shared in-memory database lives as long as one connection to it stays open.
        var connectionString = $"Data Source=factory-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync();

        var factory = new TestContextFactory(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connectionString).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    protected override async Task DropDatabaseAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

/// <summary>The same contract on real PostgreSQL, created by the real migration.</summary>
public sealed class PostgresFactoryStoreTests : FactoryStoreContract
{
    public const string Variable = "FACTORY_TEST_DB";

    private string? _database;

    private static string Admin(string database) =>
        new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)) { Database = database }.ConnectionString;

    protected override async Task<IDbContextFactory<FactoryDbContext>> CreateDatabaseAsync()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)),
            $"Set {Variable} to an admin connection string for a PostgreSQL server to run the store tests against PostgreSQL.");

        _database = $"litos_factory_test_{Guid.NewGuid():n}";
        await using (var connection = new NpgsqlConnection(Admin("postgres")))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        await FactoryDatabase.MigrateAsync(Admin(_database), default);
        return new TestContextFactory(new DbContextOptionsBuilder<FactoryDbContext>().UseNpgsql(Admin(_database)).Options);
    }

    protected override async Task DropDatabaseAsync()
    {
        if (_database is null)
            return;

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(Admin("postgres"));
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
