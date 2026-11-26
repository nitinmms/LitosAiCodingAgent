namespace Litos.Gui.Buzzies.Routines;

public sealed record RoutineSchedulerOptions
{
    /// <summary>How often due routines are checked. Well under <see cref="RoutineSchedule.Grace"/>.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How many runs may execute at once across all Buzzies; read per dispatch, so a
    /// switch to a local model (which serves one request at a time) takes effect straight away.</summary>
    public Func<int> MaxConcurrent { get; init; } = () => 3;

    /// <summary>A queued run whose Buzzy stays busy this long is skipped rather than run late.</summary>
    public TimeSpan MaxQueueWait { get; init; } = TimeSpan.FromMinutes(60);
}

/// <summary>
/// Runs routines while Litos is open (Implement_Buzzies.md, Phase 1), in this process, with no
/// separate service. Each tick plans every enabled routine (<see cref="RoutineSchedule.Plan"/>),
/// claims due occurrences by their unique key, records missed ones, then starts queued runs: one
/// at a time per Buzzy, never while the user is chatting with that Buzzy, and within the global
/// limit. A per-user lock file makes sure only one Litos window schedules; any other one can still
/// show routines and history but runs nothing.
/// </summary>
public sealed class RoutineScheduler : IAsyncDisposable
{
    private readonly BuzzyDb _db;
    private readonly BuzzyStore _store;
    private readonly IBuzzyRunExecutor _executor;
    private readonly TimeProvider _clock;
    private readonly RoutineSchedulerOptions _options;
    private readonly string _lockPath;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string BuzzyId, CancellationTokenSource Cancel, Task Task)> _running = [];
    private readonly HashSet<string> _interactive = [];
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private FileStream? _lockFile;
    private Task? _loop;

    public RoutineScheduler(BuzzyDb db, BuzzyStore store, IBuzzyRunExecutor executor, TimeProvider? clock = null,
        RoutineSchedulerOptions? options = null)
    {
        _db = db;
        _store = store;
        _executor = executor;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new RoutineSchedulerOptions();
        _lockPath = Path.Combine(store.RootDirectory, "scheduler.lock");
    }

    /// <summary>A run started. Raised off the UI thread.</summary>
    public event Action<RoutineRun>? RunStarted;

    /// <summary>A run ended (any way). Raised off the UI thread.</summary>
    public event Action<RoutineRun>? RunFinished;

    /// <summary>Routines or runs changed in a way a list on screen should reflect.</summary>
    public event Action? Changed;

    /// <summary>False when another Litos window holds the scheduler lock: this one runs nothing.</summary>
    public bool IsPrimary => _lockFile is not null;

    public BuzzyDb Database => _db;

    /// <summary>Takes the per-user lock, surfaces runs a previous Litos left mid-way, and starts
    /// ticking. Without the lock it does nothing, and says so via <see cref="IsPrimary"/>.</summary>
    public void Start()
    {
        if (!TryTakeLock())
            return;

        if (_db.MarkInterrupted(_clock.GetUtcNow()) > 0)
            Changed?.Invoke();
        _loop = Task.Run(LoopAsync);
    }

    private bool TryTakeLock()
    {
        try
        {
            Directory.CreateDirectory(_store.RootDirectory);
            _lockFile = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_options.Tick, _clock);
        do
        {
            try
            {
                await TickAsync();
            }
            catch (Exception)
            {
                // A failed tick (database locked, disk full) is retried on the next one.
            }
        }
        while (await WaitNextAsync(timer));
    }

    private async Task<bool> WaitNextAsync(PeriodicTimer timer)
    {
        try
        {
            return await timer.WaitForNextTickAsync(_stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>One scheduling pass: plan, claim, record, then dispatch. Internal for tests,
    /// which drive it with a manual clock instead of waiting for the timer.</summary>
    internal async Task TickAsync()
    {
        await _tickLock.WaitAsync();
        try
        {
            var now = _clock.GetUtcNow();
            var changed = false;
            foreach (var routine in _db.ListRoutines())
                changed |= PlanRoutine(routine, now);
            changed |= Dispatch(now);
            if (changed)
                Changed?.Invoke();
        }
        finally
        {
            _tickLock.Release();
        }
    }

    private bool PlanRoutine(Routine routine, DateTimeOffset now)
    {
        var zone = RoutineSchedule.ResolveTimeZone(routine.TimeZoneId);
        var plan = RoutineSchedule.Plan(routine, now, zone);
        if (plan.ScheduledUpTo is null)
            return false;

        // Only the most recent missed occurrences get a row; the newest says how many more there were.
        var unlisted = plan.MissedTotal - plan.Missed.Count;
        for (var i = 0; i < plan.Missed.Count; i++)
        {
            var missed = plan.Missed[i];
            var why = "Litos was closed or the computer was asleep at this time, so this run was skipped.";
            if (i == plan.Missed.Count - 1 && unlisted > 0)
                why += $" {unlisted} earlier run(s) were missed too and are not listed.";
            _db.TryAddRun(new RoutineRun(NewId(), routine.Id, routine.BuzzyId, RoutineSchedule.OccurrenceKey(routine, missed, zone),
                RunTrigger.Scheduled, RunStatus.Missed, missed, FinishedAt: now, Error: why));
        }

        if (plan.RunNow is { } occurrence)
        {
            _db.TryAddRun(new RoutineRun(NewId(), routine.Id, routine.BuzzyId, RoutineSchedule.OccurrenceKey(routine, occurrence, zone),
                plan.RunTrigger, RunStatus.Queued, now));
        }

        _db.SetLastScheduled(routine.Id, plan.ScheduledUpTo.Value);
        return true;
    }

    /// <summary>For an edit made straight to the database (create, edit, pause, delete in the
    /// Routines window): tells everything showing routines to refresh.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Queues a run of <paramref name="routineId"/> now, whatever its schedule.</summary>
    public async Task<RoutineRun?> RunNowAsync(string routineId)
    {
        if (_db.GetRoutine(routineId) is not { } routine)
            return null;

        var now = _clock.GetUtcNow();
        var run = new RoutineRun(NewId(), routine.Id, routine.BuzzyId, $"manual:{Guid.NewGuid():n}", RunTrigger.Manual, RunStatus.Queued, now);
        _db.TryAddRun(run);
        Changed?.Invoke();
        if (IsPrimary)
            await TickAsync();
        return run;
    }

    /// <summary>Cancels a queued or running run. False if it had already ended.</summary>
    public bool Cancel(string runId)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(runId, out var running))
            {
                running.Cancel.Cancel();
                return true;
            }
        }

        var cancelled = _db.Finish(runId, RunStatus.Cancelled, _clock.GetUtcNow(), error: "Cancelled before it started.");
        if (cancelled)
            Changed?.Invoke();
        return cancelled;
    }

    /// <summary>The run currently executing for a Buzzy, if any.</summary>
    public RoutineRun? RunningFor(string buzzyId)
    {
        string? runId;
        lock (_gate)
            runId = _running.FirstOrDefault(r => r.Value.BuzzyId == buzzyId).Key;
        return runId is null ? null : _db.GetRun(runId);
    }

    /// <summary>The user is chatting with this Buzzy: no routine starts for it until <see cref="EndInteractive"/>.</summary>
    public void BeginInteractive(string buzzyId)
    {
        lock (_gate)
            _interactive.Add(buzzyId);
    }

    public void EndInteractive(string buzzyId)
    {
        lock (_gate)
            _interactive.Remove(buzzyId);
        if (IsPrimary)
            _ = TickAsync();
    }

    /// <summary>Starts every queued run that may start now. Returns true when anything changed.</summary>
    private bool Dispatch(DateTimeOffset now)
    {
        var changed = false;
        foreach (var run in _db.ListQueued())
        {
            if (now - run.QueuedAt > _options.MaxQueueWait)
            {
                changed |= _db.Finish(run.Id, RunStatus.Skipped, now, error: "Skipped: its Buzzy was busy for too long, so it would have run very late.");
                continue;
            }

            var profile = _store.Get(run.BuzzyId);
            var routine = _db.GetRoutine(run.RoutineId);
            if (profile is null || routine is null)
            {
                changed |= _db.Finish(run.Id, RunStatus.Failed, now, error: "Its Buzzy or routine no longer exists.");
                continue;
            }

            lock (_gate)
            {
                if (_running.Count >= Math.Max(1, _options.MaxConcurrent())
                    || _interactive.Contains(run.BuzzyId)
                    || _running.Values.Any(r => r.BuzzyId == run.BuzzyId))
                    continue;

                var sessionId = Guid.NewGuid().ToString("n");
                if (!_db.TryMarkRunning(run.Id, sessionId, now))
                    continue;

                var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                var started = run with { Status = RunStatus.Running, SessionId = sessionId, StartedAt = now };
                var task = Task.Run(() => ExecuteAsync(started, profile, routine, cancel));
                _running[run.Id] = (run.BuzzyId, cancel, task);
                changed = true;
            }
        }

        return changed;
    }

    private async Task ExecuteAsync(RoutineRun run, BuzzyProfile profile, Routine routine, CancellationTokenSource cancel)
    {
        RunStarted?.Invoke(run);
        RunOutcome outcome;
        try
        {
            outcome = await _executor.RunAsync(new RunRequest(profile, routine, run.SessionId!, run.StartedAt!.Value), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new RunOutcome(RunStatus.Cancelled, null, _stopping.IsCancellationRequested ? "Litos was closed during this run." : "Cancelled.", 0, 0, []);
        }
        catch (Exception ex)
        {
            outcome = new RunOutcome(RunStatus.Failed, null, ex.Message, 0, 0, []);
        }

        if (outcome.Status == RunStatus.Cancelled && _stopping.IsCancellationRequested)
            outcome = outcome with { Error = "Litos was closed during this run." };

        var finishedAt = _clock.GetUtcNow();
        _db.Finish(run.Id, outcome.Status, finishedAt, outcome.Summary, outcome.Error, outcome.InputTokens, outcome.OutputTokens, outcome.Outputs);
        lock (_gate)
            _running.Remove(run.Id);
        cancel.Dispose();

        RunFinished?.Invoke(_db.GetRun(run.Id) ?? run with { Status = outcome.Status, FinishedAt = finishedAt });
        Changed?.Invoke();

        if (!_stopping.IsCancellationRequested)
            _ = TickAsync(); // A queued run may have been waiting for this one.
    }

    /// <summary>Waits for every run in progress to end. For tests and shutdown.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_gate)
                tasks = [.. _running.Values.Select(r => r.Task)];
            if (tasks.Length == 0)
                return;
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>Stops scheduling, cancels runs in progress (recorded as cancelled, since Litos is
    /// closing) and releases the lock so another Litos window can take over.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
        }

        if (_loop is not null)
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
        _lockFile?.Dispose();
        _lockFile = null;
    }

    private static string NewId() => Guid.NewGuid().ToString("n");
}
