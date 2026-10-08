using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>
/// The limits and no-progress rules of ReadMe_LitosSoftwareFactory_V1.md §8.5. Each one stops a
/// run as Blocked with a stated reason; the edits are kept.
/// </summary>
public sealed record RunLimits
{
    /// <summary>Shared between test failures and blocking review findings.</summary>
    public int MaxRepairCycles { get; init; } = 2;

    /// <summary>request_decision calls per run. Past this the factory proceeds on the agent's
    /// own recommendation and says so in the handoff.</summary>
    public int MaxDecisions { get; init; } = 3;

    public int MaxToolCallsPerTurn { get; init; } = 200;

    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(45);

    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromHours(3);

    /// <summary>Above this many changed lines the review turn gets the changed-file list to read
    /// for itself instead of the diff inline.</summary>
    public int InlineReviewMaxChangedLines { get; init; } = 1_500;

    /// <summary>Total size of the failure excerpts a repair brief may carry.</summary>
    public int RepairExcerptTokens { get; init; } = 4_000;

    // ---- Review depth and allowance (ReviewPlanner, TurnAllowance) ----

    /// <summary>More added lines than this outside tests adds 3 to a change's risk score.</summary>
    public int LightReviewMaxChangedLines { get; init; } = 150;

    /// <summary>More files than this adds 3 to a change's risk score.</summary>
    public int LightReviewMaxFiles { get; init; } = 8;

    /// <summary>More added lines than this outside tests (and no more than the light limit)
    /// adds 1: a change past it is not small enough to go unreviewed.</summary>
    public int NoReviewMaxChangedLines { get; init; } = 40;

    /// <summary>More files than this (and no more than the light limit) adds 1.</summary>
    public int NoReviewMaxFiles { get; init; } = 4;

    /// <summary>The risk score from which a change gets a light review; below it, none. At 1,
    /// any signal at all earns a review (ReviewPlanner).</summary>
    public int LightReviewFromScore { get; init; } = 1;

    /// <summary>The risk score from which a change gets a full review.</summary>
    public int FullReviewFromScore { get; init; } = 6;

    /// <summary>False keeps at least a light review on every change (FACTORY_REVIEW_NONE=off).</summary>
    public bool AllowNoReview { get; init; } = true;

    /// <summary>A light review is asked to submit after this many model calls...</summary>
    public int LightReviewWrapUpCalls { get; init; } = 2;

    /// <summary>...and is stopped after this many tool calls: the two it needs, plus room for a
    /// PTC kernel error or a rejected submission, each of which costs a call of its own.</summary>
    public int LightReviewMaxToolCalls { get; init; } = 6;

    /// <summary>A full review is asked to submit after this many model calls (the first real
    /// reviews took 5 to 37)...</summary>
    public int FullReviewWrapUpCalls { get; init; } = 12;

    /// <summary>...or once it has cost this share of what the implementation cost...</summary>
    public double ReviewAllowanceShare { get; init; } = 0.5;

    /// <summary>...but never less than this, so a cheap implementation still gets a real review...</summary>
    public long ReviewAllowanceFloorTokens { get; init; } = 40_000;

    /// <summary>...and is stopped after this many tool calls.</summary>
    public int FullReviewMaxToolCalls { get; init; } = 24;

    // ---- The cost note in work turns (CostMeter) ----

    /// <summary>Whether implement, rework and repair turns are sent cost notes (FACTORY_COST_NOTES=off turns them off).</summary>
    public bool CostNotes { get; init; } = true;

    /// <summary>A note is sent after this many calls since the last one...</summary>
    public int CostNoteEveryCalls { get; init; } = 15;

    /// <summary>...and when the context first passes each of these sizes, in tokens...</summary>
    public IReadOnlyList<long> CostNoteContextThresholds { get; init; } = [50_000, 80_000, 110_000];

    /// <summary>...but never within this many calls of the last note.</summary>
    public int CostNoteMinGapCalls { get; init; } = 5;

