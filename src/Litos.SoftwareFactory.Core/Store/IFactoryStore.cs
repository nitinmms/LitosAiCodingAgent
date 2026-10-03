using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Store;

/// <summary>A user action arrived for a thread that has moved on since the user last saw it
/// (§14 transaction rule 4), or is not legal in the thread's state.</summary>
public sealed class StoreConflictException(string message) : Exception(message);

public sealed class StoreNotFoundException(string message) : Exception(message);

public enum DispatchOutcome
{
    /// <summary>A run was created (or a rework run, after testing) and queued.</summary>
    Queued,

    /// <summary>A run is already active: the message is a follow-up instruction for it.</summary>
    FollowUp,

    /// <summary>This message id was dispatched before; nothing was done again.</summary>
    Duplicate,

    /// <summary>The thread cannot take work in its current state. The message was not recorded.</summary>
    Rejected,
}

public sealed record DispatchResult(DispatchOutcome Outcome, TaskThread Thread, Guid? RunId, string? Reason = null);

public sealed record ClaimedRun(TaskRun Run, TaskThread Thread, Project Project);

/// <summary>Everything needed to stop a run, applied in one transaction.</summary>
public sealed record StopRunCommand(Guid RunId, LifecycleTrigger Trigger, StopReason Reason, string Message)
{
    /// <summary>The orchestrator's state at the stop, so the run can be resumed from it.</summary>
    public string? StateJson { get; init; }

    public Stage Stage { get; init; } = Stage.Implement;

    /// <summary>True when the working copy is clean and the branch pushed, or the run is over:
    /// another thread may then use the repository.</summary>
    public bool ReleaseLease { get; init; }

    public MessageKind MessageKind { get; init; } = MessageKind.Status;

    public string? MessagePayloadJson { get; init; }

    public Guid? DecisionId { get; init; }
}

public sealed record ReserveCommand(
    string RequestKey, Guid ThreadId, Guid? RunId, Guid UserId, string Provider, string Model,
    long EstimatedInputRaw, long EstimatedInput)
{
    /// <summary>How much of EstimatedInput the provider is expected to serve from its cache;
    /// that part is reserved at the cached weight.</summary>
    public long ExpectedCachedInput { get; init; }

    /// <summary>The turn the call is made in; see UsageEntry.Phase.</summary>
    public string? Phase { get; init; }
}

/// <summary>The outcome of asking to send one model call.</summary>
public sealed record ReservationResult(AdmissionDecision Decision, bool AlreadyKnown);

/// <param name="HeldLease">The thread still holds its repository: the withdrawn run's edits are
/// in the working copy and no other thread can have touched it.</param>
public sealed record WithdrawResult(TaskThread Thread, Project Project, bool HeldLease);

public sealed record ThreadDetails(
    TaskThread Thread, Project Project, IReadOnlyList<ThreadMessage> Messages, IReadOnlyList<Decision> Decisions,
    TaskRun? LatestRun, HandoffRecord? LatestHandoff, VerificationRecord? LatestVerification,
    IReadOnlyList<ReviewFindingRecord> Findings);

/// <summary>
/// The factory's durable state (docs/software-factory/m1-architecture.md §4 ports, §7 schema).
/// Every method is one transaction: a state change, its message and its outbox event are
/// committed together or not at all, and no transaction is ever held open while a worker, git
/// or a build command runs (§14 transaction rules).
/// </summary>
public interface IFactoryStore
{
    // ---- Projects and threads ----

