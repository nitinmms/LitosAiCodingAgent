using Litos.Tools.FileSystem;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// /reflect's confirm-before-write UI: an editable TextView pre-filled with the reflector's
/// proposed AGENTS.md content, plus Console's own DiffView (reused as-is, unlike Litos.Gui which
/// had to hand-reimplement DiffView's color rules in Avalonia) re-rendered on every keystroke
/// against the existing file content via DiffView.SetDiff. Reflector.ReflectAsync itself
/// (Litos.Agent) needs no changes — only this review-before-write UI is new.
///
/// Reached from active key dispatch (Composer.Submitted -> HandleSubmitAsync -> /reflect), so —
/// per AttachDialog's doc comment — this follows its Begin/Iteration/TaskCompletionSource pattern
/// rather than a nested app.Run, even though the dialog itself does no async work once open (the
/// caller already awaited Reflector.ReflectAsync before ShowAsync is called).
/// </summary>
public static class ReflectDialog
{
    /// <summary>
    /// Shows the review dialog and returns the (possibly user-edited) proposed content once
    /// confirmed, or null if the user cancels — the caller writes to disk only on a non-null
    /// result, never inside this dialog itself.
    /// </summary>
    public static Task<string?> ShowAsync(IApplication app, string? existingContent, string proposedContent, string path)
    {
        var dialog = new Dialog<string?>
        {
            Title = $"Review {path}",
            Width = Dim.Percent(90),
            Height = Dim.Percent(85),
        };

        var contentView = new TextView
        {
            Text = proposedContent,
            X = 0,
            Y = 0,
            Width = Dim.Percent(50),
            Height = Dim.Fill() - 1,
        };

        var diffView = new DiffView(UnifiedDiff.Render(existingContent, proposedContent, path))
        {
            X = Pos.Right(contentView) + 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1,
        };

        contentView.ContentsChanged += (_, _) =>
            diffView.SetDiff(UnifiedDiff.Render(existingContent, contentView.Text?.ToString() ?? string.Empty, path));

        dialog.Add(contentView, diffView);

        var confirmButton = new Button { Text = "Write", IsDefault = true };
        confirmButton.Accepting += (_, _) =>
        {
            dialog.Result = contentView.Text?.ToString() ?? proposedContent;
            dialog.RequestStop();
        };
        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Accepting += (_, _) =>
        {
            dialog.Result = null;
            dialog.RequestStop();
        };
        dialog.AddButton(confirmButton);
        dialog.AddButton(cancelButton);

        var tcs = new TaskCompletionSource<string?>();
        var token = app.Begin(dialog);
        if (token is null)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        void OnIteration(object? sender, EventArgs e)
        {
            if (!dialog.StopRequested)
                return;

            app.Iteration -= OnIteration;
            app.End(token);
            tcs.SetResult(dialog.Result);
        }

        app.Iteration += OnIteration;
        return tcs.Task;
    }
}
