using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Gui.Buzzies.Routines;

namespace Litos.Gui.Buzzies;

/// <summary>
/// New or edit a routine: which Buzzy, what it should do, when, and its limits. Shows what the
/// run will be allowed to use before saving (ReadMe_Buzzies.md §1A: a routine is confirmed before
/// it is enabled). Returns the routine to save, or null when cancelled.
/// </summary>
public sealed class RoutineEditorWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F48771"));

    private static readonly string[] ScheduleLabels =
    [
        "Only when I press Run now", "Every day", "Every week", "Every few minutes (for testing)",
    ];

    private readonly Routine? _existing;
    private readonly IReadOnlyList<BuzzyProfile> _buzzies;
    private readonly ComboBox _buzzy = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { PlaceholderText = "Morning research" };
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, MaxHeight = 220,
        PlaceholderText = "Find new open-source models that run on a 24 GB Mac and save a short report.",
    };
    private readonly ComboBox _schedule = new() { ItemsSource = ScheduleLabels, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _time = new() { Width = 80, Text = "09:00" };
    private readonly ComboBox _day = new() { ItemsSource = Enum.GetValues<DayOfWeek>(), SelectedItem = DayOfWeek.Friday, Width = 140 };
    private readonly NumericUpDown _interval = new() { Minimum = Routine.MinIntervalMinutes, Maximum = 1440, Value = 2, Increment = 1, Width = 130, FormatString = "0" };
    private readonly NumericUpDown _maxMinutes = new() { Minimum = 1, Maximum = 120, Value = Routine.DefaultMaxMinutes, Width = 130, FormatString = "0" };
    private readonly NumericUpDown _maxTokens = new() { Minimum = 10, Maximum = 5000, Value = Routine.DefaultMaxTokens / 1000, Width = 130, FormatString = "0", Increment = 10 };
    private readonly CheckBox _catchUp = new() { Content = "If Litos was closed at the scheduled time, run once when it opens again" };
    private readonly StackPanel _whenRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _toolsText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 12 };
    private readonly TextBlock _error = new() { Foreground = Warn, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public RoutineEditorWindow() : this(null, [], null)
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    public RoutineEditorWindow(Routine? existing, IReadOnlyList<BuzzyProfile> buzzies, string? preselectBuzzyId)
    {
        _existing = existing;
        _buzzies = buzzies;
        Title = existing is null ? "New routine" : $"Edit routine — {existing.Name}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        _buzzy.ItemsSource = buzzies.Select(b => b.Name).ToList();
        var selectedId = existing?.BuzzyId ?? preselectBuzzyId;
        _buzzy.SelectedIndex = Math.Max(0, buzzies.ToList().FindIndex(b => b.Id == selectedId));
        _buzzy.IsEnabled = existing is null;
        _buzzy.SelectionChanged += (_, _) => RefreshTools();

        if (existing is not null)
        {
            _name.Text = existing.Name;
            _prompt.Text = existing.Prompt;
            _schedule.SelectedIndex = (int)existing.Trigger;
            _time.Text = existing.TimeOfDay?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";
            _day.SelectedItem = existing.Day ?? DayOfWeek.Friday;
            _interval.Value = existing.IntervalMinutes ?? 2;
            _maxMinutes.Value = existing.MaxMinutes;
            _maxTokens.Value = existing.MaxTokens / 1000;
            _catchUp.IsChecked = existing.MissedRuns == MissedRunPolicy.CatchUpOnce;
        }
        else
        {
            _schedule.SelectedIndex = (int)RoutineTrigger.Daily;
        }
        _schedule.SelectionChanged += (_, _) => RefreshWhen();

        var save = new Button { Content = existing is null ? "Create routine" : "Save", IsDefault = true };
        save.Click += (_, _) => Accept();
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
                    new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.SemiBold },
                    Label("Buzzy"), _buzzy,
                    Label("Name"), _name,
                    Label("What should it do each time?"), _prompt,
                    Label("When"), _schedule, _whenRow,
                    Label("Limits for each run"),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        Children = { _maxMinutes, Muted("minutes at most"), _maxTokens, Muted("thousand tokens at most") },
                    },
                    _catchUp,
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#252526")), CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12), Margin = new Thickness(0, 6, 0, 0), Child = _toolsText,
                    },
                    _error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 6, 0, 0), Children = { cancel, save },
                    },
                },
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };

        RefreshWhen();
        RefreshTools();
    }

    public static Task<Routine?> ShowAsync(Window owner, Routine? existing, IReadOnlyList<BuzzyProfile> buzzies, string? preselectBuzzyId) =>
        new RoutineEditorWindow(existing, buzzies, preselectBuzzyId).ShowDialog<Routine?>(owner);

    private RoutineTrigger Trigger => (RoutineTrigger)Math.Max(0, _schedule.SelectedIndex);

    private void RefreshWhen()
    {
        _whenRow.Children.Clear();
        switch (Trigger)
        {
            case RoutineTrigger.Daily:
                _whenRow.Children.Add(Muted("at"));
                _whenRow.Children.Add(_time);
                break;
            case RoutineTrigger.Weekly:
                _whenRow.Children.Add(Muted("on"));
                _whenRow.Children.Add(_day);
                _whenRow.Children.Add(Muted("at"));
                _whenRow.Children.Add(_time);
                break;
            case RoutineTrigger.Interval:
                _whenRow.Children.Add(Muted("every"));
                _whenRow.Children.Add(_interval);
                _whenRow.Children.Add(Muted("minutes"));
                break;
        }
        _whenRow.Children.Add(Muted(Trigger == RoutineTrigger.Manual ? "" : $"({TimeZoneInfo.Local.DisplayName})"));
    }

    private void RefreshTools()
    {
        var buzzy = SelectedBuzzy();
        if (buzzy is null)
        {
            _toolsText.Text = "Create a Buzzy first.";
            return;
        }

        var tools = buzzy.Tools.Concat(BuzzyToolPolicy.AlwaysGranted).Distinct()
            .Where(t => BuzzyToolPolicy.Grantable.Contains(t) && t != BuzzyToolNames.Shell).Order();
        _toolsText.Text =
            $"Runs as {buzzy.Name}, in its workspace, with its memory, using the model chosen with /model. " +
            $"It can use: {string.Join(", ", tools)}. Scheduled runs never use the shell and never wait for approval. " +
            "Routines run only while Litos is open; closing the window keeps Litos running in the tray.";
    }

    private BuzzyProfile? SelectedBuzzy() =>
        _buzzy.SelectedIndex >= 0 && _buzzy.SelectedIndex < _buzzies.Count ? _buzzies[_buzzy.SelectedIndex] : null;

    private void Accept()
    {
        var buzzy = SelectedBuzzy();
        var name = _name.Text?.Trim() ?? "";
        var prompt = _prompt.Text?.Trim() ?? "";
        TimeOnly? time = null;
        if (Trigger is RoutineTrigger.Daily or RoutineTrigger.Weekly)
        {
            if (!TimeOnly.TryParseExact(_time.Text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                ShowError("Enter the time as HH:mm, for example 09:00 or 17:30.");
                return;
            }
            time = parsed;
        }

        if (buzzy is null)
        {
            ShowError("Create a Buzzy first.");
            return;
        }
        if (name.Length == 0 || prompt.Length == 0)
        {
            ShowError("Give the routine a name and say what it should do.");
            return;
        }

        var routine = new Routine(
            Id: _existing?.Id ?? Guid.NewGuid().ToString("n"),
            BuzzyId: buzzy.Id,
            Name: name,
            Prompt: prompt,
            Trigger: Trigger,
            TimeOfDay: time,
            Day: Trigger == RoutineTrigger.Weekly ? (DayOfWeek?)_day.SelectedItem : null,
            IntervalMinutes: Trigger == RoutineTrigger.Interval ? (int)(_interval.Value ?? 2) : null,
            TimeZoneId: _existing?.TimeZoneId ?? TimeZoneInfo.Local.Id,
            Enabled: _existing?.Enabled ?? true,
            MaxMinutes: (int)(_maxMinutes.Value ?? Routine.DefaultMaxMinutes),
            MaxTokens: (int)(_maxTokens.Value ?? Routine.DefaultMaxTokens / 1000) * 1000,
            MissedRuns: _catchUp.IsChecked == true ? MissedRunPolicy.CatchUpOnce : MissedRunPolicy.Skip,
            // A changed schedule starts afresh from now, so an edit never makes past times look due.
            CreatedAt: _existing is { } old && old.Trigger == Trigger && old.IntervalMinutes == (Trigger == RoutineTrigger.Interval ? (int?)(int)(_interval.Value ?? 2) : null)
                ? old.CreatedAt
                : DateTimeOffset.UtcNow,
            LastScheduledAt: _existing is null ? null : DateTimeOffset.UtcNow);
        Close(routine);
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock Muted(string text) => new() { Text = text, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
}
