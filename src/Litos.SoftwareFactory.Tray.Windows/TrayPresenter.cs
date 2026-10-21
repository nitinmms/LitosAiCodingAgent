namespace Litos.SoftwareFactory.Tray.Windows;

public enum IconKind
{
    /// <summary>Connected; nothing awaits the user.</summary>
    Idle,

    /// <summary>Connected; <see cref="IconState.Count"/> tasks await the user (amber).</summary>
    Awaiting,

    /// <summary>Connected and muted, nothing waiting.</summary>
    Muted,

    /// <summary>Connecting, or the factory can't be reached (grey).</summary>
    Offline,

    /// <summary>Not set up, or the token was refused (red).</summary>
    SignIn,
}

public sealed record IconState(IconKind Kind, int Count = 0);

public enum MenuAction
{
    None,
    OpenTask,
    OpenBoard,
    ToggleMute,
    MuteForAnHour,
    Start,
    Stop,
    Status,
    Doctor,
    Settings,
    SignOut,
    Quit,
}

/// <summary>One line of the tray menu. A null <see cref="Text"/> is a separator.</summary>
public sealed record MenuEntry(
    string? Text,
    MenuAction Action = MenuAction.None,
    bool Enabled = true,
    bool Checked = false,
    Guid? TaskId = null,
    IReadOnlyList<MenuEntry>? Children = null)
{
    public static readonly MenuEntry Separator = new(null, Enabled: false);

    public static MenuEntry Label(string text) => new(text, Enabled: false);
}

/// <summary>Where a click on a notification goes.</summary>
public enum AlertTarget
{
    Task,
    Board,
    Settings,
}

/// <summary>What to do about an alert: the sound, the notification, and what clicking it opens.</summary>
public sealed record AlertDecision(bool PlaySound, string Title, string Body, AlertTarget Target, Guid? TaskId = null);

/// <summary>
/// Everything the Windows tray app decides, with no WinForms in it, so it can be tested: what the
/// icon shows, the menu, and whether an alert, a reminder or a refused token rings or notifies
/// (§26.3). <see cref="TrayApplication"/> renders what this says.
/// </summary>
public sealed class TrayPresenter(TraySettings settings, bool factoryOnThisPc)
{
    /// <summary>How many waiting tasks the menu lists before "and N more".</summary>
    public const int MenuTasks = 8;

    public TraySettings Settings { get; set; } = settings;

    /// <summary>litos-factory.exe sits beside the tray app: the menu offers Start, Stop, Status and Doctor.</summary>
    public bool FactoryOnThisPc { get; } = factoryOnThisPc;

    /// <summary>A token is kept for the address: the tray app can connect, and can sign out.</summary>
    public bool HasToken { get; set; }

    public WatcherState State { get; private set; } = new(ConnectionStatus.Connecting);

    public IReadOnlyList<TrayTask> Awaiting { get; private set; } = [];

    /// <summary>When it last rang, for reminders.</summary>
    public DateTimeOffset LastRing { get; private set; }

    public Uri? Address => FactoryAddress.Parse(Settings.Address, Settings.AllowPlainHttp).Address;

    /// <summary>Set up: an address that can be used, and a token for it.</summary>
    public bool IsSetUp => Address is not null && HasToken;

    public IconState Icon(DateTimeOffset now)
    {
        if (!IsSetUp || State.Status == ConnectionStatus.SignInNeeded)
            return new IconState(IconKind.SignIn);
        if (State.Status != ConnectionStatus.Connected)
            return new IconState(IconKind.Offline);
        if (Awaiting.Count > 0)
            return new IconState(IconKind.Awaiting, Awaiting.Count);
        return new IconState(Settings.IsMuted(now) ? IconKind.Muted : IconKind.Idle);
    }

    public string Tooltip(DateTimeOffset now) =>
        IsSetUp ? AlertText.Tooltip(State, Awaiting.Count, Settings.IsMuted(now)) : "Litos Factory: set up the tray app";

    public void SetAwaiting(IReadOnlyList<TrayTask> tasks) => Awaiting = tasks;

    /// <summary>
    /// Takes the connection's new state. Turning to "sign in needed" is worth a notification (no
    /// sound): the tray app has stopped listening, and only the person can fix it.
    /// </summary>
    public AlertDecision? OnState(WatcherState state)
    {
        var refusedNow = state.Status == ConnectionStatus.SignInNeeded && State.Status != ConnectionStatus.SignInNeeded;
        State = state;
        return refusedNow
            ? new AlertDecision(false, "Litos Factory: sign in needed", AlertText.Fit(state.Message ?? "The factory refused the access token.", AlertText.BodyLimit), AlertTarget.Settings)
            : null;
    }

