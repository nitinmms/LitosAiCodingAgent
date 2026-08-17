using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// "/md": shows the last completed assistant reply through a real Terminal.Gui.Views.Markdown
/// view (Markdig-backed CommonMark rendering — headings, emphasis, code blocks, tables, links)
/// inside a Dialog, rather than the plain-text TranscriptView it normally scrolls past in. Slice
/// 3.1 Step A's isolated proving ground for whether Markdown behaves well under this app's
/// AppModel.Inline driver before committing to Step B's larger TranscriptView restructure (see
/// the plan's Risk #1) — read-only, no mutation, no async work, so a plain on-thread app.Run is
/// safe here exactly like ContextBreakdownDialog.
/// </summary>
public static class MarkdownViewDialog
{
    public static void Show(IApplication app, string markdownText)
    {
        var dialog = new Dialog<bool>
        {
            Title = "Last reply (rendered)",
            Width = Dim.Percent(85),
            Height = Dim.Percent(80),
        };

        var markdownView = new Markdown
        {
            Text = markdownText,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1,
        };
        dialog.Add(markdownView);

        var closeButton = new Button { Text = "Close", IsDefault = true };
        closeButton.Accepting += (_, _) => dialog.RequestStop();
        dialog.AddButton(closeButton);

        app.Run(dialog, null);
    }
}
