using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Litos.Gui.Buzzies.Routines;

namespace Litos.Gui.Buzzies;

/// <summary>
/// Every Buzzy's routines with their schedule and next run, Run now / Pause / Edit / Delete, and
/// the recent runs with what they produced, Open conversation and Cancel (Implement_Buzzies.md,
/// Phase 1). Live: it redraws whenever the scheduler reports a change, and every 30 seconds so the
/// "next run" and "running for" times stay current. Not modal, so it can stay open beside the chat.
/// </summary>
public sealed class RoutinesWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#4EC9B0"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F48771"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#D7BA7D"));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly RoutineScheduler _scheduler;
    private readonly BuzzyStore _store;
    private readonly string? _preselectBuzzyId;
    private readonly Func<RoutineRun, Task> _openRun;
    private readonly StackPanel _routines = new() { Spacing = 8 };
    private readonly StackPanel _runs = new() { Spacing = 6 };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    public RoutinesWindow() : this(null!, new BuzzyStore(), null, _ => Task.CompletedTask)
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    public RoutinesWindow(RoutineScheduler scheduler, BuzzyStore store, string? preselectBuzzyId, Func<RoutineRun, Task> openRun)
    {
        _scheduler = scheduler;
        _store = store;
        _preselectBuzzyId = preselectBuzzyId;
        _openRun = openRun;
        Title = "Routines";
        Width = 760;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        var create = new Button { Content = "+ New routine" };
        create.Click += async (_, _) => await EditAsync(null);

        var header = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { new TextBlock { Text = "Routines", FontSize = 20, FontWeight = FontWeight.SemiBold }, At(create, 1) },
                },
                new TextBlock
                {
                    Text = "Routines run while Litos is open: closing the window keeps it running in the tray, and Quit stops it. " +
                           "A scheduled run can't use the shell and never waits for approval. Each run is its own conversation.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 12,
                },
            },
        };
        if (scheduler is { IsPrimary: false })
            header.Children.Add(new TextBlock
            {
                Text = "Another Litos window is running routines right now, so this one won't start any. You can still edit them here.",
                Foreground = Amber, TextWrapping = TextWrapping.Wrap,
            });

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    header,
                    _routines,
                    new TextBlock { Text = "Recent runs", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) },
                    _runs,
                },
            },
        };

        if (scheduler is not null)
        {
            void OnChanged() => Dispatcher.UIThread.Post(Refresh);
            scheduler.Changed += OnChanged;
            Closed += (_, _) =>
            {
                scheduler.Changed -= OnChanged;
                _clock.Stop();
            };
            _clock.Tick += (_, _) => Refresh();
            _clock.Start();
            Refresh();
        }
    }

    public static void Show(Window owner, RoutineScheduler scheduler, BuzzyStore store, string? preselectBuzzyId, Func<RoutineRun, Task> openRun) =>
        new RoutinesWindow(scheduler, store, preselectBuzzyId, openRun).Show(owner);

    private void Refresh()
    {
        var buzzies = _store.List().ToDictionary(b => b.Id);
        var db = _scheduler.Database;
        var routines = db.ListRoutines();
        var now = DateTimeOffset.UtcNow;

        _routines.Children.Clear();
        if (buzzies.Count == 0)
            _routines.Children.Add(Muted("Create a Buzzy first, then give it a routine."));
        else if (routines.Count == 0)
            _routines.Children.Add(Muted("No routines yet. Press New routine to teach a Buzzy something to repeat."));
        foreach (var routine in routines.OrderBy(r => r.BuzzyId != _preselectBuzzyId).ThenBy(r => r.CreatedAt))
        {
            if (buzzies.TryGetValue(routine.BuzzyId, out var buzzy))
                _routines.Children.Add(RoutineCard(routine, buzzy, now));
        }

        _runs.Children.Clear();
        var runs = db.ListRecentRuns(null, 25);
        if (runs.Count == 0)
            _runs.Children.Add(Muted("Nothing has run yet."));
        var names = routines.ToDictionary(r => r.Id, r => r.Name);
        foreach (var run in runs)
            _runs.Children.Add(RunCard(run, names.GetValueOrDefault(run.RoutineId, "(deleted routine)"), buzzies.GetValueOrDefault(run.BuzzyId), now));
    }

    private Control RoutineCard(Routine routine, BuzzyProfile buzzy, DateTimeOffset now)
    {
        var next = routine.Enabled ? RoutineSchedule.Next(routine, now, RoutineSchedule.ResolveTimeZone(routine.TimeZoneId)) : null;
        var nextText = !routine.Enabled ? "Paused" : next is { } at ? $"Next: {Friendly(at)}" : "Runs when you press Run now";

        var runNow = new Button { Content = "Run now" };
        runNow.Click += async (_, _) => await _scheduler.RunNowAsync(routine.Id);
        var pause = new Button { Content = routine.Enabled ? "Pause" : "Resume" };
        pause.Click += (_, _) =>
        {
            _scheduler.Database.SetEnabled(routine.Id, !routine.Enabled);
            if (!routine.Enabled)
                _scheduler.Database.SetLastScheduled(routine.Id, DateTimeOffset.UtcNow); // Resuming doesn't make paused-over times due.
            _scheduler.NotifyChanged();
        };
        var edit = new Button { Content = "Edit" };
        edit.Click += async (_, _) => await EditAsync(routine);
        var delete = new Button { Content = "Delete" };
        delete.Click += async (_, _) =>
        {
            if (await ConfirmationDialog.ShowAsync(this, $"Delete \"{routine.Name}\"?", "This deletes the routine and its run history. Files it saved stay in the Buzzy's workspace."))
            {
                _scheduler.Database.DeleteRoutine(routine.Id);
                _scheduler.NotifyChanged();
            }
        };

        var text = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = routine.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                Muted($"{buzzy.Name} · {RoutineSchedule.Describe(routine)} · {nextText}"),
                new TextBlock { Text = routine.Prompt, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#C8C8C8")) },
            },
        };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { runNow, pause, edit, delete })
        {
            button.Margin = new Thickness(4, 0, 0, 4);
            buttons.Children.Add(button);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(new BuzzyAvatar(buzzy.Color, 36) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0) });
        grid.Children.Add(At(text, 1));
        grid.Children.Add(At(buttons, 2));
        return new Border { Background = CardBrush, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Child = grid, Opacity = routine.Enabled ? 1 : 0.7 };
    }

    private Control RunCard(RoutineRun run, string routineName, BuzzyProfile? buzzy, DateTimeOffset now)
    {
        var (label, brush) = StatusLabel(run.Status);
        var when = run.StartedAt ?? run.QueuedAt;
        var timing = run.Status switch
        {
            RunStatus.Running => $"running for {Duration(now - when)}",
            RunStatus.Queued => "waiting for its Buzzy",
            _ when run.FinishedAt is { } done && run.StartedAt is { } started => $"took {Duration(done - started)}",
            _ => "",
        };
        var tokens = run.InputTokens + run.OutputTokens > 0 ? $" · {run.InputTokens + run.OutputTokens:N0} tokens" : "";

        var body = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Inlines =
                    [
                        new Avalonia.Controls.Documents.Run(label) { Foreground = brush, FontWeight = FontWeight.SemiBold },
                        new Avalonia.Controls.Documents.Run($"  {routineName} · {buzzy?.Name ?? run.BuzzyId} · {Friendly(when)}{(timing.Length > 0 ? " · " + timing : "")}{tokens}") { Foreground = Dim },
                    ],
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        if (run.Error is { } error)
            body.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = run.Status == RunStatus.Cancelled ? Dim : Bad });
        if (run.Summary is { } summary)
            body.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
        if (run.Outputs is { Count: > 0 } outputs)
            body.Children.Add(new TextBlock { Text = "Saved: " + string.Join(", ", outputs), TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = Mono, Foreground = Ok });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        if (run.SessionId is not null && run.Status != RunStatus.Queued)
        {
            var open = new Button { Content = "Open conversation" };
            open.Click += async (_, _) => await _openRun(run);
            actions.Children.Add(open);
        }
        if (run.IsActive)
        {
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) =>
            {
                _scheduler.Cancel(run.Id);
                Refresh();
            };
            actions.Children.Add(cancel);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(body);
        grid.Children.Add(At(actions, 1));
        return new Border { Background = CardBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = grid };
    }

    private async Task EditAsync(Routine? routine)
    {
        var buzzies = _store.List();
        var saved = await RoutineEditorWindow.ShowAsync(this, routine, buzzies, routine?.BuzzyId ?? _preselectBuzzyId);
        if (saved is null)
            return;
        _scheduler.Database.SaveRoutine(saved);
        _scheduler.NotifyChanged();
    }

    internal static (string Label, IBrush Brush) StatusLabel(RunStatus status) => status switch
    {
        RunStatus.Succeeded => ("✓ Done", Ok),
        RunStatus.Running => ("▶ Running", Ok),
        RunStatus.Queued => ("… Queued", Amber),
        RunStatus.Failed => ("✗ Failed", Bad),
        RunStatus.Interrupted => ("! Interrupted", Bad),
        RunStatus.Missed => ("– Missed", Amber),
        RunStatus.Skipped => ("– Skipped", Amber),
        _ => ("■ Cancelled", Dim),
    };

    /// <summary>"today 09:00", "tomorrow 09:00", "Fri 27 Nov 09:00".</summary>
    internal static string Friendly(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTime.Now.Date;
        var day = local.Date == today ? "today"
            : local.Date == today.AddDays(1) ? "tomorrow"
            : local.Date == today.AddDays(-1) ? "yesterday"
            : local.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        return $"{day} {local:HH:mm}";
    }

    internal static string Duration(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{Math.Max(0, (int)span.TotalSeconds)}s"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
        : $"{(int)span.TotalHours}h {span.Minutes}m";

    private static TextBlock Muted(string text) => new() { Text = text, Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    private static T At<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
