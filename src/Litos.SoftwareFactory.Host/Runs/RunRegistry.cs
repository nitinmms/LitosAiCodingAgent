using System.Collections.Concurrent;
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

    public bool HasSecret(string presented) =>
        presented.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(Secret));

    /// <summary>Starts a turn: clears what the previous turn reported.</summary>
    public CancellationToken BeginTurn(TurnKind kind, TurnKind workKind, string sessionId, CancellationToken runToken)
    {
        lock (_lock)
        {
            _turn?.Dispose();
            _turn = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            TurnKind = kind;
            WorkKind = workKind;
            SessionId = sessionId;
            _submission = null;
            BudgetRefusal = null;
            DecisionId = null;
            return _turn.Token;
        }
    }

    public Submission? Submission
    {
        get { lock (_lock) return _submission; }
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
                _ => false,
            };
            if (!allowed)
                return new SubmissionResponse(false, $"{ToolName(submission)} is not valid in a {workKind} turn.");

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
