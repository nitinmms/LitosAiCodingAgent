using Litos.Gui.Buzzies.Routines;
using Microsoft.Data.Sqlite;

namespace Litos.Gui.Tests.Buzzies;

public sealed class BuzzyDbTests : IDisposable
{
    private static readonly DateTimeOffset Now = Routines.Monday9;
    private readonly TempDir _temp = new();
    private readonly BuzzyDb _db;

    public BuzzyDbTests() => _db = new BuzzyDb(Path.Combine(_temp.Path, "buzzies.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static RoutineRun Run(Routine routine, string key, RunStatus status = RunStatus.Queued) =>
        new(Guid.NewGuid().ToString("n"), routine.Id, routine.BuzzyId, key, RunTrigger.Scheduled, status, Now);

    [Fact]
    public void Routine_RoundTrips()
    {
        var routine = Routines.Daily("scout", new TimeOnly(9, 30), Now) with
        {
            Trigger = RoutineTrigger.Weekly, Day = DayOfWeek.Friday, MissedRuns = MissedRunPolicy.CatchUpOnce, LastScheduledAt = Now.AddDays(1),
        };

        _db.SaveRoutine(routine);

        Assert.Equal(routine, _db.GetRoutine(routine.Id));
    }

    [Fact]
    public void SaveRoutine_UpdatesInPlace()
    {
        var routine = Routines.Every("scout", 2, Now);
        _db.SaveRoutine(routine);

        _db.SaveRoutine(routine with { Name = "Renamed", IntervalMinutes = 5 });

        var saved = Assert.Single(_db.ListRoutines("scout"));
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(5, saved.IntervalMinutes);
    }

    [Fact]
    public void TryAddRun_SameOccurrenceTwice_OnlyTheFirstIsAdded()
    {
        var routine = Routines.Every("scout", 2, Now);
        _db.SaveRoutine(routine);

        Assert.True(_db.TryAddRun(Run(routine, "2026-11-23T09:02Z")));
        Assert.False(_db.TryAddRun(Run(routine, "2026-11-23T09:02Z")));
        Assert.Single(_db.ListQueued());
    }

    [Fact]
    public void TryAddRun_SameOccurrenceFromASecondConnection_IsStillRefused()
    {
        var routine = Routines.Every("scout", 2, Now);
        _db.SaveRoutine(routine);
        var other = new BuzzyDb(Path.Combine(_temp.Path, "buzzies.db")); // e.g. a second Litos window

        Assert.True(_db.TryAddRun(Run(routine, "k")));
        Assert.False(other.TryAddRun(Run(routine, "k")));
    }

    [Fact]
    public void Lifecycle_QueuedRunningFinished_AndAFinishedRunKeepsItsFirstEnding()
    {
        var routine = Routines.Every("scout", 2, Now);
        var run = Run(routine, "k");
        _db.TryAddRun(run);

        Assert.True(_db.TryMarkRunning(run.Id, "session-1", Now));
        Assert.False(_db.TryMarkRunning(run.Id, "session-2", Now)); // already running
        Assert.True(_db.Finish(run.Id, RunStatus.Succeeded, Now.AddMinutes(1), "summary", null, 10, 20, ["a.md", "http://x/y"]));
        Assert.False(_db.Finish(run.Id, RunStatus.Cancelled, Now.AddMinutes(2)));

        var saved = _db.GetRun(run.Id)!;
        Assert.Equal(RunStatus.Succeeded, saved.Status);
        Assert.Equal("session-1", saved.SessionId);
        Assert.Equal(30, saved.InputTokens + saved.OutputTokens);
        Assert.Equal(["a.md", "http://x/y"], saved.Outputs);
    }

    [Fact]
    public void MarkInterrupted_SurfacesRunningRuns_AndLeavesTheRest()
    {
        var routine = Routines.Every("scout", 2, Now);
        var running = Run(routine, "a");
        var queued = Run(routine, "b");
        _db.TryAddRun(running);
        _db.TryAddRun(queued);
        _db.TryMarkRunning(running.Id, "s", Now);

        Assert.Equal(1, _db.MarkInterrupted(Now.AddHours(1)));

        Assert.Equal(RunStatus.Interrupted, _db.GetRun(running.Id)!.Status);
        Assert.Contains("not retried", _db.GetRun(running.Id)!.Error);
        Assert.Equal(RunStatus.Queued, _db.GetRun(queued.Id)!.Status);
    }

    [Fact]
    public void SetLastScheduled_NeverMovesBackwards()
    {
        var routine = Routines.Every("scout", 2, Now);
        _db.SaveRoutine(routine);

        _db.SetLastScheduled(routine.Id, Now.AddMinutes(10));
        _db.SetLastScheduled(routine.Id, Now.AddMinutes(4));

        Assert.Equal(Now.AddMinutes(10), _db.GetRoutine(routine.Id)!.LastScheduledAt);
    }

    [Fact]
    public void DeleteForBuzzy_RemovesOnlyThatBuzzysRoutinesAndRuns()
    {
        var scouts = Routines.Every("scout", 2, Now);
        var buddys = Routines.Every("buddy", 2, Now);
        _db.SaveRoutine(scouts);
        _db.SaveRoutine(buddys);
        _db.TryAddRun(Run(scouts, "k"));
        _db.TryAddRun(Run(buddys, "k"));

        _db.DeleteForBuzzy("scout");

        Assert.Equal([buddys.Id], _db.ListRoutines().Select(r => r.Id));
        Assert.Equal(["buddy"], _db.ListRecentRuns(null, 10).Select(r => r.BuzzyId));
    }

    [Fact]
    public void ListRecentRuns_NewestFirst_ForOneBuzzyOrAll()
    {
        var routine = Routines.Every("scout", 2, Now);
        _db.TryAddRun(Run(routine, "old") with { QueuedAt = Now });
        _db.TryAddRun(Run(routine, "new") with { QueuedAt = Now.AddMinutes(5) });
        _db.TryAddRun(Run(Routines.Every("buddy", 2, Now), "x"));

        Assert.Equal(["new", "old"], _db.ListRecentRuns("scout", 10).Select(r => r.OccurrenceKey));
        Assert.Equal(3, _db.ListRecentRuns(null, 10).Count);
    }

    [Fact]
    public void Reopening_KeepsData_AndUsesWal()
    {
        var routine = Routines.Every("scout", 2, Now);
        _db.SaveRoutine(routine);

        var reopened = new BuzzyDb(Path.Combine(_temp.Path, "buzzies.db"));

        Assert.NotNull(reopened.GetRoutine(routine.Id));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_temp.Path, "buzzies.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar() as string);
    }
}
