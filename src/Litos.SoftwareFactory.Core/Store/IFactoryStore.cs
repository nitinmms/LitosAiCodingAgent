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

    /// <summary>A plain message: a read-only chat run was queued to answer it.</summary>
    Chat,
}

public sealed record DispatchResult(DispatchOutcome Outcome, TaskThread Thread, Guid? RunId, string? Reason = null);

public sealed record ClaimedRun(TaskRun Run, TaskThread Thread, Project Project);

/// <summary>The kinds of thing the API names by id that belong to a project, and so are only
/// visible to its members (m2-architecture.md §4).</summary>
public enum ProjectScoped
{
    Thread,
    Decision,
    Finding,
}

/// <summary>Everything needed to stop a run, applied in one transaction.</summary>
public sealed record StopRunCommand(Guid RunId, LifecycleTrigger Trigger, StopReason Reason, string Message)
{
    /// <summary>The orchestrator's state at the stop, so the run can be resumed from it.</summary>
    public string? StateJson { get; init; }

    /// <summary>The working copy at the stop; null keeps the last checkpoint's.</summary>
    public string? WorkspaceSnapshotJson { get; init; }

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

    /// <summary>The quotas of UserId, as the settings stood when the call was made.</summary>
    public UserQuotas Quotas { get; init; } = UserQuotas.None;
}

/// <summary>The outcome of asking to send one model call.</summary>
public sealed record ReservationResult(AdmissionDecision Decision, bool AlreadyKnown);

/// <param name="HeldLease">The thread still holds its repository: the withdrawn run's edits are
/// in the working copy and no other thread can have touched it.</param>
public sealed record WithdrawResult(TaskThread Thread, Project Project, bool HeldLease);

