using System.Diagnostics;
using System.Windows.Forms;

namespace Litos.SoftwareFactory.Tray.Windows;

/// <summary>
/// The running tray app: the notify icon and its menu, the watcher that follows the factory, the
/// notification and sound, reminders and the Settings window. Every decision is
/// <see cref="TrayPresenter"/>'s; this renders it. The watcher calls back on its own thread, so
/// each call is posted to the UI thread first.
/// </summary>
internal sealed class TrayApplication : ApplicationContext, IAttentionListener
{
    private readonly NotifyIcon _icon = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly TrayIcons _icons = new();
    private readonly System.Windows.Forms.Timer _minute = new() { Interval = 60_000 };
    private readonly SynchronizationContext _ui;
    private readonly TrayPresenter _presenter;
    private readonly IDesktop _desktop;
    private readonly ITokenStore _tokens;
    private readonly LocalFactory _local;
    private readonly string _settingsPath;
    private CancellationTokenSource? _watching;
    private AlertDecision? _balloon;
    private SettingsForm? _settingsForm;

    public TrayApplication(string settingsPath, IDesktop desktop, ITokenStore tokens, LocalFactory local, bool openSettingsIfNotSetUp)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settingsPath = settingsPath;
        _desktop = desktop;
        _tokens = tokens;
        _local = local;
        _presenter = new TrayPresenter(TraySettings.Load(settingsPath), local.IsInstalled);

