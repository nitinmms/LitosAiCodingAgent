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
        // Whoever registers a project belongs to it.
        db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = project.CreatedBy, CreatedBy = project.CreatedBy, CreatedAt = project.CreatedAt });
        Audit(db, project.CreatedBy, AuditActions.ProjectRegister, AuditTargets.Project, project.Id, project.Id,
            new { project.Name, GitHub = $"{project.GitHubOwner}/{project.GitHubRepository}", project.DefaultBranch }, project.CreatedAt);
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
        Audit(db, thread.OwnerId, AuditActions.ThreadCreate, AuditTargets.Thread, thread.Id, thread.ProjectId,
            new { thread.Title, thread.TypeLabel, thread.BudgetCap }, thread.CreatedAt);
        // Announced, so every board showing the project sees the new card.
        Touch(db, thread, thread.CreatedAt);
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
        var latestRun = await db.Runs.AsNoTracking().Where(r => r.ThreadId == threadId && r.Kind != RunKind.Chat)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var chatRun = await db.Runs.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ThreadId == threadId && r.Kind == RunKind.Chat && r.Status != RunStatus.Finished, ct);
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

        ChatProgress? chatProgress = null;
        if (chatRun is not null)
        {
            var reported = await db.Outbox.AsNoTracking()
                .Where(e => e.ThreadId == threadId && e.Type == EventTypes.ChatProgress)
                .OrderByDescending(e => e.Sequence).Select(e => e.PayloadJson).FirstOrDefaultAsync(ct);
            chatProgress = reported is null ? null : JsonSerializer.Deserialize<ChatProgress>(reported, FactoryWire.Json);
            if (chatProgress?.RunId != chatRun.Id)
                chatProgress = null;
        }

        var latestSpec = await db.Specifications.AsNoTracking().Where(s => s.ThreadId == threadId)
            .OrderByDescending(s => s.Revision).FirstOrDefaultAsync(ct);
        var taskRequest = await db.Runs.AsNoTracking().Where(r => r.ThreadId == threadId && r.Kind == RunKind.Implement)
            .OrderBy(r => r.CreatedAt).Select(r => r.Request).FirstOrDefaultAsync(ct);

        return new ThreadDetails(thread, project, messages, decisions, latestRun, handoff, verification, findings)
        {
            ChatRun = chatRun,
            ChatProgress = chatProgress,
            LatestSpec = latestSpec,
            TaskRequest = taskRequest,
        };
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
        string? action = null;
        long topUp = 0;
        // A proposed specification is approved before the work is delegated against it (§5).
        var spec = await db.Specifications.AsNoTracking().Where(s => s.ThreadId == threadId)
            .OrderByDescending(s => s.Revision).FirstOrDefaultAsync(ct);
        if (thread.State == LifecycleState.Draft && spec is { ApprovedAt: null })
            return new DispatchResult(DispatchOutcome.Rejected, thread, null, SpecAwaitsApproval(spec.Revision));

        switch (thread.State)
        {
            case LifecycleState.Draft:
                thread.State = TaskLifecycle.Apply(thread.State, LifecycleTrigger.Delegate);
                runId = AddRun(db, thread, userId, RunKind.Implement, text, now);
                outcome = DispatchOutcome.Queued;
                action = AuditActions.ThreadDelegate;
                break;

            case LifecycleState.AwaitingHumanTesting:
                // The tester asks for changes: a new run on the same branch, with its own limits.
                thread.State = TaskLifecycle.Apply(thread.State, LifecycleTrigger.RequestChanges);
                runId = AddRun(db, thread, userId, RunKind.Rework, text, now);
                outcome = DispatchOutcome.Queued;
                action = AuditActions.ThreadChangeRequest;

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

            // The run builds the newest approved revision, fixed now: a later revision is for later work.
            var approved = await db.Specifications.AsNoTracking().Where(s => s.ThreadId == threadId && s.ApprovedAt != null)
                .OrderByDescending(s => s.Revision).Select(s => (int?)s.Revision).FirstOrDefaultAsync(ct);
            db.Runs.Local.First(r => r.Id == runId).SpecificationRevision = approved;

            // A follow-up is only a message, and the message records its author.
            Audit(db, userId, action!, AuditTargets.Thread, threadId, thread.ProjectId, new { RunId = runId, BudgetTopUp = topUp }, now);
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
        Enqueue(run, now);
        db.Runs.Add(run);
        return run.Id;
    }

    /// <summary>Puts a run in the queue. The claim takes runs in the order they last entered it.</summary>
    private static void Enqueue(TaskRun run, DateTimeOffset now)
    {
        run.Status = RunStatus.Queued;
        run.QueuedAt = now;
    }

    private static async Task<Guid?> ActiveRunIdAsync(FactoryDbContext db, Guid threadId, CancellationToken ct) =>
        (await ActiveRunAsync(db, threadId, ct))?.Id;

    /// <summary>The task's own run that is not finished. A chat run is never it.</summary>
    private static Task<TaskRun?> ActiveRunAsync(FactoryDbContext db, Guid threadId, CancellationToken ct) =>
        db.Runs.FirstOrDefaultAsync(r => r.ThreadId == threadId && r.Status != RunStatus.Finished && r.Kind != RunKind.Chat, ct);

    private static string SpecAwaitsApproval(int revision) =>
        $"Specification revision {revision} is waiting for approval. Approve it, or ask for changes with @factory spec, before delegating the work.";

    public async Task<DispatchResult> RequestSpecAsync(
        Guid threadId, Guid userId, string dispatchKey, string request, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);

        if (await db.Messages.AsNoTracking().AnyAsync(m => m.DispatchKey == dispatchKey, ct))
            return new DispatchResult(DispatchOutcome.Duplicate, thread, await ActiveRunIdAsync(db, threadId, ct));
        if (thread.State != LifecycleState.Draft)
        {
            return new DispatchResult(
                DispatchOutcome.Rejected, thread, null,
                "A specification is written before the work starts. This task has already been delegated.");
        }

        thread.State = TaskLifecycle.Apply(thread.State, LifecycleTrigger.Delegate);
        thread.Stage = Stage.Spec;
        thread.StateReason = null;
        var runId = AddRun(db, thread, userId, RunKind.Spec, request, now);
        Audit(db, userId, AuditActions.ThreadSpecRequest, AuditTargets.Thread, threadId, thread.ProjectId, new { RunId = runId }, now);
        // Shown as typed: the client puts the mention in front of an @factory message.
        AddMessage(db, thread, MessageAuthor.User, userId, MessageKind.Text, $"spec {request}", now, dispatchKey: dispatchKey);
        Touch(db, thread, now);
        try
        {
            await write.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            return new DispatchResult(DispatchOutcome.Duplicate, thread, null);
        }

        return new DispatchResult(DispatchOutcome.Queued, thread, runId);
    }

    public async Task<Specification> ProposeSpecAsync(Guid runId, SpecSubmission submission, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new StoreNotFoundException("The run does not exist.");
        var thread = await LockThreadAsync(db, run.ThreadId, ct);
        if (run.Kind != RunKind.Spec || run.Status != RunStatus.Running || thread.State != LifecycleState.Running)
            throw new StoreConflictException("Only a running spec run can propose a specification.");

        var revision = (await db.Specifications.Where(s => s.ThreadId == thread.Id).MaxAsync(s => (int?)s.Revision, ct) ?? 0) + 1;
        var spec = new Specification
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            RunId = run.Id,
            Revision = revision,
            Summary = submission.Summary.Trim(),
            AcceptanceCriteriaJson = JsonSerializer.Serialize(Clean(submission.AcceptanceCriteria), FactoryWire.Json),
            AffectedAreasJson = JsonSerializer.Serialize(Clean(submission.AffectedAreas), FactoryWire.Json),
            TestPlan = submission.TestPlan.Trim(),
            OpenQuestionsJson = JsonSerializer.Serialize(Clean(submission.OpenQuestions), FactoryWire.Json),
            CreatedAt = now,
        };
        db.Specifications.Add(spec);

        run.Status = RunStatus.Finished;
        run.EndedAt = now;
        run.WorkerProcessId = null;
        run.WorkerStartTime = null;
        thread.State = TaskLifecycle.Apply(thread.State, LifecycleTrigger.ProposeSpec);
        thread.Stage = Stage.Spec;
        thread.StateReason = null;

        AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Spec, spec.Summary, now, payloadJson: SpecPayload(spec));
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return spec;

        static List<string> Clean(IReadOnlyList<string> items) => [.. items.Select(i => i.Trim()).Where(i => i.Length > 0)];
    }

    /// <summary>A Spec message's payload: the revision in full, as the card shows it.</summary>
    private static string SpecPayload(Specification spec) => JsonSerializer.Serialize(
        new
        {
            spec.Revision,
            spec.Summary,
            AcceptanceCriteria = JsonSerializer.Deserialize<List<string>>(spec.AcceptanceCriteriaJson, FactoryWire.Json),
            AffectedAreas = JsonSerializer.Deserialize<List<string>>(spec.AffectedAreasJson, FactoryWire.Json),
            spec.TestPlan,
            OpenQuestions = JsonSerializer.Deserialize<List<string>>(spec.OpenQuestionsJson, FactoryWire.Json),
        },
        FactoryWire.Json);

    public async Task<Specification> ApproveSpecAsync(Guid threadId, int revision, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);
        var spec = await db.Specifications.FirstOrDefaultAsync(s => s.ThreadId == threadId && s.Revision == revision, ct)
            ?? throw new StoreNotFoundException("The thread has no such specification revision.");
        if (spec.ApprovedAt is not null)
            return spec;

        var newest = await db.Specifications.Where(s => s.ThreadId == threadId).MaxAsync(s => s.Revision, ct);
        if (newest != revision)
            throw new StoreConflictException($"Revision {revision} has been replaced by revision {newest}. Read that one before approving.");
        if (thread.State != LifecycleState.Draft || thread.Stage != Stage.Spec)
            throw new StoreConflictException("A specification is approved before the work is delegated.");

        spec.ApprovedBy = userId;
        spec.ApprovedAt = now;
        Audit(db, userId, AuditActions.SpecApprove, AuditTargets.Thread, threadId, thread.ProjectId, new { Revision = revision }, now);
        AddMessage(
            db, thread, MessageAuthor.User, userId, MessageKind.Status,
            $"Approved specification revision {revision}. @factory now builds it.", now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return spec;
    }

    /// <summary>Where a plain message can start a chat (m2-architecture.md §5).</summary>
    private static bool CanChat(LifecycleState state) =>
        state is LifecycleState.Draft or LifecycleState.AwaitingHumanTesting or LifecycleState.Accepted;

    public async Task<DispatchResult> ChatAsync(
        Guid threadId, Guid userId, string dispatchKey, string text, long chatTurnCap, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var thread = await LockThreadAsync(db, threadId, ct);

        if (await db.Messages.AsNoTracking().AnyAsync(m => m.DispatchKey == dispatchKey, ct))
            return new DispatchResult(DispatchOutcome.Duplicate, thread, null);

        Guid? runId;
        DispatchOutcome outcome;
        if (thread.State is LifecycleState.Queued or LifecycleState.Running)
        {
            // Said to a task that is working: guidance for the agent, as an @factory follow-up is.
            runId = await ActiveRunIdAsync(db, threadId, ct);
            outcome = DispatchOutcome.FollowUp;
        }
        else if (!CanChat(thread.State))
        {
            return new DispatchResult(DispatchOutcome.Rejected, thread, null, RejectionReason(thread.State));
        }
        else if (await db.Runs.AnyAsync(r => r.ThreadId == threadId && r.Kind == RunKind.Chat && r.Status != RunStatus.Finished, ct))
        {
            return new DispatchResult(DispatchOutcome.Rejected, thread, null, "Litos is still answering your last message. Wait for the reply, then send this.");
        }
        else
        {
            runId = AddRun(db, thread, userId, RunKind.Chat, text, now);
            var run = db.Runs.Local.First(r => r.Id == runId);
            run.ChatBudgetCap = chatTurnCap;
            outcome = DispatchOutcome.Chat;
        }

        // Marked, because an @factory message is stored without its mention: the client shows
        // which of the two a person sent.
        AddMessage(db, thread, MessageAuthor.User, userId, MessageKind.Text, text, now, dispatchKey: dispatchKey, payloadJson: PlainMessagePayload);
        try
        {
            await write.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            return new DispatchResult(DispatchOutcome.Duplicate, thread, null);
        }

        return new DispatchResult(outcome, thread, runId);
    }

    /// <summary>The payload of a message sent without @factory.</summary>
    public const string PlainMessagePayload = """{"plain":true}""";

    public async Task AnnounceChatProgressAsync(ChatProgress progress, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == progress.RunId && r.Kind == RunKind.Chat, ct)
            ?? throw new StoreNotFoundException("The chat run does not exist.");
        // The thread is locked so the report cannot land after the reply that finishes the run.
        var thread = await LockThreadAsync(db, run.ThreadId, ct);
        if (await db.Runs.AnyAsync(r => r.Id == run.Id && r.Status == RunStatus.Finished, ct))
            return;

        db.Outbox.Add(new OutboxEvent
        {
            ThreadId = thread.Id,
            ProjectId = thread.ProjectId,
            Type = EventTypes.ChatProgress,
            PayloadJson = JsonSerializer.Serialize(progress, FactoryWire.Json),
            CreatedAt = now,
        });
        await write.CommitAsync(ct);
    }

    public async Task FinishChatRunAsync(Guid runId, string? reply, string? failure, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId && r.Kind == RunKind.Chat, ct)
            ?? throw new StoreNotFoundException("The chat run does not exist.");
        var thread = await LockThreadAsync(db, run.ThreadId, ct);
        if (run.Status == RunStatus.Finished)
            return;

        run.Status = RunStatus.Finished;
        run.EndedAt = now;
        run.WorkerProcessId = null;
        run.WorkerStartTime = null;
        if (!string.IsNullOrWhiteSpace(reply))
            AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Text, reply.Trim(), now);
        else
            AddMessage(db, thread, MessageAuthor.Factory, null, MessageKind.Status, failure ?? "Litos could not answer. Send the message again.", now);
        await write.CommitAsync(ct);
    }

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
                Enqueue(run, now);
                note = "Queued to continue.";
                break;
        }

        var action = trigger switch
        {
            LifecycleTrigger.Accept => AuditActions.ThreadAccept,
            LifecycleTrigger.Cancel => AuditActions.ThreadCancel,
            LifecycleTrigger.Pause => AuditActions.ThreadPause,
            _ => AuditActions.ThreadResume,
        };
        Audit(db, userId, action, AuditTargets.Thread, threadId, thread.ProjectId, new { From = thread.State.ToString(), To = next.ToString(), Trigger = trigger.ToString() }, now);

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
        Audit(db, userId, AuditActions.ThreadWithdraw, AuditTargets.Thread, threadId, thread.ProjectId, new { RunId = run.Id }, now);
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

    public async Task<TaskThread> SetBudgetCapAsync(Guid threadId, long? cap, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (cap is < 0)
            throw new ArgumentOutOfRangeException(nameof(cap));

        await using var write = await BeginWriteAsync(ct);
        var thread = await LockThreadAsync(write.Db, threadId, ct);
        // M1 changed budgets without recording who; null is "no cap".
        Audit(write.Db, userId, AuditActions.ThreadBudget, AuditTargets.Thread, threadId, thread.ProjectId, new { From = thread.BudgetCap, To = cap }, now);
        thread.BudgetCap = cap;
        Touch(write.Db, thread, now, EventTypes.UsageChanged);
        await write.CommitAsync(ct);
        return thread;
    }

    public async Task<TaskThread> EditThreadAsync(
        Guid threadId, string? title, string? typeLabel, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var thread = await LockThreadAsync(write.Db, threadId, ct);
        var newTitle = title ?? thread.Title;
        var newType = typeLabel ?? thread.TypeLabel;
        if (newTitle == thread.Title && newType == thread.TypeLabel)
            return thread;

        Audit(write.Db, userId, AuditActions.ThreadEdit, AuditTargets.Thread, threadId, thread.ProjectId,
            new { From = new { thread.Title, thread.TypeLabel }, To = new { Title = newTitle, TypeLabel = newType } }, now);
        thread.Title = newTitle;
        thread.TypeLabel = newType;
        Touch(write.Db, thread, now);
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

        Enqueue(run, now);
        run.Entry = RunEntry.DecisionAnswered;
        run.EntryAnswer = answer;

        thread.State = next;
        thread.StateReason = null;
        AddMessage(db, thread, MessageAuthor.User, userId, MessageKind.DecisionAnswer, answer, now, decisionId: decisionId);
        Audit(db, userId, AuditActions.DecisionAnswer, AuditTargets.Decision, decisionId, thread.ProjectId, new { decision.Question, Answer = answer }, now);
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return thread;
    }

    // ---- The coordinator ----

    public async Task<ClaimedRun?> ClaimNextRunAsync(int slotCap, DateTimeOffset now, CancellationToken ct, IReadOnlySet<Guid>? heldRuns = null)
    {
        await using var write = await BeginWriteAsync(ct, claim: true);
        var db = write.Db;

        // The host's registry knows which runs still have an executor, including one that has
        // stopped and is still tearing down. Without it, the runs marked Running are the slots.
        var held = heldRuns ?? (await db.Runs.Where(r => r.Status == RunStatus.Running).Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        var full = held.Count >= slotCap;

        // Even when every slot is busy the queue is scanned, so each waiting thread says why.
        var queued = await db.Runs.Where(r => r.Status == RunStatus.Queued)
            .OrderBy(r => r.QueuedAt).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        foreach (var run in queued)
        {
            // Resumed while its previous executor is still tearing down: it starts once that is done.
            if (held.Contains(run.Id))
                continue;

            var thread = await LockThreadAsync(db, run.ThreadId, ct);
            if (run.Kind == RunKind.Chat)
            {
                // Read-only, on a copy of its own: it waits only for a slot, and leaves the task's
                // state and its repository alone.
                if (full)
                    continue;
                run.Status = RunStatus.Running;
                run.StartedAt ??= now;
                run.HeartbeatAt = now;
                await write.CommitAsync(ct);
                return new ClaimedRun(run, thread, await db.Projects.FirstAsync(p => p.Id == thread.ProjectId, ct));
            }

            if (thread.State != LifecycleState.Queued)
                continue;

            var project = await db.Projects.FirstAsync(p => p.Id == thread.ProjectId, ct);
            if (run.Kind == RunKind.Spec)
            {
                // Task work, so it moves the task to Running, but it reads a copy of its own and
                // takes no lease: it waits only for a slot (m2-architecture.md §5).
                if (full)
                {
                    var waiting = $"Waiting for a free slot ({held.Count} of {slotCap} busy).";
                    if (thread.StateReason != waiting)
                    {
                        thread.StateReason = waiting;
                        Touch(db, thread, now);
                    }

                    continue;
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

            var lockIdentity = LockIdentity(project);
            var lease = await db.Leases.FirstOrDefaultAsync(l => l.LockIdentity == lockIdentity, ct);

            // The queued thread says exactly what it waits for (§6.3). Its repository being held
            // is the more specific reason, so it wins over a full set of slots.
            string? waitingFor = null;
            if (lease is not null && lease.ThreadId != thread.Id)
            {
                var holder = await db.Threads.AsNoTracking().FirstAsync(t => t.Id == lease.ThreadId, ct);
                waitingFor = $"Waiting for {project.Name} — held by \"{holder.Title}\" ({Describe(holder.State)}).";
            }
            else if (full)
            {
                waitingFor = $"Waiting for a free slot ({held.Count} of {slotCap} busy).";
            }

            if (waitingFor is not null)
            {
                if (thread.StateReason != waitingFor)
                {
                    thread.StateReason = waitingFor;
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

    public async Task SaveCheckpointAsync(
        Guid runId, string stateJson, Stage stage, DateTimeOffset now, CancellationToken ct, string? workspaceSnapshotJson = null)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new StoreNotFoundException("The run does not exist.");
        var thread = await LockThreadAsync(db, run.ThreadId, ct);

        run.StateJson = stateJson;
        run.WorkspaceSnapshotJson = workspaceSnapshotJson ?? run.WorkspaceSnapshotJson;
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
        run.WorkspaceSnapshotJson = command.WorkspaceSnapshotJson ?? run.WorkspaceSnapshotJson;
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

    public async Task<string?> LastServedByAsync(Guid threadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Usage.AsNoTracking()
            .Where(u => u.ThreadId == threadId && u.Status == UsageStatus.Settled && u.ServedBy != null && u.ServedBy != u.Provider)
            .OrderByDescending(u => u.SettledAt)
            .Select(u => u.ServedBy)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ReviewFindingRecord>> ListFindingsAsync(Guid threadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ReviewFindings.AsNoTracking()
            .Join(db.Runs.Where(r => r.ThreadId == threadId), f => f.RunId, r => r.Id, (f, r) => new { Finding = f, r.CreatedAt })
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Finding)
            .ToListAsync(ct);
    }

    public async Task<TaskThread> SetFindingVerdictAsync(Guid findingId, FindingVerdict? verdict, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var threadId = await db.ReviewFindings.Where(f => f.Id == findingId)
            .Join(db.Runs, f => f.RunId, r => r.Id, (f, r) => (Guid?)r.ThreadId)
            .FirstOrDefaultAsync(ct)
            ?? throw new StoreNotFoundException("The finding does not exist.");
        var thread = await LockThreadAsync(db, threadId, ct);
        var finding = await db.ReviewFindings.FirstAsync(f => f.Id == findingId, ct);

        Audit(db, userId, AuditActions.FindingVerdict, AuditTargets.Finding, findingId, thread.ProjectId,
            new { From = finding.Verdict?.ToString(), To = verdict?.ToString() }, now);
        finding.Verdict = verdict;
        finding.VerdictBy = verdict is null ? null : userId;
        finding.VerdictAt = verdict is null ? null : now;
        Touch(db, thread, now);
        await write.CommitAsync(ct);
        return thread;
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

        var ledger = await LedgerAsync(db, thread, command.RunId, ct);
        var expectedInputCharge = BudgetLedger.ExpectedInputCharge(command.EstimatedInput, command.ExpectedCachedInput, policy.CachedInputWeight);
        var decision = BudgetLedger.Admit(ledger.Snapshot, expectedInputCharge, policy);
        if (decision is not Admitted admitted)
            return new ReservationResult(decision, AlreadyKnown: false);

        ledger.Reserve(admitted.Reserved);
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
        if (!ledger.IsChat)
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

        var ledger = await LedgerAsync(db, thread, entry.RunId, ct);
        ledger.Settle(entry.Reserved, charge);

        entry.Status = UsageStatus.Settled;
        entry.ActualInput = usage.TotalInputTokens;
        entry.ActualCachedInput = usage.CacheCreationInputTokens + usage.CacheReadInputTokens;
        entry.ActualOutput = usage.OutputTokens;
        entry.ActualReasoning = usage.ReasoningTokens;
        entry.ServedBy = usage.ServedBy is { Length: > 0 } servedBy ? servedBy[..Math.Min(servedBy.Length, 100)] : entry.Provider;
        entry.Charged = charge;
        entry.SettledAt = now;
        if (!ledger.IsChat)
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

    public async Task<int> ReconcileUsageAsync(Guid? runId, DateTimeOffset now, CancellationToken ct, bool includeInFlight = false)
    {
        // For one run: what the gateway marked Unknown, and what is still Reserved when nothing
        // can settle it any more. For a starting host: also what a previous host left Reserved.
        System.Linq.Expressions.Expression<Func<UsageEntry, bool>> unreported = runId switch
        {
            { } id when includeInFlight => u => u.RunId == id && (u.Status == UsageStatus.Unknown || u.Status == UsageStatus.Reserved),
            { } id => u => u.RunId == id && u.Status == UsageStatus.Unknown,
            null => u => u.Status == UsageStatus.Unknown || u.Status == UsageStatus.Reserved,
        };

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
                (await LedgerAsync(write.Db, thread, entry.RunId, ct)).Settle(entry.Reserved, charge);

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

    /// <summary>
    /// Whose budget a model call is admitted against and charged to: the task's, or for a chat
    /// run the run's own (§5: chat is not charged to the task budget).
    /// </summary>
    private static async Task<Ledger> LedgerAsync(FactoryDbContext db, TaskThread thread, Guid? runId, CancellationToken ct) =>
        new(thread, runId is { } id ? await db.Runs.FirstOrDefaultAsync(r => r.Id == id && r.Kind == RunKind.Chat, ct) : null);

    private sealed class Ledger(TaskThread thread, TaskRun? chat)
    {
        public bool IsChat => chat is not null;

        public BudgetSnapshot Snapshot => chat is null
            ? new BudgetSnapshot(thread.BudgetCap, thread.TokensUsed, thread.TokensReserved)
            : new BudgetSnapshot(chat.ChatBudgetCap, chat.ChatTokensUsed, chat.ChatTokensReserved);

        public void Reserve(long tokens)
        {
            if (chat is null)
                thread.TokensReserved += tokens;
            else
                chat.ChatTokensReserved += tokens;
        }

        public void Settle(long reserved, long charge)
        {
            var after = BudgetLedger.Settle(Snapshot, reserved, charge);
            if (chat is null)
            {
                thread.TokensUsed = after.TaskUsed;
                thread.TokensReserved = after.TaskReserved;
            }
            else
            {
                chat.ChatTokensUsed = after.TaskUsed;
                chat.ChatTokensReserved = after.TaskReserved;
            }
        }
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

    public async Task<IReadOnlyList<OutboxEvent>> ReadBoardEventsAsync(
        IReadOnlySet<Guid>? projectIds, long afterSequence, int limit, CancellationToken ct)
    {
        if (projectIds is { Count: 0 })
            return [];

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var events = db.Outbox.AsNoTracking()
            .Where(e => e.Sequence > afterSequence && (e.Type == EventTypes.StateChanged || e.Type == EventTypes.UsageChanged));
        if (projectIds is not null)
        {
            var ids = projectIds.ToList();
            events = events.Where(e => ids.Contains(e.ProjectId));
        }

        return await events.OrderBy(e => e.Sequence).Take(limit).ToListAsync(ct);
    }

    public async Task<long> LastEventSequenceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Outbox.AsNoTracking().OrderByDescending(e => e.Sequence).Select(e => e.Sequence).FirstOrDefaultAsync(ct);
    }

    public async Task<TaskThread?> FindThreadAsync(Guid threadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, ct);
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
                    // Whose move it is, and what the thread is called, so a client never derives them.
                    Turn = TurnLabels.For(thread.State, thread.Stage).ToString(),
                    thread.Title,
                    thread.TypeLabel,
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

    // ---- People and access ----

    public async Task<Guid?> FindProjectIdAsync(ProjectScoped kind, Guid id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return kind switch
        {
            ProjectScoped.Thread => await db.Threads.Where(t => t.Id == id).Select(t => (Guid?)t.ProjectId).FirstOrDefaultAsync(ct),
            ProjectScoped.Decision => await (
                from d in db.Decisions
                join t in db.Threads on d.ThreadId equals t.Id
                where d.Id == id
                select (Guid?)t.ProjectId).FirstOrDefaultAsync(ct),
            ProjectScoped.Finding => await (
                from f in db.ReviewFindings
                join r in db.Runs on f.RunId equals r.Id
                join t in db.Threads on r.ThreadId equals t.Id
                where f.Id == id
                select (Guid?)t.ProjectId).FirstOrDefaultAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    public async Task<IReadOnlyList<Guid>> ListMemberProjectIdsAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ProjectMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.ProjectId).ToListAsync(ct);
    }

    public async Task<bool> IsMemberAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == projectId).OrderBy(m => m.CreatedAt).ToListAsync(ct);
    }

    public async Task<bool> AddMemberAsync(Guid projectId, Guid userId, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
            throw new StoreNotFoundException("The project does not exist.");
        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct))
            return false;

        db.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = userId, CreatedBy = actorId, CreatedAt = now });
        Audit(db, actorId, AuditActions.MemberAdd, AuditTargets.User, userId, projectId, null, now);
        try
        {
            await write.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Added by someone else a moment ago: the outcome is the same.
            return false;
        }

        return true;
    }

    public async Task<bool> RemoveMemberAsync(Guid projectId, Guid userId, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var member = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (member is null)
            return false;

        db.ProjectMembers.Remove(member);
        Audit(db, actorId, AuditActions.MemberRemove, AuditTargets.User, userId, projectId, null, now);
        await write.CommitAsync(ct);
        return true;
    }

    public async Task<Invitation> AddInvitationAsync(Invitation invitation, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        if (invitation.Id == Guid.Empty)
            invitation.Id = Guid.NewGuid();
        db.Invitations.Add(invitation);
        Audit(db, invitation.CreatedBy, AuditActions.InvitationCreate, AuditTargets.Invitation, invitation.Id, null,
            new { invitation.UserName, Role = invitation.Role.ToString(), ProjectIds = ProjectIdsOf(invitation), invitation.ExpiresAt }, invitation.CreatedAt);
        await write.CommitAsync(ct);
        return invitation;
    }

    public async Task<IReadOnlyList<Invitation>> ListInvitationsAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Invitations.AsNoTracking().OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
    }

    public async Task<Invitation?> FindInvitationAsync(string tokenHash, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
    }

    public async Task RevokeInvitationAsync(Guid invitationId, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;

        // Conditional, so a revoke and an acceptance at the same moment cannot both succeed.
        var revoked = await db.Invitations
            .Where(i => i.Id == invitationId && i.AcceptedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.RevokedAt, now), ct);
        if (revoked == 0)
        {
            var invitation = await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invitationId, ct)
                ?? throw new StoreNotFoundException("The invitation does not exist.");
            throw new StoreConflictException(invitation.AcceptedAt is not null
                ? "This invitation has already been used."
                : "This invitation has already been revoked.");
        }

        Audit(db, actorId, AuditActions.InvitationRevoke, AuditTargets.Invitation, invitationId, null, null, now);
        await write.CommitAsync(ct);
    }

    public async Task<Invitation> AcceptInvitationAsync(Guid invitationId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;

        // Conditional, so only one acceptance of a link can ever succeed.
        var accepted = await db.Invitations
            .Where(i => i.Id == invitationId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.AcceptedAt, now).SetProperty(i => i.AcceptedUserId, userId), ct);
        var invitation = await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw new StoreNotFoundException("The invitation does not exist.");
        if (accepted == 0)
            throw new StoreConflictException(invitation.UnusableReason(now) ?? "This invitation cannot be used.");

        Audit(db, userId, AuditActions.InvitationAccept, AuditTargets.Invitation, invitationId, null,
            new { invitation.UserName, Role = invitation.Role.ToString() }, now);

        // A project deleted since the invitation was made is skipped, not an error.
        var wanted = ProjectIdsOf(invitation);
        var projects = await db.Projects.Where(p => wanted.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        foreach (var projectId in projects)
        {
            db.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = userId, CreatedBy = invitation.CreatedBy, CreatedAt = now });
            Audit(db, userId, AuditActions.MemberAdd, AuditTargets.User, userId, projectId, new { FromInvitation = invitationId }, now);
        }

        await write.CommitAsync(ct);
        return invitation;
    }

    public async Task AddAuditAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (auditEvent.Id == Guid.Empty)
            auditEvent.Id = Guid.NewGuid();
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAuditAsync(Guid? projectId, int limit, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var events = db.AuditEvents.AsNoTracking();
        if (projectId is { } id)
            events = events.Where(a => a.ProjectId == id);
        return await events.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(limit).ToListAsync(ct);
    }

    private static IReadOnlyList<Guid> ProjectIdsOf(Invitation invitation) =>
        JsonSerializer.Deserialize<List<Guid>>(invitation.ProjectIdsJson) ?? [];

    /// <summary>Adds an audit row to the transaction under way, so it commits with the change it records.</summary>
    private static void Audit(
        FactoryDbContext db, Guid actorId, string action, string targetType, Guid? targetId, Guid? projectId, object? details, DateTimeOffset now) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            ProjectId = projectId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details, FactoryWire.Json),
            CreatedAt = now,
        });

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
