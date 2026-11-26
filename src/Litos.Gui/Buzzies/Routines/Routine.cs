namespace Litos.Gui.Buzzies.Routines;

/// <summary>When a routine runs. Interval exists for testing and demos ("every 2 minutes"), as
/// ReadMe_Buzzies.md asks for a short test interval rather than a fabricated overnight run.</summary>
public enum RoutineTrigger { Manual, Daily, Weekly, Interval }

/// <summary>What happens to occurrences that passed while Litos was closed or the computer slept.</summary>
public enum MissedRunPolicy { Skip, CatchUpOnce }

/// <summary>
/// A task a Buzzy repeats without the user retyping it (Implement_Buzzies.md, Phase 1). Stored in
/// buzzies.db. <see cref="LastScheduledAt"/> is the latest occurrence the scheduler has already
/// dealt with (run, queued or recorded as missed), so nothing before it is ever considered again.
/// </summary>
public sealed record Routine(
    string Id,
    string BuzzyId,
    string Name,
    string Prompt,
    RoutineTrigger Trigger,
    TimeOnly? TimeOfDay,
    DayOfWeek? Day,
    int? IntervalMinutes,
    string TimeZoneId,
    bool Enabled,
    int MaxMinutes,
    int MaxTokens,
    MissedRunPolicy MissedRuns,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastScheduledAt = null)
{
    public const int DefaultMaxMinutes = 10;
    public const int DefaultMaxTokens = 200_000;
    public const int MinIntervalMinutes = 1;
}

/// <summary>Queued → Running → Succeeded / Failed / Cancelled, plus three states a run can end in
/// without ever running: Missed (its time passed while Litos was closed or asleep), Skipped (its
/// Buzzy stayed busy too long) and Interrupted (Litos stopped mid-run; surfaced, never retried,
/// since a half-done run may already have written files).</summary>
public enum RunStatus { Queued, Running, Succeeded, Failed, Cancelled, Missed, Skipped, Interrupted }

public enum RunTrigger { Scheduled, Manual, CatchUp }

/// <summary>One run of a routine. <see cref="OccurrenceKey"/> is unique per routine, so the same
/// scheduled occurrence can never be queued twice, even across restarts or two Litos windows.</summary>
public sealed record RoutineRun(
    string Id,
    string RoutineId,
    string BuzzyId,
    string OccurrenceKey,
    RunTrigger Trigger,
    RunStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? SessionId = null,
    string? Summary = null,
    string? Error = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    IReadOnlyList<string>? Outputs = null)
{
    public bool IsActive => Status is RunStatus.Queued or RunStatus.Running;
}