        _icon.ContextMenuStrip = _menu;
        _menu.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenBoardOrSettings();
        };
        _icon.BalloonTipClicked += (_, _) => OpenBalloonTarget();
        _minute.Tick += (_, _) => Remind();
        _minute.Start();

        Connect();
        Refresh();
        _icon.Visible = true;

        if (!_presenter.IsSetUp)
        {
            if (openSettingsIfNotSetUp)
                _ui.Post(_ => ShowSettings(), null);
            else
                Notify(new AlertDecision(false, "Litos Factory tray app", "Click to connect it to your factory.", AlertTarget.Settings));
        }
    }

    // ---- the watcher's calls, on its thread ----

    void IAttentionListener.OnState(WatcherState state) => _ui.Post(_ =>
    {
        if (_presenter.OnState(state) is { } decision)
            Notify(decision);
        Refresh();
    }, null);

    void IAttentionListener.OnAwaiting(IReadOnlyList<TrayTask> tasks) => _ui.Post(_ =>
    {
        _presenter.SetAwaiting(tasks);
        Refresh();
    }, null);

    void IAttentionListener.OnAlert(IReadOnlyList<TrayTask> arrived) => _ui.Post(_ =>
    {
        if (_presenter.OnAlert(arrived, DateTimeOffset.Now, _desktop.IsBusy()) is { } decision)
            Notify(decision);
        Refresh();
    }, null);

    // ---- connecting ----

    /// <summary>(Re)starts the watcher for the saved address and token; stops it when there are none.</summary>
    private void Connect()
    {
        _watching?.Cancel();
        _watching?.Dispose();
        _watching = null;

        var address = _presenter.Address;
        var token = address is null ? null : ReadToken(address);
        _presenter.HasToken = token is not null;
        _presenter.SetAwaiting([]);
        if (address is null || token is null)
            return;

        _presenter.OnState(new WatcherState(ConnectionStatus.Connecting));
        var stop = _watching = new CancellationTokenSource();
        var http = FactoryClient.CreateHttp(address, token);
        var watcher = new AttentionWatcher(new FactoryClient(http), this);
        _ = Task.Run(async () =>
        {
            try
            {
                await watcher.RunAsync(stop.Token);
            }
            finally
            {
                http.Dispose();
            }
        });
    }

    private string? ReadToken(Uri address)
    {
        try
        {
            return _tokens.Read(address);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ---- showing ----

    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        _icon.Icon = _icons.For(_presenter.Icon(now), SystemInformation.SmallIconSize.Width);
        _icon.Text = _presenter.Tooltip(now);
    }

    private void Notify(AlertDecision decision)
    {
        if (decision.PlaySound)
            _desktop.Play(_presenter.Settings.Sound);
        _balloon = decision;
        _icon.ShowBalloonTip(10_000, decision.Title, decision.Body, ToolTipIcon.None);
    }

    private void Remind()
    {
        if (_presenter.OnReminderTick(DateTimeOffset.Now, _desktop.IsBusy()) is { } decision)
            Notify(decision);
        Refresh();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        foreach (var entry in _presenter.Menu(DateTimeOffset.Now))
            _menu.Items.Add(Item(entry));
    }

    private ToolStripItem Item(MenuEntry entry)
    {
        if (entry.Text is null)
            return new ToolStripSeparator();

        // A task's title may hold "&", which a menu would take as a keyboard shortcut.
        var item = new ToolStripMenuItem(entry.Text.Replace("&", "&&")) { Enabled = entry.Enabled, Checked = entry.Checked };
        if (entry.Children is { } children)
        {
            foreach (var child in children)
                item.DropDownItems.Add(Item(child));
        }
        else if (entry.Action != MenuAction.None)
        {
            item.Click += (_, _) => Do(entry);
        }

        return item;
    }

    // ---- acting ----

    private void Do(MenuEntry entry)
    {
        var address = _presenter.Address;
        switch (entry.Action)
        {
            case MenuAction.OpenTask when address is not null && entry.TaskId is { } id:
                _desktop.Open(FactoryAddress.TaskPage(address, id));
                break;
            case MenuAction.OpenBoard when address is not null:
                _desktop.Open(FactoryAddress.Board(address));
                break;
            case MenuAction.ToggleMute:
                SaveSettings(_presenter.Settings with { Muted = !_presenter.Settings.Muted, MutedUntil = null });
                break;
            case MenuAction.MuteForAnHour:
                SaveSettings(_presenter.Settings with { MutedUntil = DateTimeOffset.Now.AddHours(1) });
                break;
            case MenuAction.Start or MenuAction.Stop or MenuAction.Status or MenuAction.Doctor:
                RunLocal(entry.Action);
                break;
            case MenuAction.Settings:
                ShowSettings();
                break;
            case MenuAction.SignOut when address is not null:
                _tokens.Delete(address);
                Connect();
                break;
            case MenuAction.Quit:
                ExitThread();
                return;
        }

        Refresh();
    }

    private void RunLocal(MenuAction action)
    {
        try
        {
            Process.Start(_local.Command(action))?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Notify(new AlertDecision(false, "litos-factory could not be started", ex.Message, AlertTarget.Settings));
        }
    }

    private void OpenBoardOrSettings()
    {
        if (_presenter.IsSetUp && _presenter.Address is { } address)
            _desktop.Open(FactoryAddress.Board(address));
        else
            ShowSettings();
    }

    private void OpenBalloonTarget()
    {
        var address = _presenter.Address;
        switch (_balloon)
        {
            case { Target: AlertTarget.Task, TaskId: { } id } when address is not null:
                _desktop.Open(FactoryAddress.TaskPage(address, id));
                break;
            case { Target: AlertTarget.Board } when address is not null:
                _desktop.Open(FactoryAddress.Board(address));
                break;
            default:
                ShowSettings();
                break;
        }
    }

    /// <summary>Opens the Settings window, or brings it to the front if it is open.</summary>
    public void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        using var form = _settingsForm = new SettingsForm(_presenter.Settings, _local.IsInstalled ? _local.Address() : null, ReadToken, _desktop);
        if (form.ShowDialog() == DialogResult.OK && form.Result is { } result)
        {
            if (result.Token is not null)
                _tokens.Write(result.Address, result.Token);
            _desktop.SetStartAtSignIn(result.Settings.StartAtSignIn);
            SaveSettings(result.Settings);
            Connect();
        }

        _settingsForm = null;
        Refresh();
    }

    private void SaveSettings(TraySettings settings)
    {
        _presenter.Settings = settings;
        try
        {
            settings.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(new AlertDecision(false, "Settings not saved", ex.Message, AlertTarget.Settings));
        }
    }

    protected override void ExitThreadCore()
    {
        _watching?.Cancel();
        _minute.Stop();
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watching?.Dispose();
            _minute.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
