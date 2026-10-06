using System.Text.Json;
using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// The factory store on EF Core. Each method is one short transaction. Writes to a thread take
/// that thread's row lock first, so the gateway settling a model call and the coordinator saving
/// a checkpoint never overwrite each other; claiming work takes one advisory lock, because a
/// unique index alone cannot express "under the slot cap, and nobody else holds this repository".
/// </summary>
public sealed class EfFactoryStore(IDbContextFactory<FactoryDbContext> contextFactory) : IFactoryStore
{
    /// <summary>The advisory lock every claim transaction takes; any fixed value works.</summary>
    private const long ClaimLockKey = 0x4C49544F53464143; // "LITOSFAC"

    /// <summary>Stands in for row and advisory locks on providers that have neither (SQLite, in tests).</summary>
    private static readonly SemaphoreSlim FallbackWriteLock = new(1, 1);

    // ---- Projects and threads ----

    public async Task<Project> AddProjectAsync(Project project, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (project.Id == Guid.Empty)
            project.Id = Guid.NewGuid();
        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new StoreConflictException($"{project.GitHubOwner}/{project.GitHubRepository} is already registered.");
        }

        return project;
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
    }

    public async Task<TaskThread> AddThreadAsync(TaskThread thread, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (!await db.Projects.AnyAsync(p => p.Id == thread.ProjectId, ct))
            throw new StoreNotFoundException("The project does not exist.");

        if (thread.Id == Guid.Empty)
            thread.Id = Guid.NewGuid();
        thread.InitialBudgetCap ??= thread.BudgetCap;
        db.Threads.Add(thread);
        await db.SaveChangesAsync(ct);
        return thread;
    }

    public async Task<IReadOnlyList<TaskThread>> ListThreadsAsync(Guid? projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var threads = db.Threads.AsNoTracking();
        if (projectId is { } id)
            threads = threads.Where(t => t.ProjectId == id);
        return await threads.OrderByDescending(t => t.UpdatedAt).ToListAsync(ct);
    }

    public async Task<ThreadDetails?> GetThreadAsync(Guid threadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread is null)
            return null;

        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == thread.ProjectId, ct);
        var messages = await db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId).OrderBy(m => m.Sequence).ToListAsync(ct);
        var decisions = await db.Decisions.AsNoTracking().Where(d => d.ThreadId == threadId).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var latestRun = await db.Runs.AsNoTracking().Where(r => r.ThreadId == threadId).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var handoff = await db.Handoffs.AsNoTracking().Where(h => h.ThreadId == threadId).OrderByDescending(h => h.CreatedAt).FirstOrDefaultAsync(ct);

        VerificationRecord? verification = null;
        IReadOnlyList<ReviewFindingRecord> findings = [];
        if (latestRun is not null)
        {
            verification = await db.Verifications.AsNoTracking()
                .Where(v => v.RunId == latestRun.Id && v.Kind == VerificationKind.Run)
                .OrderByDescending(v => v.CreatedAt).FirstOrDefaultAsync(ct);
            findings = await db.ReviewFindings.AsNoTracking().Where(f => f.RunId == latestRun.Id).ToListAsync(ct);
        }

        return new ThreadDetails(thread, project, messages, decisions, latestRun, handoff, verification, findings);
    }

    public async Task<TaskRun?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    // ---- Dispatch and user actions ----

    public async Task<DispatchResult> DispatchAsync(
        Guid threadId, Guid userId, string dispatchKey, string text, DateTimeOffset now, CancellationToken ct, double reworkTopUpShare = 0)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);

        var earlier = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.DispatchKey == dispatchKey, ct);
        if (earlier is not null)
            return new DispatchResult(DispatchOutcome.Duplicate, thread, await ActiveRunIdAsync(db, threadId, ct));

        Guid? runId;
        DispatchOutcome outcome;
        long topUp = 0;
        switch (thread.State)
        {
            case LifecycleState.Draft:
                thread.State = TaskLifecycle.Apply(thread.State, LifecycleTrigger.Delegate);
                runId = AddRun(db, thread, userId, RunKind.Implement, text, now);
                outcome = DispatchOutcome.Queued;
                break;

            case LifecycleState.AwaitingHumanTesting:
                // The tester asks for changes: a new run on the same branch, with its own limits.
                thread.State = TaskLifecycle.Apply(thread.State, LifecycleTrigger.RequestChanges);
                runId = AddRun(db, thread, userId, RunKind.Rework, text, now);
                outcome = DispatchOutcome.Queued;

                // A change the tester asks for is new work, and gets budget of its own.
                topUp = thread.BudgetCap is null ? 0 : BudgetLedger.ReworkTopUp(thread.InitialBudgetCap ?? thread.BudgetCap, reworkTopUpShare);
                thread.BudgetCap += topUp;
                break;

            case LifecycleState.Queued or LifecycleState.Running:
                runId = await ActiveRunIdAsync(db, threadId, ct);
                outcome = DispatchOutcome.FollowUp;
                break;

            default:
                return new DispatchResult(DispatchOutcome.Rejected, thread, null, RejectionReason(thread.State));
        }

        if (outcome == DispatchOutcome.Queued)
        {
            thread.Stage = Stage.Implement;
            thread.StateReason = null;
        }

        AddMessage(db, thread, MessageAuthor.User, userId, MessageKind.Text, text, now, dispatchKey: dispatchKey);
        if (topUp > 0)
        {
            AddMessage(
                db, thread, MessageAuthor.Factory, null, MessageKind.Status,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"This change request adds {topUp:N0} tokens to the budget ({reworkTopUpShare * 100:0.#}% of the original {thread.InitialBudgetCap ?? thread.BudgetCap - topUp:N0}). The cap is now {thread.BudgetCap:N0}."),
                now);
        }

        Touch(db, thread, now);

        try
        {
            await write.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two requests with the same message id raced; the unique index let one through.
            return new DispatchResult(DispatchOutcome.Duplicate, thread, null);
        }

        return new DispatchResult(outcome, thread, runId);
    }

    private static string RejectionReason(LifecycleState state) => state switch
    {
        LifecycleState.AwaitingDecision => "This task is waiting for a decision. Answer the open decision to continue.",
        // Budget exhaustion cannot be bypassed by delegating again (§5).
        LifecycleState.PausedBudget => "This task is paused because its token budget is used up. Raise the budget and resume, or start a separate task.",
        LifecycleState.PausedUser => "This task is paused. Resume it to continue.",
        LifecycleState.Blocked => "This task is blocked. Resume it once the blocker is resolved.",
        LifecycleState.Interrupted => "This task was interrupted. Recover it to continue.",
        LifecycleState.Accepted => "This task was accepted. Start a new thread for further work.",
        LifecycleState.Cancelled => "This task was cancelled. Start a new thread for further work.",
        _ => $"This task cannot take new work while it is {state}.",
    };

    private static Guid AddRun(FactoryDbContext db, TaskThread thread, Guid userId, RunKind kind, string request, DateTimeOffset now)
    {
        var run = new TaskRun
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            RequestedBy = userId,
            Kind = kind,
            Request = request,
            PromptRevision = BriefComposer.Revision,
            CreatedAt = now,
        };
        db.Runs.Add(run);
        return run.Id;
    }

    private static async Task<Guid?> ActiveRunIdAsync(FactoryDbContext db, Guid threadId, CancellationToken ct) =>
        (await ActiveRunAsync(db, threadId, ct))?.Id;

    private static Task<TaskRun?> ActiveRunAsync(FactoryDbContext db, Guid threadId, CancellationToken ct) =>
        db.Runs.FirstOrDefaultAsync(r => r.ThreadId == threadId && r.Status != RunStatus.Finished, ct);

    public async Task<TaskThread> ApplyUserActionAsync(
        Guid threadId, Guid userId, LifecycleTrigger trigger, DateTimeOffset now, CancellationToken ct)
    {
        if (trigger is not (LifecycleTrigger.Accept or LifecycleTrigger.Cancel or LifecycleTrigger.Pause or LifecycleTrigger.Resume
            or LifecycleTrigger.RaiseBudgetAndResume or LifecycleTrigger.ResolveBlocker or LifecycleTrigger.Recover))
        {
            throw new ArgumentException($"{trigger} is not something a user does.", nameof(trigger));
        }

        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);

        // A running task is stopped through its worker, by the coordinator — not from here.
        if (thread.State == LifecycleState.Running)
            throw new StoreConflictException("This task is running; stop it through the coordinator.");
        if (!TaskLifecycle.TryApply(thread.State, trigger, out var next))
            throw new StoreConflictException($"A task that is {thread.State} cannot {trigger}.");

        var run = await ActiveRunAsync(db, threadId, ct);
        string note;
        switch (trigger)
        {
            case LifecycleTrigger.Accept:
                thread.Stage = Stage.Done;
                note = "Accepted. Acceptance records approval; merging is done in GitHub.";
                break;

            case LifecycleTrigger.Cancel:
                if (run is not null)
                {
                    run.Status = RunStatus.Finished;
                    run.StopReason = StopReason.Cancelled;
                    run.EndedAt = now;
                }

                await ReleaseLeaseAsync(db, threadId, ct);
                note = "Cancelled. Edits, the branch and the evidence are kept.";
                break;

            case LifecycleTrigger.Pause:
                if (run is not null)
                    run.Status = RunStatus.Suspended;
                note = "Paused.";
                break;

            default: // the four ways back into the queue
                if (run is null)
                    throw new StoreConflictException("This task has no run to continue.");
                run.Status = RunStatus.Queued;
                note = "Queued to continue.";
                break;
        }

        thread.State = next;
        thread.StateReason = null;
        AddMessage(db, thread, MessageAuthor.User, userId, MessageKind.Status, note, now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return thread;
    }

    public async Task<WithdrawResult> WithdrawChangesAsync(Guid threadId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);

        if (thread.State == LifecycleState.Running)
            throw new StoreConflictException("The change request is being worked on. Pause the task, then withdraw it.");
        if (!TaskLifecycle.TryApply(thread.State, LifecycleTrigger.WithdrawChanges, out var next))
            throw new StoreConflictException($"A task that is {thread.State} has no change request to withdraw.");

        // The lifecycle table allows the move from these states; only a rework run with a
        // handoff behind it makes it mean anything.
        var run = await ActiveRunAsync(db, threadId, ct);
        if (run is not { Kind: RunKind.Rework })
            throw new StoreConflictException("This task has no change request to withdraw: its run is the original work. Cancel the task to stop it.");
        if (!await db.Handoffs.AnyAsync(h => h.ThreadId == threadId, ct))
            throw new StoreConflictException("This task has no handoff to go back to.");

        run.Status = RunStatus.Finished;
        run.StopReason = StopReason.Withdrawn;
        run.EndedAt = now;
        run.WorkerProcessId = null;
        run.WorkerStartTime = null;

        // A question the withdrawn run was waiting on no longer needs an answer.
        foreach (var decision in await db.Decisions.Where(d => d.RunId == run.Id && d.Status == DecisionStatus.Open).ToListAsync(ct))
        {
            decision.Status = DecisionStatus.Answered;
            decision.Answer = "Not answered: the change request was withdrawn.";
            decision.AnsweredBy = userId;
            decision.AnsweredAt = now;
        }

        thread.State = next;
        thread.Stage = Stage.Handoff;
        thread.StateReason = null;

        var heldLease = await db.Leases.AnyAsync(l => l.ThreadId == threadId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == thread.ProjectId, ct);
        AddMessage(db, thread, MessageAuthor.User, userId, MessageKind.Status, "Change request withdrawn. The task is back at its last handoff.", now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return new WithdrawResult(thread, project, heldLease);
    }

    public async Task ReleaseLeaseAsync(Guid threadId, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        await ReleaseLeaseAsync(write.Db, threadId, ct);
        await write.CommitAsync(ct);
    }

    public async Task<TaskThread> SetBudgetCapAsync(Guid threadId, long? cap, DateTimeOffset now, CancellationToken ct)
    {
        if (cap is < 0)
            throw new ArgumentOutOfRangeException(nameof(cap));

        await using var write = await BeginWriteAsync(ct);
        var thread = await LockThreadAsync(write.Db, threadId, ct);
        thread.BudgetCap = cap;
        Touch(write.Db, thread, now, EventTypes.UsageChanged);
        await write.CommitAsync(ct);
        return thread;
    }

    public async Task<Decision> OpenDecisionAsync(Guid runId, DecisionSubmission submission, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new StoreNotFoundException("The run does not exist.");

        var decision = new Decision
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            ThreadId = run.ThreadId,
            Question = submission.Question,
            WhyItBlocks = submission.WhyItBlocks,
            OptionsJson = JsonSerializer.Serialize(submission.Options),
            Recommendation = submission.Recommendation,
            Impact = submission.Impact,
            CreatedAt = now,
        };
        db.Decisions.Add(decision);
        await db.SaveChangesAsync(ct);
        return decision;
    }

    public async Task<TaskThread> AnswerDecisionAsync(Guid decisionId, Guid userId, string answer, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var threadId = await db.Decisions.Where(d => d.Id == decisionId).Select(d => (Guid?)d.ThreadId).FirstOrDefaultAsync(ct)
            ?? throw new StoreNotFoundException("The decision does not exist.");
        var thread = await LockThreadAsync(db, threadId, ct);
        var decision = await db.Decisions.FirstAsync(d => d.Id == decisionId, ct);

        if (decision.Status != DecisionStatus.Open)
            throw new StoreConflictException("This decision has already been answered.");
        if (!TaskLifecycle.TryApply(thread.State, LifecycleTrigger.AnswerDecision, out var next))
            throw new StoreConflictException($"A task that is {thread.State} is not waiting for a decision.");

        var run = await db.Runs.FirstAsync(r => r.Id == decision.RunId, ct);
        decision.Status = DecisionStatus.Answered;
        decision.Answer = answer;
        decision.AnsweredBy = userId;
        decision.AnsweredAt = now;

        run.Status = RunStatus.Queued;
        run.Entry = RunEntry.DecisionAnswered;
        run.EntryAnswer = answer;

        thread.State = next;
        thread.StateReason = null;
        AddMessage(db, thread, MessageAuthor.User, userId, MessageKind.DecisionAnswer, answer, now, decisionId: decisionId);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return thread;
    }

    // ---- The coordinator ----

    public async Task<ClaimedRun?> ClaimNextRunAsync(int slotCap, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct, claim: true);
        var db = write.Db;

        if (await db.Threads.CountAsync(t => t.State == LifecycleState.Running, ct) >= slotCap)
            return null;

        var queued = await db.Runs.Where(r => r.Status == RunStatus.Queued).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        foreach (var run in queued)
        {
            var thread = await LockThreadAsync(db, run.ThreadId, ct);
            if (thread.State != LifecycleState.Queued)
                continue;

            var project = await db.Projects.FirstAsync(p => p.Id == thread.ProjectId, ct);
            var lockIdentity = LockIdentity(project);
            var lease = await db.Leases.FirstOrDefaultAsync(l => l.LockIdentity == lockIdentity, ct);

            if (lease is not null && lease.ThreadId != thread.Id)
            {
                // The queued thread says exactly what it waits for (§6.3).
                var holder = await db.Threads.AsNoTracking().FirstAsync(t => t.Id == lease.ThreadId, ct);
                var reason = $"Waiting for {project.Name} — held by \"{holder.Title}\" ({Describe(holder.State)}).";
                if (thread.StateReason != reason)
                {
                    thread.StateReason = reason;
                    Touch(db, thread, now);
                }

                continue;
            }

            if (lease is null)
            {
                db.Leases.Add(new WorkspaceLease
                {
                    Id = Guid.NewGuid(),
                    LockIdentity = lockIdentity,
                    ProjectId = project.Id,
                    ThreadId = thread.Id,
                    RunId = run.Id,
                    HeartbeatAt = now,
                    CreatedAt = now,
                });
            }
            else
            {
                lease.RunId = run.Id;
                lease.HeartbeatAt = now;
                lease.RecoveryRequired = false;
            }

            thread.State = TaskLifecycle.Apply(thread.State, LifecycleTrigger.Claim);
            thread.StateReason = null;
            run.Status = RunStatus.Running;
            run.StartedAt ??= now;
            run.HeartbeatAt = now;
            run.StopReason = null;
            Touch(db, thread, now);
            await write.CommitAsync(ct);
            return new ClaimedRun(run, thread, project);
        }

        // Nothing could start, but the waiting reasons recorded above are still worth keeping.
        await write.CommitAsync(ct);
        return null;
    }

    /// <summary>In clone mode the lock identity is the project (§6.3).</summary>
    private static string LockIdentity(Project project) => $"project:{project.Id:N}";

    private static string Describe(LifecycleState state) => state switch
    {
        LifecycleState.AwaitingDecision => "awaiting decision",
        LifecycleState.PausedBudget => "paused for budget",
        LifecycleState.PausedUser => "paused",
        LifecycleState.AwaitingHumanTesting => "awaiting human testing",
        _ => state.ToString().ToLowerInvariant(),
    };

    public async Task SaveCheckpointAsync(Guid runId, string stateJson, Stage stage, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new StoreNotFoundException("The run does not exist.");
        var thread = await LockThreadAsync(db, run.ThreadId, ct);

        run.StateJson = stateJson;
        run.HeartbeatAt = now;
        var lease = await db.Leases.FirstOrDefaultAsync(l => l.ThreadId == thread.Id, ct);
        if (lease is not null)
            lease.HeartbeatAt = now;

        // Meaningful stage changes are events; a checkpoint within the same stage is not.
        if (thread.Stage != stage)
        {
            thread.Stage = stage;
            Touch(db, thread, now);
        }

        await write.CommitAsync(ct);
    }

    public async Task SetRunWorkerAsync(Guid runId, int? processId, DateTimeOffset? startTime, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new StoreNotFoundException("The run does not exist.");
        run.WorkerProcessId = processId;
        run.WorkerStartTime = startTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetRunCommitsAsync(Guid runId, string? baselineCommit, string? headCommit, string? reviewSessionId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new StoreNotFoundException("The run does not exist.");
        run.BaselineCommit = baselineCommit ?? run.BaselineCommit;
        run.HeadCommit = headCommit ?? run.HeadCommit;
        run.ReviewSessionId = reviewSessionId ?? run.ReviewSessionId;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetThreadBranchAsync(Guid threadId, string branch, string baseCommit, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var thread = await LockThreadAsync(write.Db, threadId, ct);
        thread.Branch = branch;
        thread.BaseCommit = baseCommit;
        await write.CommitAsync(ct);
    }

    public async Task StopRunAsync(StopRunCommand command, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == command.RunId, ct) ?? throw new StoreNotFoundException("The run does not exist.");
        var thread = await LockThreadAsync(db, run.ThreadId, ct);

        if (!TaskLifecycle.TryApply(thread.State, command.Trigger, out var next))
            throw new StoreConflictException($"A task that is {thread.State} cannot {command.Trigger}.");

        var finished = command.Trigger is LifecycleTrigger.Handoff or LifecycleTrigger.Cancel;
        run.Status = finished ? RunStatus.Finished : RunStatus.Suspended;
        run.StopReason = command.Reason;
        run.StateJson = command.StateJson ?? run.StateJson;
        run.Entry = RunEntry.Resume;
        run.EntryAnswer = null;
        run.WorkerProcessId = null;
        run.WorkerStartTime = null;
        if (finished)
            run.EndedAt = now;

        thread.State = next;
        thread.Stage = command.Stage;
        thread.StateReason = command.Trigger == LifecycleTrigger.Handoff ? null : command.Message;

        if (command.ReleaseLease || finished)
            await ReleaseLeaseAsync(db, thread.Id, ct);

        AddMessage(
            db, thread, MessageAuthor.Factory, null, command.MessageKind, command.Message, now,
            decisionId: command.DecisionId, payloadJson: command.MessagePayloadJson);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
    }

    private static async Task ReleaseLeaseAsync(FactoryDbContext db, Guid threadId, CancellationToken ct)
    {
        var leases = await db.Leases.Where(l => l.ThreadId == threadId).ToListAsync(ct);
        db.Leases.RemoveRange(leases);
    }

    public async Task<ThreadMessage> AddFactoryMessageAsync(
        Guid threadId, MessageKind kind, string text, string? payloadJson, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var thread = await LockThreadAsync(write.Db, threadId, ct);
        var message = AddMessage(write.Db, thread, MessageAuthor.Factory, null, kind, text, now, payloadJson: payloadJson);
        thread.UpdatedAt = now;
        await write.CommitAsync(ct);
        return message;
    }

    public async Task<IReadOnlyList<ClaimedRun>> ListRunningAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var runs = await db.Runs.AsNoTracking().Where(r => r.Status == RunStatus.Running).ToListAsync(ct);
        var claimed = new List<ClaimedRun>();
        foreach (var run in runs)
        {
            var thread = await db.Threads.AsNoTracking().FirstAsync(t => t.Id == run.ThreadId, ct);
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == thread.ProjectId, ct);
            claimed.Add(new ClaimedRun(run, thread, project));
        }

        return claimed;
    }

    // ---- Evidence ----

    public async Task SaveVerificationAsync(VerificationRecord record, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (record.Id == Guid.Empty)
            record.Id = Guid.NewGuid();
        db.Verifications.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<VerificationRecord?> FindBaselineAsync(Guid projectId, string commit, int profileRevision, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Verifications.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Kind == VerificationKind.Baseline && v.Commit == commit && v.ProfileRevision == profileRevision)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveFindingsAsync(Guid runId, IReadOnlyList<ReviewFinding> findings, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.ReviewFindings.RemoveRange(await db.ReviewFindings.Where(f => f.RunId == runId).ToListAsync(ct));
        db.ReviewFindings.AddRange(findings.Select(f => new ReviewFindingRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Severity = f.Severity,
            File = f.File,
            Line = f.Line,
            Text = f.Text,
        }));
        await db.SaveChangesAsync(ct);
    }

    public async Task SetFindingsStatusAsync(Guid runId, FindingSeverity severity, FindingStatus status, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        foreach (var finding in await db.ReviewFindings.Where(f => f.RunId == runId && f.Severity == severity).ToListAsync(ct))
            finding.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveHandoffAsync(HandoffRecord handoff, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, handoff.ThreadId, ct);
        if (handoff.Id == Guid.Empty)
            handoff.Id = Guid.NewGuid();
        db.Handoffs.Add(handoff);

        thread.PullRequestNumber = handoff.PullRequestNumber ?? thread.PullRequestNumber;
        thread.PullRequestUrl = handoff.PullRequestUrl ?? thread.PullRequestUrl;
        await write.CommitAsync(ct);
    }

    // ---- Budget ----

    public async Task<ReservationResult> ReserveAsync(ReserveCommand command, BudgetPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, command.ThreadId, ct);

        // A request key seen before is the same call arriving twice: it is not reserved twice.
        var existing = await db.Usage.AsNoTracking().FirstOrDefaultAsync(u => u.RequestKey == command.RequestKey, ct);
        if (existing is not null)
            return new ReservationResult(new Admitted(existing.Reserved, policy.OutputAllowanceTokens), AlreadyKnown: true);

        var expectedInputCharge = BudgetLedger.ExpectedInputCharge(command.EstimatedInput, command.ExpectedCachedInput, policy.CachedInputWeight);
        var decision = BudgetLedger.Admit(new BudgetSnapshot(thread.BudgetCap, thread.TokensUsed, thread.TokensReserved), expectedInputCharge, policy);
        if (decision is not Admitted admitted)
            return new ReservationResult(decision, AlreadyKnown: false);

        thread.TokensReserved += admitted.Reserved;
        db.Usage.Add(new UsageEntry
        {
            Id = Guid.NewGuid(),
            RequestKey = command.RequestKey,
            ThreadId = command.ThreadId,
            RunId = command.RunId,
            UserId = command.UserId,
            Provider = command.Provider,
            Model = command.Model,
            EstimatedInputRaw = command.EstimatedInputRaw,
            Phase = command.Phase,
            EstimatedInput = command.EstimatedInput,
            Reserved = admitted.Reserved,
            CreatedAt = now,
        });
        Touch(db, thread, now, EventTypes.UsageChanged);
        await write.CommitAsync(ct);
        return new ReservationResult(admitted, AlreadyKnown: false);
    }

    public async Task SettleAsync(string requestKey, UsageInfo usage, long charge, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var threadId = await db.Usage.Where(u => u.RequestKey == requestKey).Select(u => (Guid?)u.ThreadId).FirstOrDefaultAsync(ct)
            ?? throw new StoreNotFoundException("No reservation has this request key.");
        var thread = await LockThreadAsync(db, threadId, ct);
        var entry = await db.Usage.FirstAsync(u => u.RequestKey == requestKey, ct);

        // A repeated settlement must not charge twice.
        if (entry.Status == UsageStatus.Settled)
            return;

        var after = BudgetLedger.Settle(new BudgetSnapshot(thread.BudgetCap, thread.TokensUsed, thread.TokensReserved), entry.Reserved, charge);
        thread.TokensUsed = after.TaskUsed;
        thread.TokensReserved = after.TaskReserved;

        entry.Status = UsageStatus.Settled;
        entry.ActualInput = usage.TotalInputTokens;
        entry.ActualCachedInput = usage.CacheCreationInputTokens + usage.CacheReadInputTokens;
        entry.ActualOutput = usage.OutputTokens;
        entry.ActualReasoning = usage.ReasoningTokens;
        entry.ServedBy = usage.ServedBy is { Length: > 0 } servedBy ? servedBy[..Math.Min(servedBy.Length, 100)] : entry.Provider;
        entry.Charged = charge;
        entry.SettledAt = now;
        Touch(db, thread, now, EventTypes.UsageChanged);
        await write.CommitAsync(ct);
    }

    public async Task MarkUsageUnknownAsync(string requestKey, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var entry = await db.Usage.FirstOrDefaultAsync(u => u.RequestKey == requestKey, ct)
            ?? throw new StoreNotFoundException("No reservation has this request key.");

        // The thread's reserved total is deliberately untouched: the reservation stays charged
        // until it is reconciled, and is never silently refunded (§9.2).
        if (entry.Status == UsageStatus.Reserved)
        {
            entry.Status = UsageStatus.Unknown;
            await db.SaveChangesAsync(ct);
        }
    }

    public Task ReleaseReservationAsync(string requestKey, CancellationToken ct) =>
        SettleAsync(requestKey, new UsageInfo(0, 0), charge: 0, DateTimeOffset.UtcNow, ct);

    public async Task<int> ReconcileUsageAsync(Guid? runId, DateTimeOffset now, CancellationToken ct)
    {
        // For one run: what the gateway marked Unknown. For a starting host: also what a previous
        // host left Reserved, which it can no longer settle.
        System.Linq.Expressions.Expression<Func<UsageEntry, bool>> unreported = runId is { } id
            ? u => u.RunId == id && u.Status == UsageStatus.Unknown
            : u => u.Status == UsageStatus.Unknown || u.Status == UsageStatus.Reserved;

        List<Guid> threadIds;
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
            threadIds = await db.Usage.AsNoTracking().Where(unreported).Select(u => u.ThreadId).Distinct().ToListAsync(ct);

        var reconciled = 0;
        foreach (var threadId in threadIds)
        {
            await using var write = await BeginWriteAsync(ct);
            var thread = await LockThreadAsync(write.Db, threadId, ct);
            var entries = await write.Db.Usage.Where(unreported).Where(u => u.ThreadId == threadId).ToListAsync(ct);
            foreach (var entry in entries)
            {
                var charge = BudgetLedger.ReconciledCharge(entry.EstimatedInput, entry.Reserved);
                var after = BudgetLedger.Settle(new BudgetSnapshot(thread.BudgetCap, thread.TokensUsed, thread.TokensReserved), entry.Reserved, charge);
                thread.TokensUsed = after.TaskUsed;
                thread.TokensReserved = after.TaskReserved;

                entry.Status = UsageStatus.Estimated;
                entry.Charged = charge;
                entry.SettledAt = now;
                reconciled++;
            }

            if (entries.Count > 0)
                Touch(write.Db, thread, now, EventTypes.UsageChanged);
            await write.CommitAsync(ct);
        }

        return reconciled;
    }

    public async Task<IReadOnlyList<UsageEntry>> ListUsageAsync(Guid threadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Usage.AsNoTracking().Where(u => u.ThreadId == threadId).OrderBy(u => u.CreatedAt).ToListAsync(ct);
    }

    // ---- Events ----

    public async Task<IReadOnlyList<OutboxEvent>> ReadEventsAsync(Guid threadId, long afterSequence, int limit, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Outbox.AsNoTracking()
            .Where(e => e.ThreadId == threadId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<long> LastEventSequenceAsync(Guid threadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Outbox.AsNoTracking()
            .Where(e => e.ThreadId == threadId)
            .OrderByDescending(e => e.Sequence)
            .Select(e => e.Sequence)
            .FirstOrDefaultAsync(ct);
    }

    // ---- Shared ----

    private static ThreadMessage AddMessage(
        FactoryDbContext db, TaskThread thread, MessageAuthor author, Guid? userId, MessageKind kind, string text, DateTimeOffset now,
        string? dispatchKey = null, Guid? decisionId = null, string? payloadJson = null)
    {
        var message = new ThreadMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Author = author,
            AuthorUserId = userId,
            Kind = kind,
            Text = text,
            Sequence = thread.NextMessageSequence++,
            DispatchKey = dispatchKey,
            DecisionId = decisionId,
            PayloadJson = payloadJson,
            CreatedAt = now,
        };
        db.Messages.Add(message);
        db.Outbox.Add(new OutboxEvent
        {
            ThreadId = thread.Id,
            ProjectId = thread.ProjectId,
            Type = EventTypes.MessageAdded,
            PayloadJson = JsonSerializer.Serialize(
                new { message.Id, message.Sequence, Author = author.ToString(), Kind = kind.ToString(), message.Text, message.DecisionId, Payload = payloadJson },
                FactoryWire.Json),
            CreatedAt = now,
        });
        return message;
    }

    /// <summary>Records that the thread changed, with the event a client needs to redraw it.</summary>
    private static void Touch(FactoryDbContext db, TaskThread thread, DateTimeOffset now, string eventType = EventTypes.StateChanged)
    {
        thread.Revision++;
        thread.UpdatedAt = now;
        db.Outbox.Add(new OutboxEvent
        {
            ThreadId = thread.Id,
            ProjectId = thread.ProjectId,
            Type = eventType,
            PayloadJson = JsonSerializer.Serialize(
                new
                {
                    State = thread.State.ToString(),
                    Stage = thread.Stage.ToString(),
                    Reason = thread.StateReason,
                    thread.Revision,
                    thread.TokensUsed,
                    thread.TokensReserved,
                    thread.BudgetCap,
                    thread.Branch,
                    thread.PullRequestUrl,
                },
                FactoryWire.Json),
            CreatedAt = now,
        });
    }

    private async Task<TaskThread> LockThreadAsync(FactoryDbContext db, Guid threadId, CancellationToken ct)
    {
        var thread = db.Database.IsNpgsql()
            ? await db.Threads.FromSqlInterpolated($"SELECT * FROM task_threads WHERE \"Id\" = {threadId} FOR UPDATE").FirstOrDefaultAsync(ct)
            : await db.Threads.FirstOrDefaultAsync(t => t.Id == threadId, ct);
        return thread ?? throw new StoreNotFoundException("The thread does not exist.");
    }

    private async Task<WriteScope> BeginWriteAsync(CancellationToken ct, bool claim = false)
    {
        var db = await contextFactory.CreateDbContextAsync(ct);
        var postgres = db.Database.IsNpgsql();
        if (!postgres)
            await FallbackWriteLock.WaitAsync(ct);

        try
        {
            var transaction = await db.Database.BeginTransactionAsync(ct);
            if (claim && postgres)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", ct);
            return new WriteScope(db, transaction, postgres ? null : FallbackWriteLock);
        }
        catch
        {
            if (!postgres)
                FallbackWriteLock.Release();
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>One write transaction. Disposing without committing rolls it back.</summary>
    private sealed class WriteScope(FactoryDbContext db, IDbContextTransaction transaction, SemaphoreSlim? heldLock) : IAsyncDisposable
    {
        public FactoryDbContext Db => db;

        public async Task CommitAsync(CancellationToken ct)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await db.DisposeAsync();
            heldLock?.Release();
        }
    }
}