    Task<Project> AddProjectAsync(Project project, CancellationToken ct);

    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct);

    Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct);

    Task<TaskThread> AddThreadAsync(TaskThread thread, CancellationToken ct);

    Task<IReadOnlyList<TaskThread>> ListThreadsAsync(Guid? projectId, CancellationToken ct);

    Task<ThreadDetails?> GetThreadAsync(Guid threadId, CancellationToken ct);

    Task<TaskRun?> GetRunAsync(Guid runId, CancellationToken ct);

    // ---- Dispatch and user actions ----

    /// <summary>
    /// Records an @factory message and what it starts. The message id is the idempotency key, so
    /// a double-click or a network retry creates one assignment. One active run per thread: a
    /// mention during execution becomes a follow-up instruction, not a second run.
    /// </summary>
    /// <param name="reworkTopUpShare">For a change request after a handoff, the share of the task's
    /// original cap added to its budget (<see cref="Budget.BudgetPolicy.ReworkTopUpShare"/>).</param>
    Task<DispatchResult> DispatchAsync(
        Guid threadId, Guid userId, string dispatchKey, string text, DateTimeOffset now, CancellationToken ct, double reworkTopUpShare = 0);

    /// <summary>Accept, pause, resume, cancel, raise-and-resume, resolve a blocker or recover:
    /// a transition a person asked for. Throws <see cref="StoreConflictException"/> when it is
    /// not legal from the thread's state.</summary>
    Task<TaskThread> ApplyUserActionAsync(Guid threadId, Guid userId, LifecycleTrigger trigger, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Takes back a change request: the thread's unfinished rework run is ended as Withdrawn and
    /// the thread returns to awaiting human testing on its last handoff. Throws
    /// <see cref="StoreConflictException"/> when the run is executing, when the thread's run is
    /// not a rework run, or when there is no handoff to return to. The repository lease, if the
    /// thread holds one, is kept so the caller can clean the working copy before releasing it.
    /// </summary>
    Task<WithdrawResult> WithdrawChangesAsync(Guid threadId, Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Lets another thread use the repository. Does nothing when the thread holds no lease.</summary>
    Task ReleaseLeaseAsync(Guid threadId, CancellationToken ct);

    /// <summary>Changes the task's cap. Raising a cap changes the maximum, not the accounting history.</summary>
    Task<TaskThread> SetBudgetCapAsync(Guid threadId, long? cap, DateTimeOffset now, CancellationToken ct);

    Task<Decision> OpenDecisionAsync(Guid runId, DecisionSubmission submission, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records the answer and re-queues the run so the same work continues with it.</summary>
    Task<TaskThread> AnswerDecisionAsync(Guid decisionId, Guid userId, string answer, DateTimeOffset now, CancellationToken ct);

    // ---- The coordinator ----

    /// <summary>
    /// Claims the oldest queued run that can start: under the global slot cap, and with no other
    /// thread holding its repository. Taking the lease, moving the thread to Running and
    /// recording the event happen in one serialized transaction.
    /// </summary>
    Task<ClaimedRun?> ClaimNextRunAsync(int slotCap, DateTimeOffset now, CancellationToken ct);

    /// <summary>Saves the orchestrator's state after a step, with the stage the task is now in.</summary>
    Task SaveCheckpointAsync(Guid runId, string stateJson, Stage stage, DateTimeOffset now, CancellationToken ct);

    Task SetRunWorkerAsync(Guid runId, int? processId, DateTimeOffset? startTime, CancellationToken ct);

    Task SetRunCommitsAsync(Guid runId, string? baselineCommit, string? headCommit, string? reviewSessionId, CancellationToken ct);

    Task SetThreadBranchAsync(Guid threadId, string branch, string baseCommit, CancellationToken ct);

    Task StopRunAsync(StopRunCommand command, DateTimeOffset now, CancellationToken ct);

    Task<ThreadMessage> AddFactoryMessageAsync(
        Guid threadId, MessageKind kind, string text, string? payloadJson, DateTimeOffset now, CancellationToken ct);

    /// <summary>Runs still marked Running, for the startup check that their workers are alive.</summary>
    Task<IReadOnlyList<ClaimedRun>> ListRunningAsync(CancellationToken ct);

    // ---- Evidence ----

    Task SaveVerificationAsync(VerificationRecord record, CancellationToken ct);

    Task<VerificationRecord?> FindBaselineAsync(Guid projectId, string commit, int profileRevision, CancellationToken ct);

    /// <summary>Replaces the run's findings with those the review reported.</summary>
    Task SaveFindingsAsync(Guid runId, IReadOnlyList<ReviewFinding> findings, CancellationToken ct);

    Task SetFindingsStatusAsync(Guid runId, FindingSeverity severity, FindingStatus status, CancellationToken ct);

    Task SaveHandoffAsync(HandoffRecord handoff, CancellationToken ct);

    // ---- Budget ----

    /// <summary>
    /// Admits one model call: reads the task's used tokens and open reservations under the
    /// thread's budget lock, and records the reservation if it fits (§9.2). A request key seen
    /// before returns its earlier decision without reserving again.
    /// </summary>
    Task<ReservationResult> ReserveAsync(ReserveCommand command, BudgetPolicy policy, DateTimeOffset now, CancellationToken ct);

    /// <summary>Replaces a reservation with what the call is charged.</summary>
    Task SettleAsync(string requestKey, UsageInfo usage, long charge, DateTimeOffset now, CancellationToken ct);

    /// <summary>The call ended without reported usage: the reservation stays charged.</summary>
    Task MarkUsageUnknownAsync(string requestKey, CancellationToken ct);

    /// <summary>The call was never sent: the reservation is returned in full.</summary>
    Task ReleaseReservationAsync(string requestKey, CancellationToken ct);

    /// <summary>
    /// Reconciles calls whose usage will never be reported: each is charged
    /// <see cref="BudgetLedger.ReconciledCharge"/> and the rest of its reservation is released.
    /// Call it only when the calls cannot still be in flight. With a run id, that run's Unknown
    /// calls — for when the run has stopped. With none, every Unknown call and every call still
    /// marked Reserved — for host startup, when no call from a previous host can be in flight.
    /// Returns the number of calls reconciled.
    /// </summary>
    Task<int> ReconcileUsageAsync(Guid? runId, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<UsageEntry>> ListUsageAsync(Guid threadId, CancellationToken ct);

    // ---- Events ----

    /// <summary>Events for a thread after a sequence number, oldest first.</summary>
    Task<IReadOnlyList<OutboxEvent>> ReadEventsAsync(Guid threadId, long afterSequence, int limit, CancellationToken ct);

    /// <summary>The sequence number of the thread's newest event, or 0 when it has none: where a
    /// client that has just loaded the thread starts listening from.</summary>
    Task<long> LastEventSequenceAsync(Guid threadId, CancellationToken ct);
}
