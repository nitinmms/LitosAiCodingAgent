using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Gui.Buzzies;
using Litos.Gui.Buzzies.Routines;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Serves one scripted response per StreamAsync call; records each request.</summary>
internal sealed class ScriptedChatProvider : IChatProvider
{
    private readonly Queue<AgentEvent[]> _responses = new();

    public string ProviderName => "scripted";
    public List<ChatRequest> Requests { get; } = [];
    public TimeSpan Delay { get; set; }

    public void Enqueue(params AgentEvent[] events) => _responses.Enqueue(events);

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, ct);
        foreach (var evt in _responses.Count > 0 ? _responses.Dequeue() : [])
        {
            ct.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }
}

/// <summary>An executor whose runs end when the test says so (or straight away).</summary>
internal sealed class FakeExecutor : IBuzzyRunExecutor
{
    private readonly Dictionary<string, TaskCompletionSource<RunOutcome>> _gates = [];
    private readonly Lock _lock = new();

    /// <summary>When true, each run waits for <see cref="Complete"/>; otherwise it succeeds at once.</summary>
    public bool Hold { get; set; }

    public List<RunRequest> Started { get; } = [];

    public async Task<RunOutcome> RunAsync(RunRequest request, CancellationToken ct)
    {
        TaskCompletionSource<RunOutcome>? gate = null;
        lock (_lock)
        {
            Started.Add(request);
            if (Hold)
                _gates[request.SessionId] = gate = new TaskCompletionSource<RunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (gate is null)
            return new RunOutcome(RunStatus.Succeeded, $"ran {request.Routine.Name}", null, 10, 5, ["reports/out.md"]);

        await using (ct.Register(() => gate.TrySetCanceled(ct)))
            return await gate.Task;
    }

    public void Complete(RunRequest request, RunStatus status = RunStatus.Succeeded)
    {
        lock (_lock)
            _gates[request.SessionId].TrySetResult(new RunOutcome(status, "done", null, 1, 1, []));
    }

    public int StartedCount
    {
        get
        {
            lock (_lock)
                return Started.Count;
        }
    }
}

internal static class Routines
{
    public static readonly DateTimeOffset Monday9 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    public static Routine Daily(string buzzyId, TimeOnly at, DateTimeOffset created, string name = "Daily report", MissedRunPolicy missed = MissedRunPolicy.Skip) =>
        new(Guid.NewGuid().ToString("n"), buzzyId, name, "Write the report.", RoutineTrigger.Daily, at, null, null,
            TimeZoneInfo.Utc.Id, true, 10, 100_000, missed, created);

    public static Routine Every(string buzzyId, int minutes, DateTimeOffset created, string name = "Tick") =>
        new(Guid.NewGuid().ToString("n"), buzzyId, name, "Tick.", RoutineTrigger.Interval, null, null, minutes,
            TimeZoneInfo.Utc.Id, true, 10, 100_000, MissedRunPolicy.Skip, created);

    public static Routine Manual(string buzzyId, DateTimeOffset created, string name = "On demand") =>
        new(Guid.NewGuid().ToString("n"), buzzyId, name, "Do it.", RoutineTrigger.Manual, null, null, null,
            TimeZoneInfo.Utc.Id, true, 10, 100_000, MissedRunPolicy.Skip, created);
}

/// <summary>A temp Buzzy store with a database and a scheduler driven by a manual clock.</summary>
internal sealed class SchedulerHarness : IAsyncDisposable
{
    public TempDir Temp { get; } = new();
    public BuzzyStore Store { get; }
    public BuzzyDb Db { get; }
    public ManualClock Clock { get; }
    public FakeExecutor Executor { get; } = new();
    public RoutineScheduler Scheduler { get; }

    public SchedulerHarness(DateTimeOffset start, RoutineSchedulerOptions? options = null)
    {
        Store = new BuzzyStore(Temp.Path);
        Db = new BuzzyDb(Store.DatabasePath);
        Clock = new ManualClock(start);
        Scheduler = new RoutineScheduler(Db, Store, Executor, Clock, options);
    }

    public BuzzyProfile Buzzy(string name = "Scout") => Store.Create(BuzzyTemplates.Scout, name, "#2FB457", Clock.Now);

    public async Task TickAndSettleAsync()
    {
        await Scheduler.TickAsync();
        await Scheduler.WhenIdleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Temp.Dispose();
    }
}