    // ---- The decision scan (DecisionPolicy) ----

    /// <summary>After each answer, while questions remain, the scan session checks whether the
    /// answers settle them before the next is asked (FACTORY_SCAN_RECHECK=off turns it off).</summary>
    public bool ScanRecheck { get; init; } = true;

    /// <summary>Whether a first run starts with a decision scan. Off unless the host turns it on
    /// (FACTORY_DECISION_SCAN), so the orchestrator's behaviour is unchanged by default.</summary>
    public bool DecisionScan { get; init; }

    /// <summary>Stop after the scan and report what it would ask and assume, without asking or
    /// implementing: for checking the scan on requests cheaply (FACTORY_SCAN_ONLY).</summary>
    public bool ScanOnly { get; init; }

    /// <summary>A scan is asked to submit after this many model calls...</summary>
    public int ScanWrapUpCalls { get; init; } = 6;

    /// <summary>...and is stopped after this many tool calls.</summary>
    public int ScanMaxToolCalls { get; init; } = 10;
}

public enum RunKind
{
    /// <summary>A first run for a task: starts with an implement turn.</summary>
    Implement,

    /// <summary>A new run on the same branch after the tester asked for changes.</summary>
    Rework,

    /// <summary>
    /// One read-only answer to a plain message (m2-architecture.md §5): no repository lease, no
    /// change to the task's state, and a budget of its own rather than the task's.
    /// </summary>
    Chat,

    /// <summary>
    /// One read-only turn that proposes a specification (m2-architecture.md §5). Task work, so it
    /// is charged to the task's budget and moves the task through Queued and Running, but it
    /// takes no repository lease: it reads the project's reading copy.
    /// </summary>
    Spec,
}

/// <summary>Which template a turn's prompt is composed from.</summary>
public enum BriefKind
{
    Run,
    Rework,
    Repair,
    Review,
    Nudge,
    DecisionAnswer,
    ProceedOnRecommendation,
    Resume,

    /// <summary>The decision scan: list the choices the request leaves open.</summary>
    Scan,

    /// <summary>After an answer, the scan checks whether the answers so far settle the questions
    /// not yet asked.</summary>
    ScanRecheck,
}

// ---- Steps: what the host should do next ----

[JsonPolymorphic(TypeDiscriminatorPropertyName = "step")]
[JsonDerivedType(typeof(PreflightStep), "preflight")]
[JsonDerivedType(typeof(StartTurnStep), "turn")]
[JsonDerivedType(typeof(VerifyStep), "verify")]
[JsonDerivedType(typeof(HandoffStep), "handoff")]
[JsonDerivedType(typeof(StopStep), "stop")]
public abstract record RunStep;

/// <summary>Host step: fetch, create or check out the task branch, confirm a clean working
/// copy, establish the baseline verification, snapshot settings.</summary>
public sealed record PreflightStep : RunStep;

/// <summary>Which conversation a turn belongs to (§8.6).</summary>
public enum SessionScope
{
    /// <summary>The thread's one session, which carries chat, implement, repair and rework turns.</summary>
    Thread,

    /// <summary>The run's review session: created empty for the review brief, so the reviewer
    /// never sees the thread's conversation, and reused for a nudge or a resume of that review.</summary>
    Review,

    /// <summary>The run's decision-scan session: fresh, so the scan looks at the request without
    /// an implementer's commitments, and reused for a nudge or a resume of the scan.</summary>
    Scan,
}

/// <summary>Agent turn.</summary>
public sealed record StartTurnStep(TurnKind Kind, BriefKind Brief, SessionScope Session = SessionScope.Thread) : RunStep;

/// <summary>Host step: run the verification profile, read its reports, compute changed-line coverage.</summary>
public sealed record VerifyStep : RunStep;

/// <summary>Host step: commit, push, create or update the draft PR, compose the handoff.</summary>
public sealed record HandoffStep : RunStep;

