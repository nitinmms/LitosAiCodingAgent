using Litos.Gui.Buzzies.Routines;

namespace Litos.Gui.Tests.Buzzies;

public class RoutineScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Created = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero); // a Monday

    /// <summary>UTC+0 in winter, UTC+1 from the last Sunday of March 01:00 to the last Sunday of
    /// October 02:00 — like Europe/London, but defined here so the test doesn't depend on the OS.</summary>
    private static readonly TimeZoneInfo SummerTime = TimeZoneInfo.CreateCustomTimeZone(
        "Test/SummerTime", TimeSpan.Zero, "Summer time", "Winter", "Summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    [Fact]
    public void Daily_NextIsTodayIfStillAhead_ElseTomorrow()
    {
        var routine = Routines.Daily("scout", new TimeOnly(9, 0), Created);

        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero), RoutineSchedule.Next(routine, Created, Utc));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), RoutineSchedule.Next(routine, Created.AddHours(1), Utc));
    }

    [Fact]
    public void Weekly_NextIsTheNamedDay()
    {
        var routine = Routines.Daily("scout", new TimeOnly(17, 30), Created) with { Trigger = RoutineTrigger.Weekly, Day = DayOfWeek.Friday };

        Assert.Equal(new DateTimeOffset(2026, 11, 27, 17, 30, 0, TimeSpan.Zero), RoutineSchedule.Next(routine, Created, Utc));
        Assert.Equal(new DateTimeOffset(2026, 12, 4, 17, 30, 0, TimeSpan.Zero),
            RoutineSchedule.Next(routine, new DateTimeOffset(2026, 11, 27, 17, 30, 0, TimeSpan.Zero), Utc));
    }

    [Fact]
    public void Interval_StepsFromCreation()
    {
        var routine = Routines.Every("scout", 2, Created);

        Assert.Equal(Created.AddMinutes(2), RoutineSchedule.Next(routine, Created, Utc));
        Assert.Equal(Created.AddMinutes(6), RoutineSchedule.Next(routine, Created.AddMinutes(5), Utc));
    }

    [Fact]
    public void Manual_HasNoNextOccurrence() =>
        Assert.Null(RoutineSchedule.Next(Routines.Manual("scout", Created), Created, Utc));

    [Fact]
    public void DailyTimeInTheSpringGap_RunsAtTheEndOfTheGap()
    {
        // 2026-03-29 is the last Sunday of March: local 01:00–02:00 doesn't exist.
        var routine = Routines.Daily("scout", new TimeOnly(1, 30), Created);

        var next = RoutineSchedule.Next(routine, new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), SummerTime);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), next!.Value.ToUniversalTime()); // 02:00 local, UTC+1
    }

    [Fact]
    public void DailyTimeInTheAutumnOverlap_RunsOnce_AtTheFirstInstance()
    {
        // 2026-10-25: local 01:00–02:00 happens twice.
        var routine = Routines.Daily("scout", new TimeOnly(1, 30), Created);
        var after = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        var first = RoutineSchedule.Next(routine, after, SummerTime)!.Value;
        var second = RoutineSchedule.Next(routine, first, SummerTime)!.Value;

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), first.ToUniversalTime());
        Assert.Equal(new DateTime(2026, 10, 26), TimeZoneInfo.ConvertTime(second, SummerTime).Date);
    }

    [Fact]
    public void Plan_OnTime_RunsTheOccurrence_AndMovesTheMarkToIt()
    {
        var routine = Routines.Daily("scout", new TimeOnly(9, 0), Created);
        var now = new DateTimeOffset(2026, 11, 23, 9, 0, 20, TimeSpan.Zero);

        var plan = RoutineSchedule.Plan(routine, now, Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero), plan.RunNow);
        Assert.Equal(RunTrigger.Scheduled, plan.RunTrigger);
        Assert.Empty(plan.Missed);
        Assert.Equal(plan.RunNow, plan.ScheduledUpTo);
    }

    [Fact]
    public void Plan_NothingDue_DoesNothing()
    {
        var routine = Routines.Daily("scout", new TimeOnly(9, 0), Created);

        var plan = RoutineSchedule.Plan(routine, Created.AddMinutes(30), Utc);

        Assert.Null(plan.RunNow);
        Assert.Null(plan.ScheduledUpTo);
    }

    [Fact]
    public void Plan_LateBeyondGrace_RecordsMissed_AndDoesNotRun()
    {
        var routine = Routines.Daily("scout", new TimeOnly(9, 0), Created);
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero); // missed the 23rd, 24th and 25th

        var plan = RoutineSchedule.Plan(routine, now, Utc);

        Assert.Null(plan.RunNow);
        Assert.Equal(3, plan.Missed.Count);
        Assert.Equal(3, plan.MissedTotal);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), plan.ScheduledUpTo);
    }

    [Fact]
    public void Plan_LateWithCatchUp_RunsTheNewestOnce_AndRecordsTheRestAsMissed()
    {
        var routine = Routines.Daily("scout", new TimeOnly(9, 0), Created, missed: MissedRunPolicy.CatchUpOnce);
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        var plan = RoutineSchedule.Plan(routine, now, Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), plan.RunNow);
        Assert.Equal(RunTrigger.CatchUp, plan.RunTrigger);
        Assert.Equal(2, plan.Missed.Count);
    }

    [Fact]
    public void Plan_AfterTheMarkMovesOn_SameOccurrenceIsNeverPlannedAgain()
    {
        var routine = Routines.Daily("scout", new TimeOnly(9, 0), Created);
        var now = new DateTimeOffset(2026, 11, 23, 9, 0, 20, TimeSpan.Zero);
        var first = RoutineSchedule.Plan(routine, now, Utc);

        var second = RoutineSchedule.Plan(routine with { LastScheduledAt = first.ScheduledUpTo }, now.AddSeconds(15), Utc);

        Assert.Null(second.RunNow);
        Assert.Null(second.ScheduledUpTo);
    }

    [Fact]
    public void Plan_PausedOrManual_NeverRuns()
    {
        var now = new DateTimeOffset(2026, 11, 23, 9, 0, 20, TimeSpan.Zero);

        Assert.Null(RoutineSchedule.Plan(Routines.Daily("scout", new TimeOnly(9, 0), Created) with { Enabled = false }, now, Utc).RunNow);
        Assert.Null(RoutineSchedule.Plan(Routines.Manual("scout", Created), now, Utc).RunNow);
    }

    [Fact]
    public void Plan_LongAbsenceOfAOneMinuteRoutine_IsCountedNotWalked_AndRecordsAFew()
    {
        var routine = Routines.Every("scout", 1, Created);
        var now = Created.AddDays(30).AddSeconds(30); // ~43,200 occurrences

        var plan = RoutineSchedule.Plan(routine, now, Utc);

        Assert.Equal(Created.AddDays(30), plan.RunNow); // the newest is within grace
        Assert.Equal(43_199, plan.MissedTotal);
        Assert.Equal(RoutineSchedule.MaxMissedRecorded, plan.Missed.Count);
    }

    [Fact]
    public void OccurrenceKey_IsWallClockForDaily_AndUtcForInterval()
    {
        var daily = Routines.Daily("scout", new TimeOnly(9, 0), Created);
        var interval = Routines.Every("scout", 2, Created);
        var at = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-11-23T09:00", RoutineSchedule.OccurrenceKey(daily, at, Utc));
        Assert.Equal("2026-11-23T09:00Z", RoutineSchedule.OccurrenceKey(interval, at, Utc));
    }

    [Fact]
    public void Describe_ReadsNaturally()
    {
        Assert.Equal("Every day at 09:00", RoutineSchedule.Describe(Routines.Daily("s", new TimeOnly(9, 0), Created)));
        Assert.Equal("Every 2 minutes (for testing)", RoutineSchedule.Describe(Routines.Every("s", 2, Created)));
        Assert.Equal("Only when you press Run now", RoutineSchedule.Describe(Routines.Manual("s", Created)));
    }

    [Fact]
    public void UnknownTimeZone_FallsBackToLocal() =>
        Assert.Equal(TimeZoneInfo.Local, RoutineSchedule.ResolveTimeZone("Nowhere/Imaginary"));
}
