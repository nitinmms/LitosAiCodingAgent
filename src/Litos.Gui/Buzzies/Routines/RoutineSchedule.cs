using System.Globalization;

namespace Litos.Gui.Buzzies.Routines;

/// <summary>What the scheduler should do for one routine at one tick.</summary>
/// <param name="RunNow">The occurrence to queue now: due within the grace window, or the one catch-up.</param>
/// <param name="Missed">Occurrences to record as missed, newest last (capped: see MaxMissedRecorded).</param>
/// <param name="MissedTotal">How many occurrences were missed in all, which can exceed what is recorded.</param>
/// <param name="ScheduledUpTo">The new LastScheduledAt: the latest occurrence dealt with, or null if none was due.</param>
public sealed record SchedulePlan(
    DateTimeOffset? RunNow,
    RunTrigger RunTrigger,
    IReadOnlyList<DateTimeOffset> Missed,
    int MissedTotal,
    DateTimeOffset? ScheduledUpTo);

/// <summary>
/// Scheduling arithmetic, free of I/O and clocks so it is fully testable. Daily and weekly times
/// are wall-clock times in the routine's time zone, so "every day at 09:00" stays at 09:00 across
/// a daylight-saving change; a time that doesn't exist that day (clocks jump forward) runs at the
/// first valid moment after it, and a repeated time (clocks go back) runs once, at its first instance.
/// </summary>
public static class RoutineSchedule
{
    /// <summary>An occurrence this late is still run; anything later counts as missed.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    internal const int MaxMissedRecorded = 10;

    /// <summary>The routine's time zone, or the machine's when its id isn't known here (an id saved
    /// on another OS, or an IANA id while globalization data is unavailable).</summary>
    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>The first occurrence strictly after <paramref name="after"/>, or null for a
    /// manual routine or one missing the fields its trigger needs.</summary>
    public static DateTimeOffset? Next(Routine routine, DateTimeOffset after, TimeZoneInfo zone)
    {
        switch (routine.Trigger)
        {
            case RoutineTrigger.Interval when routine.IntervalMinutes is >= Routine.MinIntervalMinutes:
            {
                var step = TimeSpan.FromMinutes(routine.IntervalMinutes.Value);
                var k = Math.Floor((after - routine.CreatedAt) / step) + 1;
                return routine.CreatedAt + step * Math.Max(1, k);
            }
            case RoutineTrigger.Daily when routine.TimeOfDay is { } time:
                return FirstLocalOccurrenceAfter(after, zone, time, day: null);
            case RoutineTrigger.Weekly when routine.TimeOfDay is { } time && routine.Day is { } day:
                return FirstLocalOccurrenceAfter(after, zone, time, day);
            default:
                return null;
        }
    }

    /// <summary>Every occurrence in (<paramref name="after"/>, <paramref name="until"/>], oldest
    /// first, keeping only the newest <paramref name="keepLast"/>; also returns how many there were.</summary>
    public static (IReadOnlyList<DateTimeOffset> Occurrences, int Total) Between(
        Routine routine, DateTimeOffset after, DateTimeOffset until, TimeZoneInfo zone, int keepLast)
    {
        if (until <= after)
            return ([], 0);

        if (routine.Trigger == RoutineTrigger.Interval && routine.IntervalMinutes is >= Routine.MinIntervalMinutes)
        {
            // Arithmetic, not a walk: a 1-minute routine left alone for a month has ~43,000.
            var step = TimeSpan.FromMinutes(routine.IntervalMinutes.Value);
            var first = (long)Math.Max(1, Math.Floor((after - routine.CreatedAt) / step) + 1);
            var last = (long)Math.Floor((until - routine.CreatedAt) / step);
            if (last < first)
                return ([], 0);
            var total = (int)Math.Min(int.MaxValue, last - first + 1);
            var from = Math.Max(first, last - keepLast + 1);
            List<DateTimeOffset> kept = [];
            for (var k = from; k <= last; k++)
                kept.Add(routine.CreatedAt + step * k);
            return (kept, total);
        }

        var all = new List<DateTimeOffset>();
        var cursor = after;
        // A daily routine over a long absence walks one step per day; capped at ~3 years.
        for (var guard = 0; guard < 1200 && Next(routine, cursor, zone) is { } next && next <= until; guard++)
        {
            all.Add(next);
            cursor = next;
        }

        return (all.Count > keepLast ? all[^keepLast..] : all, all.Count);
    }

