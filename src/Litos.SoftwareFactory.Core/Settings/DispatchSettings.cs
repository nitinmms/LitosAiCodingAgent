namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>
/// Whether the coordinator may start runs (m4-architecture.md §8.1). An Admin pauses dispatch,
/// usually for a backup: nothing new starts, of any kind, and runs already working carry on to
/// their next stop. Kept in the database, so a restart, or a backup that failed half way, does
/// not quietly resume it.
/// </summary>
public sealed record DispatchSettings
{
    public bool Paused { get; init; }

    /// <summary>Why, as the waiting threads and the app's banner say it.</summary>
    public string? Reason { get; init; }

    public Guid? ChangedBy { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }

    public const int MaxReasonChars = 200;

    /// <summary>What a queued thread says while dispatch is paused; null while it runs.</summary>
    public string? WaitingReason => Paused
        ? string.IsNullOrWhiteSpace(Reason) ? "Dispatch is paused by an Admin." : $"Dispatch is paused by an Admin: {Reason}"
        : null;
}