public sealed record ThreadDetails(
    TaskThread Thread, Project Project, IReadOnlyList<ThreadMessage> Messages, IReadOnlyList<Decision> Decisions,
    TaskRun? LatestRun, HandoffRecord? LatestHandoff, VerificationRecord? LatestVerification,
    IReadOnlyList<ReviewFindingRecord> Findings)
{
    /// <summary>A chat run still answering, if there is one. Chat runs are never the LatestRun:
    /// that is the task's own work.</summary>
    public TaskRun? ChatRun { get; init; }

    /// <summary>What <see cref="ChatRun"/> last said it was doing, if it has said anything.</summary>
    public ChatProgress? ChatProgress { get; init; }

    /// <summary>The newest specification revision, approved or not; null when none was proposed.</summary>
    public Specification? LatestSpec { get; init; }

    /// <summary>What the task was delegated to do: its first implement run's request. Not the
    /// first message, which may be a question or a request for a specification.</summary>
    public string? TaskRequest { get; init; }
}

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
    Task<TaskThread> SetBudgetCapAsync(Guid threadId, long? cap, Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// A plain message, with no @factory (m2-architecture.md §5). In Draft, after a handoff or
    /// after acceptance it queues a read-only chat run to answer it, with a budget of
    /// <paramref name="chatTurnCap"/>; while the task is queued or running it is a follow-up for
    /// the agent; in any other state it is rejected, as is a second message while one is still
    /// being answered. The dispatch key makes a retried message safe, as for @factory.
    /// </summary>
    Task<DispatchResult> ChatAsync(
        Guid threadId, Guid userId, string dispatchKey, string text, long chatTurnCap, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// An @factory spec (m2-architecture.md §5): from Draft, queues a spec run for
    /// <paramref name="request"/> and moves the task to the Spec stage. Rejected in any other state.
    /// The dispatch key makes a retried message safe.
    /// </summary>
    Task<DispatchResult> RequestSpecAsync(Guid threadId, Guid userId, string dispatchKey, string request, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// A spec run's proposal: recorded as the thread's next specification revision and posted as a
    /// Spec message; the run finishes and the task is a draft at the Spec stage again. Throws
    /// <see cref="StoreConflictException"/> when the run is not a running spec run.
    /// </summary>
    Task<Specification> ProposeSpecAsync(Guid runId, Contracts.SpecSubmission submission, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Approves <paramref name="revision"/>, which must be the newest, on a draft at the Spec stage.
    /// Approving it again does nothing. Throws <see cref="StoreConflictException"/> otherwise.
    /// </summary>
    Task<Specification> ApproveSpecAsync(Guid threadId, int revision, Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Tells the thread's stream what a chat answer in progress is doing. Does nothing
    /// once the run has finished, so a late report never follows the reply.</summary>
    Task AnnounceChatProgressAsync(ChatProgress progress, DateTimeOffset now, CancellationToken ct);

    /// <summary>Ends a chat run: posts its reply, or says why there is none.</summary>
    Task FinishChatRunAsync(Guid runId, string? reply, string? failure, DateTimeOffset now, CancellationToken ct);

    /// <summary>Renames a thread or changes its task type; null leaves that one as it is.</summary>
    Task<TaskThread> EditThreadAsync(Guid threadId, string? title, string? typeLabel, Guid userId, DateTimeOffset now, CancellationToken ct);

    Task<Decision> OpenDecisionAsync(Guid runId, DecisionSubmission submission, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records the answer and re-queues the run so the same work continues with it.</summary>
    Task<TaskThread> AnswerDecisionAsync(Guid decisionId, Guid userId, string answer, DateTimeOffset now, CancellationToken ct);

    // ---- The coordinator ----

    /// <summary>
    /// Claims the run that has waited longest in the queue and can start: under the global slot
    /// cap, not still held by the host, and with no other thread holding its repository. Taking
    /// the lease, moving the thread to Running and recording the event happen in one serialized
    /// transaction. Every queued thread that cannot start is given its waiting reason, even when
    /// all the slots are busy (§6.3).
    /// </summary>
    /// <param name="heldRuns">The runs the host still has an executor for. They fill the slots,
    /// and one that has been queued again is not claimed until its executor has finished. Null
    /// counts the runs marked Running instead.</param>
    Task<ClaimedRun?> ClaimNextRunAsync(int slotCap, DateTimeOffset now, CancellationToken ct, IReadOnlySet<Guid>? heldRuns = null);

    /// <summary>Saves the orchestrator's state after a step, with the stage the task is now in,
    /// and the working copy as it is now (null keeps the last one).</summary>
    Task SaveCheckpointAsync(
        Guid runId, string stateJson, Stage stage, DateTimeOffset now, CancellationToken ct, string? workspaceSnapshotJson = null);

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

    /// <summary>
    /// The upstream that served the thread's most recent settled call, when its provider reported
    /// one other than itself (a router's); null when none has.
    /// </summary>
    Task<string?> LastServedByAsync(Guid threadId, CancellationToken ct);

    /// <summary>Every finding of every run of the thread, oldest run first.</summary>
    Task<IReadOnlyList<ReviewFindingRecord>> ListFindingsAsync(Guid threadId, CancellationToken ct);

    /// <summary>Records a person's verdict on a finding, or clears it with null.</summary>
    /// <exception cref="StoreNotFoundException">No finding has this id.</exception>
    Task<TaskThread> SetFindingVerdictAsync(Guid findingId, FindingVerdict? verdict, Guid userId, DateTimeOffset now, CancellationToken ct);

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
    /// calls — for when the run has stopped — and, with <paramref name="includeInFlight"/>, its
    /// calls still marked Reserved, for a run that no executor owns any more. With none, every
    /// Unknown call and every call still marked Reserved — for host startup, when no call from a
    /// previous host can be in flight. Returns the number of calls reconciled.
    /// </summary>
    Task<int> ReconcileUsageAsync(Guid? runId, DateTimeOffset now, CancellationToken ct, bool includeInFlight = false);

    Task<IReadOnlyList<UsageEntry>> ListUsageAsync(Guid threadId, CancellationToken ct);

    // ---- Events ----

    /// <summary>Events for a thread after a sequence number, oldest first.</summary>
    Task<IReadOnlyList<OutboxEvent>> ReadEventsAsync(Guid threadId, long afterSequence, int limit, CancellationToken ct);

    /// <summary>The sequence number of the thread's newest event, or 0 when it has none: where a
    /// client that has just loaded the thread starts listening from.</summary>
    Task<long> LastEventSequenceAsync(Guid threadId, CancellationToken ct);

    /// <summary>
    /// The state and usage events of the given projects after a sequence number, oldest first:
    /// what a board listens to. Null projects means every project (an Admin).
    /// </summary>
    Task<IReadOnlyList<OutboxEvent>> ReadBoardEventsAsync(IReadOnlySet<Guid>? projectIds, long afterSequence, int limit, CancellationToken ct);

    /// <summary>The newest event's sequence number across every thread, or 0: where a client that
    /// has just loaded the thread list starts listening from.</summary>
    Task<long> LastEventSequenceAsync(CancellationToken ct);

    /// <summary>One thread's row alone, without its messages and records; null when there is none.</summary>
    Task<TaskThread?> FindThreadAsync(Guid threadId, CancellationToken ct);

    // ---- People and access (docs/software-factory/m2-architecture.md §4) ----
    //
    // Every change here writes its AuditEvent in the same transaction. Accounts themselves live in
    // Identity, which the host changes through its UserManager; it records those with AddAuditAsync.

    /// <summary>The project a thread, decision or finding belongs to, or null when there is no such thing.</summary>
    Task<Guid?> FindProjectIdAsync(ProjectScoped kind, Guid id, CancellationToken ct);

    /// <summary>The projects a user is a member of. Admins see every project whatever this says.</summary>
    Task<IReadOnlyList<Guid>> ListMemberProjectIdsAsync(Guid userId, CancellationToken ct);

    Task<bool> IsMemberAsync(Guid projectId, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken ct);

    /// <summary>Makes the user a member of the project. False, and nothing recorded, when they
    /// already are.</summary>
    /// <exception cref="StoreNotFoundException">The project does not exist.</exception>
    Task<bool> AddMemberAsync(Guid projectId, Guid userId, Guid actorId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Ends the user's membership. False, and nothing recorded, when they had none.</summary>
    Task<bool> RemoveMemberAsync(Guid projectId, Guid userId, Guid actorId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records a new invitation, made by its CreatedBy.</summary>
    Task<Invitation> AddInvitationAsync(Invitation invitation, CancellationToken ct);

    /// <summary>Every invitation, newest first.</summary>
    Task<IReadOnlyList<Invitation>> ListInvitationsAsync(CancellationToken ct);

    /// <summary>The invitation whose link's token hashes to this, or null.</summary>
    Task<Invitation?> FindInvitationAsync(string tokenHash, CancellationToken ct);

    /// <summary>Makes an unused invitation's link stop working.</summary>
    /// <exception cref="StoreNotFoundException">There is no such invitation.</exception>
    /// <exception cref="StoreConflictException">It was already accepted or revoked.</exception>
    Task RevokeInvitationAsync(Guid invitationId, Guid actorId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Uses up an invitation for the account just created for it, and makes that account a member
    /// of the invitation's projects that still exist. Only one acceptance can ever succeed.
    /// </summary>
    /// <exception cref="StoreNotFoundException">There is no such invitation.</exception>
    /// <exception cref="StoreConflictException">It was accepted, revoked or has expired; the
    /// message says which.</exception>
    Task<Invitation> AcceptInvitationAsync(Guid invitationId, Guid userId, DateTimeOffset now, CancellationToken ct);

    // ---- Factory settings and secrets (m3-architecture.md §3) ----

    /// <summary>Every settings section written so far; a section never written is absent.</summary>
    Task<IReadOnlyList<SettingsSection>> ListSettingsAsync(CancellationToken ct);

    /// <summary>
    /// Writes a section, when its revision is still the one the caller read (0 for one never
    /// written), and returns it at its new revision. An Admin's change is audited; the host's
    /// seeding (no actor) is not.
    /// </summary>
    /// <exception cref="StoreConflictException">The section changed since the caller read it.</exception>
    Task<SettingsSection> SaveSettingsAsync(string section, string json, long expectedRevision, Guid? actorId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Every secret, protected. The API never passes the ciphertext on.</summary>
    Task<IReadOnlyList<FactorySecret>> ListSecretsAsync(CancellationToken ct);

    /// <summary>Sets or replaces a secret. An Admin's change is audited by name only.</summary>
    Task SetSecretAsync(string name, string ciphertext, Guid? actorId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Clears a secret. False, and nothing recorded, when it was not set.</summary>
    Task<bool> ClearSecretAsync(string name, Guid actorId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records a change made outside the store, such as to an Identity account.</summary>
    Task AddAuditAsync(AuditEvent auditEvent, CancellationToken ct);

    /// <summary>The newest audit rows, of one project or of everything.</summary>
    Task<IReadOnlyList<AuditEvent>> ListAuditAsync(Guid? projectId, int limit, CancellationToken ct);
}
