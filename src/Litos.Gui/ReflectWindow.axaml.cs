using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Litos.Tools.FileSystem;

namespace Litos.Gui;

/// <summary>
/// /reflect's confirm-before-write modal: shows the proposed new AGENTS.md content in an editable
/// TextBox (pre-filled with the reflector's output, but the user can freely tweak it) plus a
/// read-only, color-coded diff against the current content for reference. Plain Window opened via
/// a static async factory, mirroring ViewContextWindow/ListPickerWindow's convention — no
/// ViewModel, no IDialogService, this codebase has neither.
/// </summary>
public sealed partial class ReflectWindow : Window
{
    // Same line-classification rule as Litos.Console's DiffView (+++/--- bold, + green, - red) —
    // kept in sync by eye since Terminal.Gui and Avalonia have no shared rendering abstraction to
    // share this through.
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#6A9955"));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.Parse("#F14C4C"));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#D4D4D4"));

    public ReflectWindow()
    {
        InitializeComponent();
        // Parameterless constructor required by the Avalonia XAML previewer/loader.
    }

    /// <summary>
    /// Returns whatever text is in the editable box at Confirm time — the reflector's proposed
    /// content, or the user's edits to it — or null if the user cancels/closes without confirming.
    /// </summary>
    public static Task<string?> ShowAsync(Window owner, string existingContent, string proposedContent, string path)
    {
        var window = new ReflectWindow();
        window.ContentBox.Text = proposedContent;

        // Re-diffs against the original existingContent on every keystroke, not just once at
        // open, so the reference pane always reflects the user's current edits rather than only
        // the reflector's initial proposal.
        void UpdateDiff() => RenderDiff(window.DiffText, UnifiedDiff.Render(existingContent, window.ContentBox.Text ?? "", path));
        UpdateDiff();
        window.ContentBox.TextChanged += (_, _) => UpdateDiff();

        window.ConfirmButton.Click += (_, _) => window.Close(window.ContentBox.Text);
        window.CancelButton.Click += (_, _) => window.Close(null);
        return window.ShowDialog<string?>(owner);
    }

    private static void RenderDiff(TextBlock target, string unifiedDiff)
    {
        var lines = unifiedDiff.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        target.Inlines = [];

        foreach (var line in lines)
        {
            var brush = line switch
            {
                _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) => HeaderBrush,
                _ when line.StartsWith('+') => AddedBrush,
                _ when line.StartsWith('-') => RemovedBrush,
                _ => null,
            };

            target.Inlines.Add(new Run(line + Environment.NewLine) { Foreground = brush });
        }
    }
}