    /// <summary>
    /// Decides one tick. The newest due occurrence runs if it is no more than <see cref="Grace"/>
    /// late; every older one, and that one too if it is later than that, is missed: Litos was
    /// closed, the computer slept, or the scheduler fell behind. A missed run is recorded, not run,
    /// unless the routine asks for one catch-up, which is then the newest missed occurrence.
    /// </summary>
    public static SchedulePlan Plan(Routine routine, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!routine.Enabled || routine.Trigger == RoutineTrigger.Manual)
            return new SchedulePlan(null, RunTrigger.Scheduled, [], 0, null);

        var from = routine.LastScheduledAt ?? routine.CreatedAt;
        var (due, total) = Between(routine, from, now, zone, MaxMissedRecorded + 1);
        if (total == 0)
            return new SchedulePlan(null, RunTrigger.Scheduled, [], 0, null);

        var latest = due[^1];
        if (now - latest <= Grace)
        {
            var missed = due.Take(due.Count - 1).TakeLast(MaxMissedRecorded).ToList();
            return new SchedulePlan(latest, RunTrigger.Scheduled, missed, total - 1, latest);
        }

        var allMissed = due.TakeLast(MaxMissedRecorded).ToList();
        return routine.MissedRuns == MissedRunPolicy.CatchUpOnce
            ? new SchedulePlan(latest, RunTrigger.CatchUp, allMissed.Take(allMissed.Count - 1).ToList(), total - 1, latest)
            : new SchedulePlan(null, RunTrigger.Scheduled, allMissed, total, latest);
    }

    /// <summary>The key that makes an occurrence unique within its routine: the wall-clock time in
    /// the routine's zone for daily and weekly, the UTC instant for intervals.</summary>
    public static string OccurrenceKey(Routine routine, DateTimeOffset occurrence, TimeZoneInfo zone) =>
        routine.Trigger == RoutineTrigger.Interval
            ? occurrence.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture)
            : TimeZoneInfo.ConvertTime(occurrence, zone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"Every day at 09:00", for the routines list.</summary>
    public static string Describe(Routine routine) => routine.Trigger switch
    {
        RoutineTrigger.Daily => $"Every day at {routine.TimeOfDay:HH\\:mm}",
        RoutineTrigger.Weekly => $"Every {routine.Day} at {routine.TimeOfDay:HH\\:mm}",
        RoutineTrigger.Interval => routine.IntervalMinutes == 1 ? "Every minute (for testing)" : $"Every {routine.IntervalMinutes} minutes (for testing)",
        _ => "Only when you press Run now",
    };

    private static DateTimeOffset FirstLocalOccurrenceAfter(DateTimeOffset after, TimeZoneInfo zone, TimeOnly time, DayOfWeek? day)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        for (var offset = -1; offset <= 8; offset++)
        {
            var date = localDate.AddDays(offset);
            if (day is { } wanted && date.DayOfWeek != wanted)
                continue;
            var candidate = ToInstant(date.ToDateTime(time), zone);
            if (candidate > after)
                return candidate;
        }

        throw new InvalidOperationException("No occurrence within the next week.");
    }

    /// <summary>A wall-clock time in <paramref name="zone"/> as an instant: a skipped time moves to
    /// the end of the gap, a repeated one takes its first (earlier) instance.</summary>
    internal static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