    /// <summary>Tasks that just started waiting. Muted: nothing (the count still shows). Busy: the
    /// notification, which Windows holds back by itself, but no sound if the setting says so.</summary>
    public AlertDecision? OnAlert(IReadOnlyList<TrayTask> arrived, DateTimeOffset now, bool windowsBusy)
    {
        if (arrived.Count == 0 || Settings.IsMuted(now))
            return null;

        LastRing = now;
        var (title, body) = AlertText.Notification(arrived);
        return arrived.Count == 1
            ? new AlertDecision(Sounds(windowsBusy), title, body, AlertTarget.Task, arrived[0].Id)
            : new AlertDecision(Sounds(windowsBusy), title, body, AlertTarget.Board);
    }

    /// <summary>Checked every minute: rings again while tasks still wait, if the person asked for reminders.</summary>
    public AlertDecision? OnReminderTick(DateTimeOffset now, bool windowsBusy)
    {
        if (State.Status != ConnectionStatus.Connected || Settings.IsMuted(now) || !Settings.ReminderDue(LastRing, now, Awaiting.Count))
            return null;

        LastRing = now;
        var title = Awaiting.Count == 1 ? $"{Awaiting[0].Title} still waits for you" : $"{Awaiting.Count} tasks still await you";
        var body = string.Join('\n', Awaiting.Take(5).Select(t => $"• {t.Title}: {AlertText.Reason(t).Label.ToLowerInvariant()}"));
        return Awaiting.Count == 1
            ? new AlertDecision(Sounds(windowsBusy), AlertText.Fit(title, AlertText.TitleLimit), AlertText.Fit(body, AlertText.BodyLimit), AlertTarget.Task, Awaiting[0].Id)
            : new AlertDecision(Sounds(windowsBusy), AlertText.Fit(title, AlertText.TitleLimit), AlertText.Fit(body, AlertText.BodyLimit), AlertTarget.Board);
    }

    private bool Sounds(bool windowsBusy) => Settings.Sound != AlertSound.None && !(windowsBusy && Settings.SilentWhenBusy);

    /// <summary>The right-click menu, as of <paramref name="now"/>.</summary>
    public IReadOnlyList<MenuEntry> Menu(DateTimeOffset now)
    {
        var menu = new List<MenuEntry> { Label("Litos Software Factory"), Label(StatusLine()), MenuEntry.Separator };

        if (IsSetUp && State.Status == ConnectionStatus.Connected)
        {
            menu.Add(Label($"Awaiting you ({Awaiting.Count})"));
            if (Awaiting.Count == 0)
                menu.Add(Label("Nothing awaits you"));
            foreach (var task in Awaiting.Take(MenuTasks))
                menu.Add(new MenuEntry($"{AlertText.Fit(task.Title, 48)} — {AlertText.Reason(task).Label}", MenuAction.OpenTask, TaskId: task.Id));
            if (Awaiting.Count > MenuTasks)
                menu.Add(new MenuEntry($"and {Awaiting.Count - MenuTasks} more on the board", MenuAction.OpenBoard));
            menu.Add(MenuEntry.Separator);
        }

        var mutedForAWhile = !Settings.Muted && Settings.MutedUntil > now;
        menu.Add(new MenuEntry("Open board", MenuAction.OpenBoard, Enabled: Address is not null));
        menu.Add(new MenuEntry("Mute alerts", MenuAction.ToggleMute, Checked: Settings.Muted));
        menu.Add(new MenuEntry(
            mutedForAWhile ? $"Muted until {Settings.MutedUntil!.Value.ToLocalTime():HH:mm}" : "Mute for 1 hour",
            MenuAction.MuteForAnHour, Enabled: !Settings.Muted, Checked: mutedForAWhile));
        menu.Add(MenuEntry.Separator);

        if (FactoryOnThisPc)
        {
            menu.Add(new MenuEntry("Factory on this PC", Children:
            [
                new MenuEntry("Start", MenuAction.Start),
                new MenuEntry("Stop", MenuAction.Stop),
                MenuEntry.Separator,
                new MenuEntry("Status", MenuAction.Status),
                new MenuEntry("Doctor", MenuAction.Doctor),
            ]));
        }

        menu.Add(new MenuEntry("Settings…", MenuAction.Settings));
        menu.Add(new MenuEntry("Sign out", MenuAction.SignOut, Enabled: HasToken));
        menu.Add(new MenuEntry("Quit", MenuAction.Quit));
        return menu;

        static MenuEntry Label(string text) => MenuEntry.Label(text);
    }

    private string StatusLine()
    {
        if (Address is null)
            return "Not set up: choose Settings…";
        if (!HasToken)
            return $"No token for {Address.Authority}: choose Settings…";
        return State.Status switch
        {
            ConnectionStatus.Connected => $"Connected · {Address.Authority}{(State.User is { } user ? $" · {user.Name}" : "")}",
            ConnectionStatus.Reconnecting => State.RetryAt is { } at
                ? $"Reconnecting at {at.ToLocalTime():HH:mm:ss}: {AlertText.Fit(State.Message ?? "", 60)}"
                : "Reconnecting…",
            ConnectionStatus.SignInNeeded => $"Sign in needed: {AlertText.Fit(State.Message ?? "", 60)}",
            _ => $"Connecting to {Address.Authority}…",
        };
    }
}
