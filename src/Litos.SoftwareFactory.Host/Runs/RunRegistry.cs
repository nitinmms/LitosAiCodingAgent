using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Estimation;

namespace Litos.SoftwareFactory.Host.Runs;

public enum StopRequest
{
    None,
    Pause,
    Cancel,
}

/// <summary>
/// What the host knows about a run that is executing right now: the secret its worker must
/// present, the turn in progress, and what that turn has reported. It lives in memory only —
/// everything that must survive a restart is in the store.
/// </summary>
public sealed class ActiveRun(Guid runId, Guid threadId, Guid userId, string provider, string model)
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _turn;
    private Submission? _submission;

    public Guid RunId { get; } = runId;

    public Guid ThreadId { get; } = threadId;

    /// <summary>The user the run's tokens are attributed to.</summary>
    public Guid UserId { get; } = userId;

    public string Provider { get; } = provider;

    public string Model { get; } = model;

    /// <summary>Generated per launch; the worker's callbacks and the host's calls both carry it.</summary>
    public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public TaskCompletionSource<WorkerReady> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TurnKind? TurnKind { get; private set; }

    /// <summary>The work the turn belongs to. A nudge continues the work it follows, so what it
    /// may submit is decided by this, not by its own kind.</summary>
    public TurnKind WorkKind { get; private set; } = Contracts.TurnKind.Implement;

    public string? SessionId { get; private set; }

    /// <summary>Set by a user's pause or cancel; the turn's end is then reported as that.</summary>
    public StopRequest StopRequest { get; private set; }

    /// <summary>Why the gateway refused a call during this turn, if it did.</summary>
    public string? BudgetRefusal { get; private set; }

    public Guid? DecisionId { get; set; }

    /// <summary>The worker serving this run, once it is started; steering goes through it.</summary>
    public Litos.SoftwareFactory.Core.Ports.IWorkerClient? Client { get; set; }

    /// <summary>The last settled model call of each session, which the next call's estimate starts from.</summary>
    public ConcurrentDictionary<string, SessionBaseline> Baselines { get; } = new();

    /// <summary>
    /// The upstream that served the task's last call, when its provider routes calls: the next
    /// call asks for it first, so the conversation stays where its prompt cache is. Loaded from
    /// the store on the run's first call, so a resumed run keeps the upstream of the run before.
    /// </summary>
    public string? PreferredUpstream { get; set; }

    /// <summary>Whether <see cref="PreferredUpstream"/> has been loaded from the store.</summary>
    public bool PreferredUpstreamLoaded { get; set; }

    public bool HasSecret(string presented) =>
        presented.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(Secret));

    /// <summary>
    /// What the model calls of the turn in progress are recorded under: the turn's kind, or
    /// "LightReview" for a light review. A nudge or a resume of a review is recorded as review,
    /// so the review's full cost is in one place. Null outside a turn (compaction before one).
    /// </summary>
    public string? Phase { get; private set; }

    /// <summary>The allowance of the turn in progress, if it has one (reviews do).</summary>
    public Core.Orchestration.TurnAllowance? Allowance { get; private set; }

    /// <summary>Model calls settled in the turn in progress.</summary>
    public int TurnCalls { get; private set; }

    /// <summary>Tokens charged in the turn in progress.</summary>
    public long TurnCharged { get; private set; }

    private bool _wrapUpSent;

    /// <summary>Whether a submission whose limitations break something existing is sent back
    /// once as a decision to ask (<see cref="Core.Orchestration.DecisionSignals"/>).</summary>
    private bool _askAboutBreakingLimitations;

    private bool _breakingLimitationRaised;

    /// <summary>Starts a turn: clears what the previous turn reported.</summary>
    /// <param name="askAboutBreakingLimitations">False once the task has had a decision: a
    /// limitation may then describe exactly what the person chose.</param>
    public CancellationToken BeginTurn(
        TurnKind kind, TurnKind workKind, string sessionId, CancellationToken runToken,
        string? phase = null, Core.Orchestration.TurnAllowance? allowance = null, bool askAboutBreakingLimitations = true)
    {
        lock (_lock)
        {
            _turn?.Dispose();
            _turn = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            TurnKind = kind;
            WorkKind = workKind;
            SessionId = sessionId;
            Phase = phase ?? kind.ToString();
            Allowance = allowance;
            TurnCalls = 0;
            TurnCharged = 0;
            _wrapUpSent = false;
            _costMeter = new Core.Orchestration.CostMeterState();
            _askAboutBreakingLimitations = askAboutBreakingLimitations;
            _breakingLimitationRaised = false;
            _submission = null;
            BudgetRefusal = null;
            DecisionId = null;
            return _turn.Token;
        }
    }

    private Core.Orchestration.CostMeterState _costMeter = new();

    /// <summary>
    /// Whether the turn in progress is due a cost note after the call just recorded (CostMeter).
    /// Only implementation work gets one: implement, rework and repair turns and their nudges,
    /// never a review or a scan, and never once the turn has submitted.
    /// </summary>
    public bool CostNoteDue(long contextTokens, Core.Orchestration.RunLimits limits)
    {
        lock (_lock)
        {
            if (TurnKind is null || _submission is not null
                || WorkKind is not (Contracts.TurnKind.Implement or Contracts.TurnKind.Rework or Contracts.TurnKind.Repair))
            {
                return false;
            }

            if (!Core.Orchestration.CostMeter.IsDue(TurnCalls, contextTokens, _costMeter, limits, out var next))
                return false;
            _costMeter = next;
            return true;
        }
    }

    /// <summary>
    /// Records a settled model call of the turn in progress. Returns the message to steer the
    /// turn with when this call takes it past its allowance — once per turn, and never after
    /// it has already submitted — or null.
    /// </summary>
    public string? RecordTurnCall(long charged)
    {
        lock (_lock)
        {
            TurnCalls++;
            TurnCharged += charged;
            if (Allowance is not { } allowance || _wrapUpSent || _submission is not null)
                return null;

            var overCalls = TurnCalls >= allowance.WrapUpAfterCalls;
            var overTokens = allowance.WrapUpAfterTokens is { } tokens && TurnCharged >= tokens;
            if (!overCalls && !overTokens)
                return null;

            _wrapUpSent = true;
            var spent = overTokens
                ? string.Create(CultureInfo.InvariantCulture, $"{TurnCharged:N0} tokens, its allowance of {allowance.WrapUpAfterTokens:N0}")
                : $"{TurnCalls} model calls, its allowance of {allowance.WrapUpAfterCalls}";
            return WorkKind == Contracts.TurnKind.Scan
                ? $"This scan has used {spent}. Stop reading now and call `submit_plan` with the choices you have found. "
                  + "List any choice you suspect but could not confirm, with an empty settledBy."
                : $"This review has used {spent}. Stop investigating now and call `submit_review` with the findings you have. "
                  + "Report anything you suspect but could not confirm as a minor finding, and say that it is unconfirmed.";
        }
    }

    public Submission? Submission
    {
        get { lock (_lock) return _submission; }
    }

    /// <summary>
    /// True once the turn running in this session has recorded the result that finishes it:
    /// work, a review or a specification. A decision is not included; its turn is cancelled.
    /// </summary>
    public bool HasFinished(string sessionId)
    {
        lock (_lock)
            return TurnKind is not null && sessionId == SessionId && _submission is WorkSubmission or ReviewSubmission or SpecSubmission or PlanSubmission;
    }

    /// <summary>
    /// Records what a completion tool reported, if it belongs to the turn in progress. A turn
    /// can only report what its kind allows: a review turn cannot submit work, and an implement
    /// turn cannot declare its own review clean.
    /// </summary>
    public SubmissionResponse Accept(string sessionId, Submission submission)
    {
        lock (_lock)
        {
            var workKind = WorkKind;
            if (TurnKind is null || sessionId != SessionId)
                return new SubmissionResponse(false, "No turn is running for this session.");

            var allowed = submission switch
            {
                WorkSubmission or DecisionSubmission => workKind is Contracts.TurnKind.Implement or Contracts.TurnKind.Repair or Contracts.TurnKind.Rework,
                ReviewSubmission => workKind == Contracts.TurnKind.Review,
                SpecSubmission => workKind == Contracts.TurnKind.Spec,
                PlanSubmission => workKind == Contracts.TurnKind.Scan,
                _ => false,
            };
            if (!allowed)
                return new SubmissionResponse(false, $"{ToolName(submission)} is not valid in a {workKind} turn.");

            // A decision made alone and reported as a limitation goes back once as a question to
            // ask; submitted again unchanged, it is recorded.
            if (submission is WorkSubmission work && _askAboutBreakingLimitations && !_breakingLimitationRaised
                && Core.Orchestration.DecisionSignals.BreakingLimitation(work) is { } breaking)
            {
                _breakingLimitationRaised = true;
                return new SubmissionResponse(false, Core.Orchestration.DecisionSignals.Refusal(breaking));
            }

            _submission = submission;
            return new SubmissionResponse(true, "");
        }
    }

    private static string ToolName(Submission submission) => submission switch
    {
        WorkSubmission => "submit_work",
        DecisionSubmission => "request_decision",
        ReviewSubmission => "submit_review",
        SpecSubmission => "submit_spec",
        PlanSubmission => "submit_plan",
        _ => "This submission",
    };

    public void RefuseForBudget(string reason)
    {
        lock (_lock)
            BudgetRefusal = reason;
    }

    /// <summary>Asks the turn in progress to stop, for a pause or a cancel.</summary>
    public void RequestStop(StopRequest request)
    {
        lock (_lock)
        {
            StopRequest = request;
            CancelTurnLocked();
        }
    }

    /// <summary>Ends the turn in progress without marking the run as stopped by a user — used once
    /// request_decision has been recorded.</summary>
    public void CancelTurn()
    {
        lock (_lock)
            CancelTurnLocked();
    }

    private void CancelTurnLocked()
    {
        try
        {
            _turn?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn ended on its own a moment ago.
        }
    }
}

