using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Litos.Gui.Buzzies;
using Litos.Gui.Buzzies.Routines;

namespace Litos.Gui;

/// <summary>
/// Routines, the tray and notifications (Implement_Buzzies.md, Phase 1). The scheduler lives as
/// long as this window does. While any routine is switched on, closing the window hides it to the
/// tray so routines keep running; Quit in the tray menu (or closing with no routines on) ends
/// Litos, cancelling any run in progress. A finished or failed run shows a corner notification
/// that opens that run's conversation.
/// </summary>
public sealed partial class MainWindow
{
    private RoutineScheduler? _routines;
    private TrayIcon? _trayIcon;
    private bool _quitting;
    private bool _toldAboutTray;

    private void InitializeRoutines()
    {
        try
        {
            var db = new BuzzyDb(Buzzies.DatabasePath);
            _routines = new RoutineScheduler(db, Buzzies, new BuzzyRunExecutor(SnapshotRunEnvironmentAsync), options: new RoutineSchedulerOptions
            {
                // A local model serves one request at a time, so don't queue three behind it.
                MaxConcurrent = () => _session.ProviderName == "local" ? 1 : 3,
            });
        }
        catch (Exception ex)
        {
            AddToolLine($"Routines are unavailable this run: {ex.Message}");
            return;
        }

        _routines.RunStarted += run => Dispatcher.UIThread.Post(() => RefreshBuzzyRowStatus(run.BuzzyId));
        _routines.RunFinished += run => Dispatcher.UIThread.Post(() => OnRunFinished(run));
        _routines.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            foreach (var id in _buzzyRows.Keys)
                RefreshBuzzyRowStatus(id);
            RefreshTrayVisibility();
        });
        _routines.Start();
        CreateTrayIcon();
    }

    /// <summary>The provider and model selected right now, and a fresh tool registry, read on the UI
    /// thread where MainWindow changes them.</summary>
    private Task<BuzzyRunEnvironment> SnapshotRunEnvironmentAsync(CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => new BuzzyRunEnvironment(
            _session.LoopFactory,
            _session.ToolRegistryFactory.Create(),
            _session.ChatProvider,
            _session.Model,
            _session.ContextLength,
            _session.TranscriptStore,
            BuzzyToolContext()), DispatcherPriority.Normal, ct).GetTask();

    private void ShowRoutines(string? buzzyId)
    {
        if (_routines is null)
        {
            AddToolLine("Routines are unavailable this run.");
            return;
        }
        RoutinesWindow.Show(this, _routines, Buzzies, buzzyId, OpenRunAsync);
    }

    /// <summary>Shows a run's conversation: selects its Buzzy and resumes the run's session.</summary>
    private async Task OpenRunAsync(RoutineRun run)
    {
        ShowFromTray();
        if (TurnInFlight)
        {
            AddToolLine("A turn is still running. Wait for it to finish, then open the run again.");
            return;
        }
        if (Buzzies.Get(run.BuzzyId) is not { } profile || run.SessionId is null)
            return;
        if (_routines?.RunningFor(run.BuzzyId)?.Id == run.Id)
        {
            AddToolLine($"{profile.Name} is still working on this run. Open it again once it has finished.");
            return;
        }

        await SelectBuzzyAsync(profile);
        await ResumeSessionAsync(run.SessionId, announce: true);
        ApplyWorkingDirectory(Buzzies.WorkspaceOf(profile.Id));
    }

    private void OnRunFinished(RoutineRun run)
    {
        RefreshBuzzyRowStatus(run.BuzzyId);
        var profile = Buzzies.Get(run.BuzzyId);
        var routine = _routines?.Database.GetRoutine(run.RoutineId);
        if (profile is null || run.Status == RunStatus.Cancelled)
            return;

        var name = routine?.Name ?? "a routine";
        var (title, body) = run.Status == RunStatus.Succeeded
            ? ($"{profile.Name} finished \"{name}\"", run.Summary ?? "Done. Click to see what it did.")
            : ($"{profile.Name} couldn't finish \"{name}\"", run.Error ?? "The run failed. Click to see what happened.");
        ShowToast(title, body, profile.Color, () => _ = OpenRunAsync(run));
    }

    private static void ShowToast(string title, string body, string color, Action? onClick) =>
        new ToastWindow(title, body, color, onClick).Show();

    // ---- Tray and closing ----------------------------------------------------------------------

    /// <summary>True when this window runs routines and at least one is switched on. A second Litos
    /// window (which lost the scheduler lock, so runs nothing) never keeps itself alive in the tray.</summary>
    private bool HasEnabledRoutines => _routines is { IsPrimary: true } routines && routines.Database.ListRoutines().Any(r => r.Enabled);

    private void CreateTrayIcon()
    {
        if (Application.Current is not { } app)
            return;

        try
        {
            var open = new NativeMenuItem("Open Litos");
            open.Click += (_, _) => ShowFromTray();
            var routines = new NativeMenuItem("Routines…");
            routines.Click += (_, _) =>
            {
                ShowFromTray();
                ShowRoutines(_buzzy?.Id);
            };
            var quit = new NativeMenuItem("Quit Litos (stops routines)");
            quit.Click += (_, _) => QuitFromTray();

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Litos.Gui/Assets/AppIcon.png"))),
                ToolTipText = "Litos: your Buzzies' routines run while this is here",
                Menu = new NativeMenu { Items = { open, routines, new NativeMenuItemSeparator(), quit } },
                IsVisible = false,
            };
            _trayIcon.Clicked += (_, _) => ShowFromTray();
            TrayIcon.SetIcons(app, [_trayIcon]);
        }
        catch (Exception)
        {
            // No tray on this desktop (some Linux setups): closing then simply quits, as before.
            _trayIcon = null;
        }
        RefreshTrayVisibility();
    }

    /// <summary>The tray icon shows while routines are on (Litos will stay running when closed) or
    /// while the window is hidden.</summary>
    private void RefreshTrayVisibility()
    {
        if (_trayIcon is not null)
            _trayIcon.IsVisible = HasEnabledRoutines || !IsVisible;
    }

    /// <summary>The window's own close button (not Quit, not the OS shutting down): with routines
    /// on, hide to the tray instead of quitting. Returns true when it did, cancelling the close.</summary>
    private bool TryHideToTray(WindowClosingEventArgs e)
    {
        if (_quitting || _trayIcon is null || e.CloseReason != WindowCloseReason.WindowClosing || !HasEnabledRoutines)
            return false;

        e.Cancel = true;
        Hide();
        RefreshTrayVisibility();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            ShowToast("Litos is still running in the tray",
                "Your Buzzies' routines keep running while it's here. To stop them, choose Quit Litos from the tray icon.",
                BuzzyTemplates.Buddy.Color, ShowFromTray);
        }
        return true;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        RefreshTrayVisibility();
    }

    private void QuitFromTray()
    {
        _quitting = true;
        Close();
    }

    /// <summary>On quit: cancels runs in progress (recorded as cancelled because Litos closed),
    /// releases the scheduler lock and removes the tray icon. Bounded, so quitting never hangs.</summary>
    private void ShutdownRoutines()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_routines is null)
            return;
        var routines = _routines;
        try
        {
            // On the thread pool: the UI thread is blocked here, and the scheduler's continuations
            // (and a run fetching its environment from the UI thread) must not wait for it.
            Task.Run(() => routines.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Quitting anyway; a run left mid-way is marked Interrupted on the next start.
        }
        _routines = null;
    }
}
