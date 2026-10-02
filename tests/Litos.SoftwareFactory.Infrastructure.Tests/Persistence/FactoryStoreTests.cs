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
        Assert.Equal([EventTypes.MessageAdded, EventTypes.StateChanged], events.Select(e => e.Type));
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

    /// <summary>The slot cap stays at 1 in M1: a second queued run waits while one is running.</summary>
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
        Assert.Equal((RefusalReason.TaskBudget, 20_900L, 20_000L), (refused.Reason, refused.Needed, refused.Remaining));
        Assert.Equal(0, (await ThreadAsync(running.Thread.Id)).TokensReserved);
        Assert.Empty(await Store.ListUsageAsync(running.Thread.Id, default));
    }

    [SkippableFact]
    public async Task Reserve_ThenSettle_ChargesActualUsage_AndReleasesTheRest()
    {
        var running = await RunningAsync(cap: 100_000);

        var admitted = Assert.IsType<Admitted>((await Store.ReserveAsync(Reserve(running, "key-1"), Policy, T0, default)).Decision);
        Assert.Equal(20_900, (await ThreadAsync(running.Thread.Id)).TokensReserved);

        var usage = new UsageInfo(200, 900, 3_000, 9_000, ReasoningTokens: 640);
        await Store.SettleAsync("key-1", usage, BudgetLedger.ChargeFor(usage, cachedInputWeight: 1), T0.AddSeconds(30), default);

        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((13_100L, 0L), (thread.TokensUsed, thread.TokensReserved));
        var entry = Assert.Single(await Store.ListUsageAsync(running.Thread.Id, default));
        Assert.Equal(UsageStatus.Settled, entry.Status);
        Assert.Equal((12_200L, 12_000L, 900L, 640L, 13_100L, admitted.Reserved), (entry.ActualInput, entry.ActualCachedInput, entry.ActualOutput, entry.ActualReasoning, entry.Charged, entry.Reserved));
        Assert.Equal(T0.AddSeconds(30), entry.SettledAt);
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

        Assert.Equal(20_900, (await ThreadAsync(running.Thread.Id)).TokensReserved);
        Assert.Equal(UsageStatus.Unknown, Assert.Single(await Store.ListUsageAsync(running.Thread.Id, default)).Status);
        Assert.IsType<Refused>((await Store.ReserveAsync(Reserve(running, "key-2"), Policy, T0, default)).Decision);

        // Reconciled later with what the provider finally reported.
        await Store.SettleAsync("key-1", new UsageInfo(9_000, 500), 9_500, T0, default);
        var thread = await ThreadAsync(running.Thread.Id);
        Assert.Equal((9_500L, 0L), (thread.TokensUsed, thread.TokensReserved));
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

        var thread = await Store.SetBudgetCapAsync(running.Thread.Id, 100_000, T0, default);

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
        var running = await RunningAsync(cap: 50_000); // each call reserves 20,900: two fit, a third does not

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => Store.ReserveAsync(Reserve(running, $"key-{i}"), Policy, T0, default))));

        Assert.Equal(2, results.Count(r => r.Decision is Admitted));
        Assert.Equal(41_800, (await ThreadAsync(running.Thread.Id)).TokensReserved);
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

    [SkippableFact]
    public async Task MessageSequences_AreGaplessPerThread()
    {
        var running = await RunningAsync();
        for (var i = 0; i < 4; i++)
            await Store.AddFactoryMessageAsync(running.Thread.Id, MessageKind.Status, $"note {i}", null, T0, default);

        var messages = (await Store.GetThreadAsync(running.Thread.Id, default))!.Messages;

        Assert.Equal([1L, 2L, 3L, 4L, 5L], messages.Select(m => m.Sequence));
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
