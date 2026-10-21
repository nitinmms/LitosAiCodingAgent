namespace Litos.SoftwareFactory.Tray;

/// <summary>
/// The words the tray app shows: the reason a task waits, the notification for an alert, and the
/// icon's tooltip, each cut to fit what Windows shows (a notify-icon balloon's title is at most 63
/// characters and its text 255; the tooltip 127).
/// </summary>
public static class AlertText
{
    public const int TitleLimit = 63;
    public const int BodyLimit = 255;
    public const int TooltipLimit = 127;

    /// <summary>Why a task waits on its owner: as a phrase after its title, and as a short label.</summary>
    public static (string Phrase, string Label) Reason(TrayTask task) => task.State switch
    {
        "AwaitingDecision" => ("needs a decision", "Decision needed"),
        "AwaitingHumanTesting" => ("is ready for testing", "Ready for testing"),
        "Blocked" => ("is blocked", "Blocked"),
        "PausedBudget" => ("hit its budget", "Budget paused"),
        "Interrupted" => ("was interrupted", "Interrupted"),
        "Draft" => ("has a spec to approve", "Spec to approve"),
        _ => ("needs you", "Needs you"),
    };

    /// <summary>The notification for tasks that just started waiting.</summary>
    public static (string Title, string Body) Notification(IReadOnlyList<TrayTask> arrived)
    {
        if (arrived.Count == 1)
        {
            var task = arrived[0];
            var (phrase, label) = Reason(task);
            return (Fit($"{task.Title} {phrase}", TitleLimit), Fit(string.IsNullOrWhiteSpace(task.StateReason) ? label : task.StateReason.Trim(), BodyLimit));
        }

        var lines = arrived.Select(t => $"• {t.Title}: {Reason(t).Label.ToLowerInvariant()}");
        return ($"{arrived.Count} tasks need you", Fit(string.Join('\n', lines), BodyLimit));
    }

    /// <summary>The icon's tooltip.</summary>
    public static string Tooltip(WatcherState state, int awaiting, bool muted)
    {
        var text = state.Status switch
        {
            ConnectionStatus.SignInNeeded => "Litos Factory: sign in needed",
            ConnectionStatus.Reconnecting => "Litos Factory: reconnecting…",
            ConnectionStatus.Connecting => "Litos Factory: connecting…",
            _ when awaiting == 1 => "Litos Factory: 1 task awaits you",
            _ when awaiting > 1 => $"Litos Factory: {awaiting} tasks await you",
            _ => "Litos Factory: nothing awaits you",
        };
        return Fit(muted ? text + " (muted)" : text, TooltipLimit);
    }

    /// <summary>The text, cut at <paramref name="limit"/> characters with an ellipsis when longer.</summary>
    public static string Fit(string text, int limit) =>
        text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit - 1).TrimEnd(), "…");
}