/// <summary>The run stops here. Trigger is the lifecycle transition the host applies.</summary>
public sealed record StopStep(LifecycleTrigger Trigger, StopReason Reason, string Message) : RunStep;

public enum StopReason
{
    HandedOff,
    DecisionNeeded,
    BudgetExhausted,
    PausedByUser,
    Cancelled,
    PreflightFailed,
    TurnFaulted,

    /// <summary>A turn, and the nudge after it, both ended without a completion tool.</summary>
    NoCompletionCall,

    /// <summary>A turn changed no files and made no completion call.</summary>
    NoProgress,

    /// <summary>The same failures before and after a repair turn.</summary>
    RepairMadeNoDifference,
    RepairCyclesExhausted,
    ToolCallLimit,
    TurnTimeLimit,
    RunTimeLimit,

    /// <summary>The verification commands could not be started at all.</summary>
    VerificationUnavailable,
    HandoffFailed,

    /// <summary>The host or the worker was lost while the run was in progress.</summary>
    Interrupted,

    /// <summary>A rework run whose change request the tester withdrew.</summary>
    Withdrawn,

    /// <summary>The scan-only check finished: the run reports what the scan found and stops.</summary>
    ScanOnly,
}

// ---- Outcomes: what happened when the host did it ----

public abstract record StepOutcome;

/// <summary>The run was claimed and is starting.</summary>
public sealed record RunStarted : StepOutcome;

/// <summary>A stopped run was resumed: by the user, after a budget raise, after a blocker was
/// resolved or after recovery from an interruption.</summary>
public sealed record Resumed : StepOutcome;

public sealed record PreflightCompleted(bool Succeeded, string? Failure = null) : StepOutcome;

public enum TurnEndReason
{
    /// <summary>The model replied without further tool calls.</summary>
    Completed,

    /// <summary>The gateway refused the next model call for lack of budget or quota.</summary>
    BudgetRefused,
    PausedByUser,
    Cancelled,
    Faulted,
    ToolCallLimit,
    TimeLimit,

    /// <summary>A model call reached its output limit without replying: in practice a reasoning
    /// model that spent the whole allowance thinking.</summary>
    OutputLimit,
}

/// <summary>
/// An agent turn ended. Submission is what a completion tool reported during the turn, if any —
/// taken from the host's own record of the callback, not from the turn's event stream.
/// </summary>
/// <param name="LightReview">The turn was a light review: one cut off at its output limit is not
/// run again, because a resumed review runs as a full one.</param>
public sealed record TurnEnded(
    TurnEndReason Reason, Submission? Submission = null, bool FilesChanged = false, string? Detail = null, bool LightReview = false) : StepOutcome;

public sealed record DecisionAnswered(string Answer) : StepOutcome;

/// <summary>The host was about to start a review and its planner judged none was needed
/// (ReviewDepth.None): no turn ran.</summary>
public sealed record ReviewNotNeeded : StepOutcome;

/// <summary>Baseline is the same profile run on the base commit, so pre-existing failures are known.</summary>
public sealed record Verified(VerificationOutcome Outcome, VerificationOutcome? Baseline = null) : StepOutcome;

public sealed record HandoffCompleted(bool Succeeded, string? Failure = null) : StepOutcome;

/// <summary>
/// A person paused or cancelled the run while the host was in a step that is not a model turn
/// (preflight, verification, handoff, the work before a turn), or between two steps. A stop
/// during a turn is reported by <see cref="TurnEnded"/> instead.
/// </summary>
public sealed record StepStopped(bool Cancel) : StepOutcome;

// ---- State ----

public enum RunPhase
{
    NotStarted,
    Preflight,
    Turn,
    Verify,
    Handoff,
    Stopped,
}

public sealed record AnsweredDecision(string Question, string Answer);

/// <summary>
/// Everything the orchestrator needs to decide the next step, and nothing it would have to ask
/// the agent for. Immutable: each transition returns a new state, which the host persists with
/// the step so a restarted host resumes from its own records.
/// </summary>
public sealed record RunState(RunKind Kind)
{
    public RunPhase Phase { get; init; } = RunPhase.NotStarted;

