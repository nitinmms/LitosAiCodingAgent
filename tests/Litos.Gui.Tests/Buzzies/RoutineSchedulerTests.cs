using Litos.Gui.Buzzies.Routines;

namespace Litos.Gui.Tests.Buzzies;

public class RoutineSchedulerTests
{
    private static readonly DateTimeOffset Start = Routines.Monday9.AddMinutes(-1); // 08:59 Monday

    [Fact]
    public async Task DueRoutine_RunsOnce_EvenAcrossManyTicks()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        h.Db.SaveRoutine(Routines.Daily(scout.Id, new TimeOnly(9, 0), Start));

        h.Clock.Advance(TimeSpan.FromSeconds(75)); // 09:00:15
        await h.TickAndSettleAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(15));
        await h.TickAndSettleAsync();
        await h.TickAndSettleAsync();

        Assert.Equal(1, h.Executor.StartedCount);
        var run = Assert.Single(h.Db.ListRecentRuns(scout.Id, 10));
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("2026-11-23T09:00", run.OccurrenceKey);
        Assert.Equal(["reports/out.md"], run.Outputs);
        Assert.NotNull(run.SessionId);
    }

    [Fact]
    public async Task Restart_AtTheSameMoment_DoesNotRunTheOccurrenceAgain()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        h.Db.SaveRoutine(Routines.Daily(scout.Id, new TimeOnly(9, 0), Start));
        h.Clock.Advance(TimeSpan.FromSeconds(75));
        await h.TickAndSettleAsync();

        // A second scheduler on the same database (Litos restarted, or a second window that lost
        // the lock but ticked anyway) still can't claim the same occurrence.
        var executor = new FakeExecutor();
        await using var again = new RoutineScheduler(h.Db, h.Store, executor, h.Clock);
        await again.TickAsync();
        await again.WhenIdleAsync();

        Assert.Equal(0, executor.StartedCount);
        Assert.Single(h.Db.ListRecentRuns(scout.Id, 10));
    }

    [Fact]
    public async Task ClosedOverTheScheduledTime_RecordsMissed_AndRunsNothing()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        h.Db.SaveRoutine(Routines.Daily(scout.Id, new TimeOnly(9, 0), Start));

        h.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(3)); // reopened Wednesday 12:00
        await h.TickAndSettleAsync();

        Assert.Equal(0, h.Executor.StartedCount);
        var runs = h.Db.ListRecentRuns(scout.Id, 10);
        Assert.Equal(3, runs.Count);
        Assert.All(runs, r => Assert.Equal(RunStatus.Missed, r.Status));

        await h.TickAndSettleAsync(); // and they're never recorded twice
        Assert.Equal(3, h.Db.ListRecentRuns(scout.Id, 10).Count);
    }

    [Fact]
    public async Task CatchUpPolicy_RunsOnce_ForTheNewestMissedOccurrence()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        h.Db.SaveRoutine(Routines.Daily(scout.Id, new TimeOnly(9, 0), Start, missed: MissedRunPolicy.CatchUpOnce));

        h.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(3));
        await h.TickAndSettleAsync();
        await h.Executor.StartedAtLeastAsync(1);

        Assert.Equal(1, h.Executor.StartedCount);
        var runs = h.Db.ListRecentRuns(scout.Id, 10);
        Assert.Single(runs, r => r.Trigger == RunTrigger.CatchUp && r.Status == RunStatus.Succeeded);
        Assert.Equal(2, runs.Count(r => r.Status == RunStatus.Missed));
    }

    [Fact]
    public async Task WhileTheUserChatsWithTheBuzzy_TheRunWaits_ThenStarts()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        var routine = Routines.Manual(scout.Id, Start);
        h.Db.SaveRoutine(routine);

        h.Scheduler.BeginInteractive(scout.Id);
        await h.Scheduler.RunNowAsync(routine.Id);
        await h.TickAndSettleAsync();
        Assert.Equal(0, h.Executor.StartedCount);
        Assert.Equal(RunStatus.Queued, h.Db.ListRecentRuns(scout.Id, 1)[0].Status);

        h.Scheduler.EndInteractive(scout.Id);
        await h.TickAndSettleAsync();
        await h.Executor.StartedAtLeastAsync(1);
        Assert.Equal(1, h.Executor.StartedCount);
    }

    [Fact]
    public async Task OneRunPerBuzzy_TheSecondWaitsForTheFirst()
    {
        await using var h = new SchedulerHarness(Start);
        h.Executor.Hold = true;
        var scout = h.Buzzy();
        var a = Routines.Manual(scout.Id, Start, "A");
        var b = Routines.Manual(scout.Id, Start, "B");
        h.Db.SaveRoutine(a);
        h.Db.SaveRoutine(b);

        await h.Scheduler.RunNowAsync(a.Id);
        await h.Scheduler.RunNowAsync(b.Id);
        await h.Scheduler.TickAsync();
        // The first run is claimed on the tick, then enters the executor on the thread pool, so wait
        // for it rather than assuming it has started by the time the tick returns.
        await h.Executor.StartedAtLeastAsync(1);
        Assert.Equal(1, h.Executor.StartedCount);
        Assert.NotNull(h.Scheduler.RunningFor(scout.Id));

        h.Executor.Complete(h.Executor.Started[0]);
        await WaitUntilAsync(() => h.Executor.StartedCount == 2);
        h.Executor.Complete(h.Executor.Started[1]);
        await h.Scheduler.WhenIdleAsync();
        Assert.Null(h.Scheduler.RunningFor(scout.Id));
    }

    [Fact]
    public async Task GlobalLimit_CapsRunsAcrossBuzzies()
    {
        await using var h = new SchedulerHarness(Start, new RoutineSchedulerOptions { MaxConcurrent = () => 2 });
        h.Executor.Hold = true;
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            var buzzy = h.Buzzy(name);
            var routine = Routines.Manual(buzzy.Id, Start);
            h.Db.SaveRoutine(routine);
            await h.Scheduler.RunNowAsync(routine.Id);
        }

        await h.Scheduler.TickAsync();
        await h.Executor.StartedAtLeastAsync(2);

        Assert.Equal(2, h.Executor.StartedCount);
        foreach (var request in h.Executor.Started.ToList())
            h.Executor.Complete(request);
        await WaitUntilAsync(() => h.Executor.StartedCount == 3);
        h.Executor.Complete(h.Executor.Started[2]);
        await h.Scheduler.WhenIdleAsync();
    }

    [Fact]
    public async Task Cancel_ARunningRun_EndsCancelled()
    {
        await using var h = new SchedulerHarness(Start);
        h.Executor.Hold = true;
        var scout = h.Buzzy();
        var routine = Routines.Manual(scout.Id, Start);
        h.Db.SaveRoutine(routine);
        var run = await h.Scheduler.RunNowAsync(routine.Id);
        await h.Scheduler.TickAsync();
        await WaitUntilAsync(() => h.Executor.StartedCount == 1);

        Assert.True(h.Scheduler.Cancel(run!.Id));
        await h.Scheduler.WhenIdleAsync();

        Assert.Equal(RunStatus.Cancelled, h.Db.GetRun(run.Id)!.Status);
    }

    [Fact]
    public async Task Cancel_AQueuedRun_EndsCancelled_AndNeverStarts()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        var routine = Routines.Manual(scout.Id, Start);
        h.Db.SaveRoutine(routine);
        h.Scheduler.BeginInteractive(scout.Id);
        var run = await h.Scheduler.RunNowAsync(routine.Id);

        Assert.True(h.Scheduler.Cancel(run!.Id));
        h.Scheduler.EndInteractive(scout.Id);
        await h.TickAndSettleAsync();

        Assert.Equal(0, h.Executor.StartedCount);
        Assert.Equal(RunStatus.Cancelled, h.Db.GetRun(run.Id)!.Status);
    }

    [Fact]
    public async Task QueuedTooLong_IsSkipped()
    {
        await using var h = new SchedulerHarness(Start, new RoutineSchedulerOptions { MaxQueueWait = TimeSpan.FromMinutes(30) });
        var scout = h.Buzzy();
        var routine = Routines.Manual(scout.Id, Start);
        h.Db.SaveRoutine(routine);
        h.Scheduler.BeginInteractive(scout.Id);
        var run = await h.Scheduler.RunNowAsync(routine.Id);

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        await h.TickAndSettleAsync();

        Assert.Equal(RunStatus.Skipped, h.Db.GetRun(run!.Id)!.Status);
        Assert.Equal(0, h.Executor.StartedCount);
    }

    [Fact]
    public async Task RunForADeletedBuzzy_FailsInsteadOfRunning()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        var routine = Routines.Manual(scout.Id, Start);
        h.Db.SaveRoutine(routine);
        h.Scheduler.BeginInteractive(scout.Id);
        var run = await h.Scheduler.RunNowAsync(routine.Id);
        h.Store.Delete(scout.Id);

        h.Scheduler.EndInteractive(scout.Id);
        await h.TickAndSettleAsync();

        Assert.Equal(RunStatus.Failed, h.Db.GetRun(run!.Id)!.Status);
        Assert.Equal(0, h.Executor.StartedCount);
    }

    [Fact]
    public async Task Start_MarksRunsLeftRunningAsInterrupted_AndTakesTheLock()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        var routine = Routines.Manual(scout.Id, Start);
        var left = new RoutineRun("left", routine.Id, scout.Id, "k", RunTrigger.Manual, RunStatus.Queued, Start);
        h.Db.TryAddRun(left);
        h.Db.TryMarkRunning("left", "s", Start);

        h.Scheduler.Start();
        await using var second = new RoutineScheduler(h.Db, h.Store, new FakeExecutor(), h.Clock);
        second.Start();

        Assert.Equal(RunStatus.Interrupted, h.Db.GetRun("left")!.Status);
        Assert.True(h.Scheduler.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public async Task Dispose_CancelsARunInProgress_AndSaysLitosClosed()
    {
        var h = new SchedulerHarness(Start);
        h.Executor.Hold = true;
        var scout = h.Buzzy();
        var routine = Routines.Manual(scout.Id, Start);
        h.Db.SaveRoutine(routine);
        var run = await h.Scheduler.RunNowAsync(routine.Id);
        await h.Scheduler.TickAsync();
        await WaitUntilAsync(() => h.Executor.StartedCount == 1);

        await h.DisposeAsyncKeepingDb();

        var saved = h.Db.GetRun(run!.Id)!;
        Assert.Equal(RunStatus.Cancelled, saved.Status);
        Assert.Contains("closed", saved.Error);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task RunEvents_AreRaised()
    {
        await using var h = new SchedulerHarness(Start);
        var scout = h.Buzzy();
        var routine = Routines.Manual(scout.Id, Start);
        h.Db.SaveRoutine(routine);
        var started = 0;
        RoutineRun? finished = null;
        h.Scheduler.RunStarted += _ => Interlocked.Increment(ref started);
        h.Scheduler.RunFinished += r => finished = r;

        await h.Scheduler.RunNowAsync(routine.Id);
        await h.TickAndSettleAsync();
        await WaitUntilAsync(() => finished is not null);

        Assert.Equal(1, started);
        Assert.Equal(RunStatus.Succeeded, finished!.Status);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "timed out waiting for the scheduler");
    }
}

internal static class SchedulerHarnessExtensions
{
    /// <summary>Disposes just the scheduler, so the test can still read the database.</summary>
    public static ValueTask DisposeAsyncKeepingDb(this SchedulerHarness harness) => harness.Scheduler.DisposeAsync();
}