/// <summary>The runs executing in this host process, by run and by thread.</summary>
public sealed class RunRegistry
{
    private readonly ConcurrentDictionary<Guid, ActiveRun> _byRun = new();

    public ActiveRun Add(ActiveRun run)
    {
        _byRun[run.RunId] = run;
        return run;
    }

    public void Remove(Guid runId) => _byRun.TryRemove(runId, out _);

    public ActiveRun? Find(Guid runId) => _byRun.GetValueOrDefault(runId);

    public ActiveRun? FindByThread(Guid threadId) => _byRun.Values.FirstOrDefault(r => r.ThreadId == threadId);

    public int Count => _byRun.Count;
}

/// <summary>Wakes things that wait for work or for new events, so nothing has to poll tightly.</summary>
public sealed class FactorySignals
{
    private readonly SemaphoreSlim _work = new(0);
    private TaskCompletionSource _events = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();

    /// <summary>There may be a queued run to claim.</summary>
    public void WorkQueued()
    {
        if (_work.CurrentCount == 0)
            _work.Release();
    }

    public Task<bool> WaitForWorkAsync(TimeSpan timeout, CancellationToken ct) => _work.WaitAsync(timeout, ct);

    /// <summary>Something was written to the outbox.</summary>
    public void EventsWritten()
    {
        TaskCompletionSource previous;
        lock (_lock)
        {
            previous = _events;
            _events = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    public Task NextEvents
    {
        get { lock (_lock) return _events.Task; }
    }
}
