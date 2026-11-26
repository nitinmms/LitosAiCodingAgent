using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Gui.Buzzies.Workflows;

namespace Litos.Gui.Buzzies;

/// <summary>What the user chose in <see cref="WorkflowReviewWindow"/>.</summary>
public sealed record WorkflowReviewResult(WorkflowDraft Draft, bool ThenSchedule);

/// <summary>
/// "Remember this workflow": shows the drafted skill for the user to read and edit before it is
/// saved (ReadMe_Buzzies.md §1A: "Show the user the generated skill and routine before enabling").
/// Save writes the next version; Save and repeat… also opens the routine editor pinned to it.
/// </summary>
public sealed class WorkflowReviewWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F48771"));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly TextBox _name = new();
    private readonly TextBox _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _body = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 300, MaxHeight = 420, FontFamily = Mono, FontSize = 12,
    };
    private readonly TextBlock _versionText = new() { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Warn, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly Func<string, int> _latestVersion;

    public WorkflowReviewWindow() : this(new WorkflowDraft("workflow", "", ""), "Buzzy", [], _ => 0)
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    public WorkflowReviewWindow(WorkflowDraft draft, string buzzyName, IReadOnlyList<string> toolsUsed, Func<string, int> latestVersion)
    {
        _latestVersion = latestVersion;
        Title = $"Remember this workflow — {buzzyName}";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        _name.Text = draft.Name;
        _description.Text = draft.Description;
        _body.Text = draft.Body;
        _name.TextChanged += (_, _) => RefreshVersion();

        var save = new Button { Content = "Save workflow", IsDefault = true };
        save.Click += (_, _) => Accept(thenSchedule: false);
        var schedule = new Button { Content = "Save and repeat…" };
        ToolTip.SetTip(schedule, "Save it, then choose when this Buzzy should repeat it");
        schedule.Click += (_, _) => Accept(thenSchedule: true);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Remember this workflow", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = $"{buzzyName} drafted these steps from this conversation, from what you asked and what it did " +
                               "(not from the pages or files it read). Edit anything before saving: this is what it will follow next time, " +
                               "including in routines nobody is watching.",
                        TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 12,
                    },
                    Label("Name"), _name, _versionText,
                    Label("When to use it"), _description,
                    Label("Steps (SKILL.md)"), _body,
                    new TextBlock
                    {
                        Text = toolsUsed.Count == 0 ? "No tools were used in this conversation." : $"Tools used in this conversation: {string.Join(", ", toolsUsed)}",
                        Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    },
                    _error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 6, 0, 0), Children = { cancel, schedule, save },
                    },
                },
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };
        RefreshVersion();
    }

    public static Task<WorkflowReviewResult?> ShowAsync(Window owner, WorkflowDraft draft, string buzzyName, IReadOnlyList<string> toolsUsed, Func<string, int> latestVersion) =>
        new WorkflowReviewWindow(draft, buzzyName, toolsUsed, latestVersion).ShowDialog<WorkflowReviewResult?>(owner);

    private void RefreshVersion()
    {
        var name = _name.Text?.Trim() ?? "";
        var latest = WorkflowStore.IsValidName(name) ? _latestVersion(name) : 0;
        _versionText.Text = latest == 0
            ? "Saved as version 1 of a new workflow."
            : $"A workflow with this name exists (version {latest}). Saving adds version {latest + 1}; routines already using it stay on their version until you move them.";
    }

    private void Accept(bool thenSchedule)
    {
        var name = _name.Text?.Trim() ?? "";
        var description = _description.Text?.Trim() ?? "";
        var body = _body.Text?.Trim() ?? "";
        if (!WorkflowStore.IsValidName(name))
        {
            ShowError("Use a name of lower-case letters, digits and hyphens, starting with a letter, such as weekly-model-report.");
            return;
        }
        if (description.Length == 0 || body.Length == 0)
        {
            ShowError("Say when to use it, and keep at least one step.");
            return;
        }
        Close(new WorkflowReviewResult(new WorkflowDraft(name, description, body), thenSchedule));
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 6, 0, 0) };
}