    /// <summary>The kind of the turn in progress or last finished, including a nudge.</summary>
    public TurnKind? CurrentTurn { get; init; }

    /// <summary>The work the run is doing — the last turn that was not a nudge. A nudge, a
    /// resume and a decision answer all continue this.</summary>
    public TurnKind WorkTurn { get; init; } = TurnKind.Implement;

    /// <summary>Whether the current work turn has already had its one automatic follow-up.</summary>
    public bool NudgeUsed { get; init; }

    public int RepairCyclesUsed { get; init; }

    public int DecisionsAsked { get; init; }

    public DecisionSubmission? OpenDecision { get; init; }

    public IReadOnlyList<AnsweredDecision> Decisions { get; init; } = [];

    public WorkSubmission? LastSubmission { get; init; }

    public VerificationOutcome? LastVerification { get; init; }

    public VerificationOutcome? Baseline { get; init; }

    /// <summary>What was failing when the current repair turn started; null when the repair is
    /// for review findings or for coverage alone.</summary>
    public IReadOnlyList<string>? FailuresBeforeRepair { get; init; }

    public bool ReviewCompleted { get; init; }

    public ReviewStatus Review { get; init; } = ReviewStatus.NotRun;

    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];

    /// <summary>A repair for blocking review findings is under way and not yet verified.</summary>
    public bool ReviewRepairPending { get; init; }

    /// <summary>How many times the review's model reached its output limit without replying.</summary>
    public int ReviewCutOffs { get; init; }

    /// <summary>Limitations the handoff must state: things that were not fixed or not asked.</summary>
    public IReadOnlyList<string> Disclosures { get; init; } = [];

    /// <summary>The decision scan's result, once it has run.</summary>
    public PlanSubmission? Plan { get; init; }

    /// <summary>Choices the scan raised that are still to be asked, after the open one.</summary>
    /// <summary>True while the scan is re-checking the questions not yet asked against the answers.</summary>
    public bool Rechecking { get; init; }

    public IReadOnlyList<OpenChoice> PendingQuestions { get; init; } = [];

    /// <summary>Choices the run proceeds on without asking, stated to the implementer and in the
    /// handoff so the tester can see what was decided.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];

    /// <summary>The step to take once preflight succeeds.</summary>
    public RunStep? AfterPreflight { get; init; }

    /// <summary>Where a resumed run picks up. Null when the run cannot be resumed (handed off or
    /// cancelled) or is waiting for a decision answer instead.</summary>
    public RunStep? ResumePoint { get; init; }

    public StopStep? LastStop { get; init; }

    /// <summary>When the run last started or resumed; the run time limit counts from here.</summary>
    public DateTimeOffset ActiveSince { get; init; }

    /// <summary>The stage a task in this state is in (§5.1).</summary>
    public Stage Stage => Phase switch
    {
        RunPhase.Verify => Stage.Verify,
        RunPhase.Handoff => Stage.Handoff,
        RunPhase.Stopped when LastStop?.Reason == StopReason.HandedOff => Stage.Handoff,
        RunPhase.Stopped when ResumePoint is VerifyStep => Stage.Verify,
        RunPhase.Stopped when ResumePoint is HandoffStep => Stage.Handoff,
        _ => WorkTurn == TurnKind.Review ? Stage.Review : Stage.Implement,
    };
}

public sealed record RunTransition(RunState State, RunStep Step);

/// <summary>
/// RunState as the checkpoint the store keeps. A run resumes from this, so the shape is pinned
/// by round-trip tests: a field that failed to come back would silently reset a limit.
/// </summary>
public static class RunStateJson
{
    public static string Serialize(RunState state) => JsonSerializer.Serialize(state, FactoryWire.Json);

    public static RunState Deserialize(string json) =>
        JsonSerializer.Deserialize<RunState>(json, FactoryWire.Json) ?? throw new JsonException("The run checkpoint is empty.");
}
