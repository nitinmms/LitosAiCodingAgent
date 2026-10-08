using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Store;

// The factory's persisted records (ReadMe_LitosSoftwareFactory_V1.md §14,
// docs/software-factory/m1-architecture.md §7, m2-architecture.md §4). Plain classes with no persistence attributes:
// the mapping to tables lives in Infrastructure. Primary keys are UUIDs, timestamps are UTC,
// token counts and caps are 64-bit.

public enum WorkspaceMode
{
    /// <summary>A factory-owned clone of a GitHub repository (§6.1).</summary>
    Clone,

    /// <summary>A registered local folder edited in place (§6.2). Not built in M1.</summary>
    LocalFolder,
}

public sealed class Project
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string GitHubOwner { get; set; }
    public required string GitHubRepository { get; set; }
    public required string DefaultBranch { get; set; }
    public WorkspaceMode Mode { get; set; } = WorkspaceMode.Clone;
    public bool PullRequestEnabled { get; set; } = true;

    /// <summary>The reviewed verification profile, as JSON, and its revision. A repository's own
    /// changes to verification configuration never alter this.</summary>
    public required string VerificationProfileJson { get; set; }
    public int ProfileRevision { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TaskThread
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OwnerId { get; set; }
    public required string Title { get; set; }
    public string TypeLabel { get; set; } = "feature";
    public Stage Stage { get; set; } = Stage.Discuss;
    public LifecycleState State { get; set; } = LifecycleState.Draft;

    /// <summary>Why the task is in its current state, when a person needs telling: the
    /// blocker, the open question, what it is queued behind.</summary>
    public string? StateReason { get; set; }

    public long? BudgetCap { get; set; }

    /// <summary>The cap the task was created with. Each change request adds a share of it.</summary>
    public long? InitialBudgetCap { get; set; }

    public long TokensUsed { get; set; }
    public long TokensReserved { get; set; }
    public string? Branch { get; set; }
    public string? BaseCommit { get; set; }
    public int? PullRequestNumber { get; set; }
    public string? PullRequestUrl { get; set; }

    /// <summary>The thread's one Litos session, for the whole of its life (§5).</summary>
    public required string SessionId { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }

    /// <summary>The next message's sequence number within this thread.</summary>
    public long NextMessageSequence { get; set; } = 1;

    /// <summary>Bumped on every change; user actions carry the revision they saw, and a stale
    /// one is a conflict (§14 transaction rule 4).</summary>
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum MessageAuthor
{
    User,
    Factory,
}

public enum MessageKind
{
    /// <summary>What a user typed.</summary>
    Text,

    /// <summary>A stage change or other progress note from the factory.</summary>
    Status,
    Decision,
    DecisionAnswer,
    Handoff,
}

public sealed class ThreadMessage
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public MessageAuthor Author { get; set; }
    public Guid? AuthorUserId { get; set; }
    public MessageKind Kind { get; set; }
    public required string Text { get; set; }
    public long Sequence { get; set; }

    /// <summary>The client's message id for an @factory dispatch. Unique, so a double-click or a
    /// network retry creates one assignment (§5).</summary>
    public string? DispatchKey { get; set; }
    public Guid? DecisionId { get; set; }

    /// <summary>Structured content for cards (a handoff's evidence, a decision's options).</summary>
    public string? PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Specification
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public int Revision { get; set; }
    public required string Summary { get; set; }
    public required string AcceptanceCriteriaJson { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum RunStatus
{
    /// <summary>Waiting for the coordinator to claim it.</summary>
    Queued,
    Running,

    /// <summary>Stopped where it can be continued: awaiting a decision, paused, blocked or interrupted.</summary>
    Suspended,

    /// <summary>Handed off or cancelled; it will not run again.</summary>
    Finished,
}

/// <summary>What the coordinator reports to the orchestrator when it next claims the run.</summary>
public enum RunEntry
{
    Start,
    Resume,
    DecisionAnswered,
}

public sealed class TaskRun
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid RequestedBy { get; set; }
    public RunKind Kind { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public RunEntry Entry { get; set; } = RunEntry.Start;

    /// <summary>The answer to carry in when <see cref="Entry"/> is DecisionAnswered.</summary>
    public string? EntryAnswer { get; set; }

    /// <summary>The request this run works on: the @factory message, or the tester's feedback.</summary>
    public required string Request { get; set; }
    public string? BaselineCommit { get; set; }
    public string? HeadCommit { get; set; }

    /// <summary>The orchestrator's RunState, saved with every step so a restarted host resumes
    /// from its own records (§15 checkpoints).</summary>
    public string? StateJson { get; set; }

    /// <summary>The working copy at the last checkpoint (a WorkspaceSnapshot), which a resumed
    /// run compares with the working copy now (§16).</summary>
    public string? WorkspaceSnapshotJson { get; set; }
    public string? ReviewSessionId { get; set; }
    public required string PromptRevision { get; set; }
    public StopReason? StopReason { get; set; }

    /// <summary>With the start time, identifies the worker process across a host restart (§16).</summary>
    public int? WorkerProcessId { get; set; }
    public DateTimeOffset? WorkerStartTime { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the run last entered the queue; runs are claimed in this order, so a
    /// resumed run waits behind work queued before it.</summary>
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }

    // A chat run's own budget (RunKind.Chat): chat is not charged to the task (§5), so its model
    // calls are admitted against these, never against the thread's cap.
    public long? ChatBudgetCap { get; set; }
    public long ChatTokensUsed { get; set; }
    public long ChatTokensReserved { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public enum DecisionStatus
{
    Open,
    Answered,
}

public sealed class Decision
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid ThreadId { get; set; }
    public required string Question { get; set; }
    public required string WhyItBlocks { get; set; }
    public required string OptionsJson { get; set; }
    public string? Recommendation { get; set; }
    public string? Impact { get; set; }
    public DecisionStatus Status { get; set; } = DecisionStatus.Open;
    public string? Answer { get; set; }
    public Guid? AnsweredBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
}

/// <summary>One model call: its reservation, and what it settled at (§9.2).</summary>
public sealed class UsageEntry
{
    public Guid Id { get; set; }

    /// <summary>Unique per call, so a repeated callback cannot be charged twice.</summary>
    public required string RequestKey { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? RunId { get; set; }
    public Guid UserId { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }

    /// <summary>The estimator's raw figure and the calibrated one the reservation was built on —
    /// kept apart so the M1 evaluation can measure the estimator itself.</summary>
    public long EstimatedInputRaw { get; set; }
    public long EstimatedInput { get; set; }
    public long Reserved { get; set; }
    public long ActualInput { get; set; }
    public long ActualCachedInput { get; set; }
    public long ActualOutput { get; set; }
    public long ActualReasoning { get; set; }

    /// <summary>What was taken from the allowance when the call settled.</summary>
    public long Charged { get; set; }
    public UsageStatus Status { get; set; } = UsageStatus.Reserved;

    /// <summary>What the call was for: the kind of turn it was made in (Implement, Repair,
    /// Rework, Review, LightReview, Nudge), or null when it was made outside a turn.</summary>
    public string? Phase { get; set; }

    /// <summary>Who served the call: a router's upstream when the provider reports one
    /// (UsageInfo.ServedBy), otherwise the provider itself. Null until settled.</summary>
    public string? ServedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
}

public enum VerificationKind
{
    /// <summary>The profile run on the base commit, so pre-existing failures are known.</summary>
    Baseline,
    Run,
}

public sealed class VerificationRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? RunId { get; set; }
    public VerificationKind Kind { get; set; }

    /// <summary>The commit verified. A baseline is reused for the same project, commit and
    /// profile revision instead of being run again.</summary>
    public required string Commit { get; set; }
    public int ProfileRevision { get; set; }
    public BuildStatus Build { get; set; }
    public UnitTestStatus UnitTests { get; set; }
    public CoverageStatus Coverage { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public double? ChangedLineCoveragePercent { get; set; }

    /// <summary>The whole VerificationOutcome: test cases, commands, problems, uncovered lines.</summary>
    public required string OutcomeJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum FindingStatus
{
    Open,
    Fixed,
    Dismissed,
}

public sealed class ReviewFindingRecord
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public FindingSeverity Severity { get; set; }
    public required string File { get; set; }
    public int? Line { get; set; }
    public required string Text { get; set; }
    public FindingStatus Status { get; set; } = FindingStatus.Open;

    /// <summary>What a person judged the finding to be; null until judged. This is what review
    /// yield is measured from (ReviewYield).</summary>
    public FindingVerdict? Verdict { get; set; }
    public Guid? VerdictBy { get; set; }
    public DateTimeOffset? VerdictAt { get; set; }
}

/// <summary>A person's judgement of a review finding (ReadM_SoftwareFactory_ReviewGuidance.md §13).</summary>
public enum FindingVerdict
{
    /// <summary>A defect that was worth finding.</summary>
    Real,

    /// <summary>True, but style, preference or too minor to act on.</summary>
    NotWorthFixing,

    /// <summary>A false positive: the finding is mistaken.</summary>
    Wrong,
}

public sealed class HandoffRecord
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid ThreadId { get; set; }
    public required string Branch { get; set; }
    public string? CommitSha { get; set; }
    public int? PullRequestNumber { get; set; }
    public string? PullRequestUrl { get; set; }

    /// <summary>The handoff as the host composed it from its own evidence (§11).</summary>
    public required string EvidenceJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The repository lock (§6.3). One live lease per lock identity: in clone mode the identity is
/// the project. It is held while a run is active or stopped with uncommitted work, and released
/// at handoff, when the working copy is clean and the branch pushed.
/// </summary>
public sealed class WorkspaceLease
{
    public Guid Id { get; set; }
    public required string LockIdentity { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }

    /// <summary>Set when the holder was lost; the lease is never taken over silently (§16).</summary>
    public bool RecoveryRequired { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One thing that happened to a thread, written in the same transaction as the change itself.
/// Sequence is durable and increasing, so a reconnecting client replays everything after the
/// last event it saw (§12, §14 transaction rule 3).
/// </summary>
public sealed class OutboxEvent
{
    public long Sequence { get; set; }
    public Guid ThreadId { get; set; }
    public Guid ProjectId { get; set; }
    public required string Type { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ---- People and access (M2, docs/software-factory/m2-architecture.md §4) ----

/// <summary>An account's factory-wide role (§13.3). Stored by name; the names are the Identity roles.</summary>
public enum AccountRole
{
    /// <summary>Works in the projects they belong to: threads, delegation, decisions, acceptance.</summary>
    Member,

    /// <summary>Runs the factory: people, projects, credentials and limits. Sees every project.</summary>
    Admin,
}

/// <summary>
/// A one-time invitation to create an account (§13.3: no self-service sign-up). Only the SHA-256
/// of the link's token is kept, so the link cannot be recovered from the database.
/// </summary>
public sealed class Invitation
{
    public Guid Id { get; set; }

    /// <summary>The name the invitee signs in with.</summary>
    public required string UserName { get; set; }

    /// <summary>For the Admin's reference only; nothing is sent to it.</summary>
    public string? Email { get; set; }
    public AccountRole Role { get; set; } = AccountRole.Member;

    /// <summary>The projects the invitee joins on accepting: a JSON array of project ids.</summary>
    public string ProjectIdsJson { get; set; } = "[]";

    /// <summary>Lower-case hex SHA-256 of the link's token.</summary>
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Whether the link still creates an account at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => UnusableReason(now) is null;

    /// <summary>Why the link no longer works, in words for the person holding it; null while it does.</summary>
    public string? UnusableReason(DateTimeOffset now) =>
        AcceptedAt is not null ? "This invitation has already been used."
        : RevokedAt is not null ? "This invitation was revoked. Ask an Admin for a new one."
        : now >= ExpiresAt ? "This invitation has expired. Ask an Admin for a new one."
        : null;
}

/// <summary>A Member's place in a project (§14). Admins need none: they see every project.</summary>
public sealed class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One change a person made, written in the same transaction as the change (§13.3: every action
/// records the acting user). Reads are not recorded.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; }

    /// <summary>Who acted.</summary>
    public Guid ActorId { get; set; }

    /// <summary>What they did, as a dotted name: see <see cref="AuditActions"/>.</summary>
    public required string Action { get; set; }

    /// <summary>The kind of thing acted on: see <see cref="AuditTargets"/>.</summary>
    public required string TargetType { get; set; }
    public Guid? TargetId { get; set; }

    /// <summary>The project it happened in; null for a factory-wide change.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>What changed, as JSON (before and after where there is one).</summary>
    public string? DetailsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class AuditTargets
{
    public const string Project = "project";
    public const string Thread = "thread";
    public const string Decision = "decision";
    public const string Finding = "finding";
    public const string User = "user";
    public const string Invitation = "invitation";
}

public static class AuditActions
{
    public const string ProjectRegister = "project.register";
    public const string MemberAdd = "project.member.add";
    public const string MemberRemove = "project.member.remove";
    public const string InvitationCreate = "invitation.create";
    public const string InvitationRevoke = "invitation.revoke";
    public const string InvitationAccept = "invitation.accept";
    public const string UserDisable = "user.disable";
    public const string UserEnable = "user.enable";
    public const string UserRole = "user.role";

    public const string ThreadCreate = "thread.create";

    /// <summary>A thread renamed, or filed under another task type.</summary>
    public const string ThreadEdit = "thread.edit";

    /// <summary>The first @factory: the task is handed to the factory.</summary>
    public const string ThreadDelegate = "thread.delegate";

    /// <summary>An @factory after a handoff: the tester asks for changes.</summary>
    public const string ThreadChangeRequest = "thread.change_request";
    public const string ThreadAccept = "thread.accept";
    public const string ThreadPause = "thread.pause";
    public const string ThreadCancel = "thread.cancel";

    /// <summary>Back into the queue: a resume, a recovery, a resolved blocker or a raised budget.</summary>
    public const string ThreadResume = "thread.resume";
    public const string ThreadWithdraw = "thread.withdraw";
    public const string ThreadBudget = "thread.budget";
    public const string DecisionAnswer = "decision.answer";
    public const string FindingVerdict = "finding.verdict";
}

public static class EventTypes
{
    public const string MessageAdded = "message";
    public const string StateChanged = "state";
    public const string UsageChanged = "usage";

    /// <summary>What a chat answer in progress is doing (<see cref="ChatProgress"/>). Never on the board's stream.</summary>
    public const string ChatProgress = "chat";
}

/// <summary>What a chat answer in progress has done so far, for the person waiting for it.</summary>
/// <param name="Activity">What it is doing now, in a few words: "Thinking", "Read src/Orders.cs".</param>
public sealed record ChatProgress(Guid RunId, DateTimeOffset StartedAt, int ModelCalls, int ToolCalls, string Activity);
