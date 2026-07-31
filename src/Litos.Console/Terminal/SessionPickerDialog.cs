using Litos.Agent.Session;
using Terminal.Gui.App;

namespace Litos.Console.Terminal;

/// <summary>Replaces Rendering/SessionPicker.cs's Spectre Table + SelectionPrompt&lt;T&gt; usage.</summary>
public static class SessionPickerDialog
{
    /// <summary>
    /// One-shot listing (non-interactive). Takes a line-printer rather than writing to
    /// System.Console directly: the interactive caller passes a printer that routes through
    /// TranscriptView/app.Invoke (this can be reached from a background thread-pool
    /// continuation, same as every other slash-command branch — writing straight to the console
    /// from there would race Terminal.Gui's own concurrent redraw of the inline region); the
    /// non-interactive caller passes plain System.Console.WriteLine.
    /// </summary>
    public static void RenderTable(IReadOnlyList<SessionSummary> sessions, Action<string> printLine)
    {
        if (sessions.Count == 0)
        {
            printLine("No sessions found.");
            return;
        }

        printLine($"{"Session",-34} {"Last updated",-20} {"Msgs",-6} Preview");
        foreach (var session in sessions)
        {
            printLine(
                $"{session.SessionId,-34} {session.LastUpdatedAt.ToLocalTime():g,-20} {session.MessageCount,-6} {SingleLinePreview(session.FirstUserMessagePreview)}");
        }
    }

    public static string? PickSessionId(IApplication app, IReadOnlyList<SessionSummary> sessions)
    {
        if (sessions.Count == 0)
            return null;

        string Label(SessionSummary s) => $"{s.SessionId}  ({s.LastUpdatedAt.ToLocalTime():g}, {s.MessageCount} msgs) - {SingleLinePreview(s.FirstUserMessagePreview)}";

        var picked = PickerDialog<SessionSummary>.Pick(app, "Select a session to resume", sessions, Label);
        return picked?.SessionId;
    }

    private const int PreviewMaxLength = 60;

    // PickerDialog<T>'s label round-trips through a single-line TextField: TextField's own
    // cursor-insertion logic treats embedded '\n'/'\r' as cursor-movement rather than literal
    // text, scrambling the label into something that no longer matches labelSelector(i) for ANY
    // item once it's inserted from the Autocomplete popup -- silently breaking PickerDialog<T>.
    // Accept()'s match and making /resume behave as if nothing was picked (confirmed empirically:
    // a real multi-line message preview, e.g. a markdown reply with embedded newlines, made the
    // picked session's label come back scrambled and PickSessionId return null even after a real
    // arrow-keys-then-Enter selection). Collapse newlines before they ever reach a label, and cap
    // the length so one long opening message can't blow out the table/popup width.
    private static string? SingleLinePreview(string? preview)
    {
        if (preview is null)
            return null;

        var singleLine = preview.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length > PreviewMaxLength ? singleLine[..PreviewMaxLength] + "…" : singleLine;
    }
}
