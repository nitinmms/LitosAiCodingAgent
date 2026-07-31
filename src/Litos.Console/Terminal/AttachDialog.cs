using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// The file picker /attach opens when run interactively without a URL argument. Wraps
/// Terminal.Gui's own built-in OpenDialog (Terminal.Gui.Views.FileDialog's OpenMode.Mixed
/// subclass) rather than hand-rolling one — same "lean on the built-in component" approach
/// PickerDialog took for /resume, /model, /provider (ReadMe_AgentDesign.md §7.5/§7.6).
///
/// /attach's caller (TryHandleSlashCommandAsync) is invoked from Composer.Submitted, which is
/// raised SYNCHRONOUSLY from Composer.OnKeyDown — itself a real Terminal.Gui KeyDown handler,
/// called from inside the outer Run(litosApp, ...) loop's own key-dispatch for that iteration.
/// Two approaches were tried and empirically ruled out for this exact call shape:
///
/// - IApplication.Run(dialog, ...) (mirrors PickerDialog's non-active-loop branch): nests a
///   second blocking RunLoop inside the outer loop's own key-dispatch. The dialog itself works
///   (opens, responds to input, closes), but afterward the outer loop's own Invoke/TimedEvents
///   processing is left broken — printLine calls silently stop reaching the transcript and typed
///   slash commands stop being recognized, persisting past the dialog closing.
/// - IApplication.Begin(dialog) + blocking-wait via tcs.Task.GetAwaiter().GetResult() (mirrors
///   PickerDialog's active-loop branch): freezes the whole app. RaiseIteration() — the only thing
///   that can ever set dialog.StopRequested via our Iteration handler — is called exclusively
///   from inside Coordinator.RunIteration(), AFTER key dispatch for the current iteration
///   finishes. Blocking synchronously inside that same key dispatch waits on an event that
///   structurally cannot fire until the blocking call returns — true deadlock, confirmed
///   empirically (mouse/keyboard input stopped responding entirely).
///
/// The only combination that neither nests a nor blocks the loop: Begin (push the dialog onto
/// the SAME session stack the active outer loop is already driving — IApplication.SessionStack's
/// own doc confirms other sessions "continue to be laid out, drawn, and receive iteration
/// events") plus a genuinely non-blocking handoff back to the caller via
/// TaskCompletionSource/await, so control actually returns through Composer.Submitted -> OnKeyDown
/// -> the outer loop's key-dispatch -> that iteration's own RaiseIteration() call, which is what
/// lets our Iteration handler ever observe dialog.StopRequested becoming true. Confirmed
/// end-to-end against the real interactive app: opens without freezing, multi-select works, and
/// the "Attached '...'" confirmation lines land in the transcript with no aftereffects on
/// subsequent slash commands.
/// </summary>
public static class AttachDialog
{
    /// <summary>
    /// Shows the picker and returns the selected paths (empty if canceled) once the dialog
    /// closes, without blocking the calling thread while it's open. Must be called from the main
    /// UI thread while an outer Run loop is already active (true for /attach's only caller,
    /// TryHandleSlashCommandAsync via Composer.Submitted -- Begin/Iteration/End are Terminal.Gui
    /// main-loop APIs, not safe to call from a background thread). <paramref name="startPath"/>
    /// seeds the dialog's starting location — /attach's argument when given, current directory
    /// otherwise.
    /// </summary>
    public static Task<IReadOnlyList<string>> PickFilesAsync(IApplication app, string startPath)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();
        var dialog = CreateDialog(startPath);
        var token = app.Begin(dialog);
        if (token is null)
        {
            tcs.SetResult([]);
            return tcs.Task;
        }

        void OnIteration(object? sender, EventArgs e)
        {
            if (!dialog.StopRequested)
                return;

            app.Iteration -= OnIteration;
            app.End(token);
            tcs.SetResult(dialog.FilePaths);
        }

        app.Iteration += OnIteration;
        return tcs.Task;
    }

    private static OpenDialog CreateDialog(string startPath) => new()
    {
        Title = "Attach file(s)",
        OpenMode = OpenMode.File,
        AllowsMultipleSelection = true,
        Path = startPath,
    };
}
