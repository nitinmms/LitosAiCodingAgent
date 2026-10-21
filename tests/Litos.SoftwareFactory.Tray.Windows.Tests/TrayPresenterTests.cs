namespace Litos.SoftwareFactory.Tray.Windows.Tests;

public sealed class TrayPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 32, 0, TimeSpan.Zero);
    private static readonly TrayUser Ben = new(Guid.NewGuid(), "ben", "Ben Okafor");

    private static TrayTask Task(string title, string state = "AwaitingDecision", string? reason = null) =>
        new(Guid.NewGuid(), Ben.Id, title, TrayTask.AwaitingYou, state, "Implement", reason, Now);

    /// <summary>Set up and connected, with these tasks waiting.</summary>
    private static TrayPresenter Connected(TraySettings? settings = null, bool local = false, params TrayTask[] waiting)
    {
        var presenter = new TrayPresenter(settings ?? new TraySettings { Address = "http://127.0.0.1:5180/" }, local) { HasToken = true };
        presenter.OnState(new WatcherState(ConnectionStatus.Connected, Ben));
        presenter.SetAwaiting(waiting);
        return presenter;
    }

    private static string[] Texts(IEnumerable<MenuEntry> menu) => [.. menu.Select(e => e.Text ?? "—")];

    // ---- the icon ----

    [Fact]
    public void TheIcon_SaysTheState()
    {
        Assert.Equal(new IconState(IconKind.Idle), Connected().Icon(Now));
        Assert.Equal(new IconState(IconKind.Awaiting, 2), Connected(null, false, Task("A"), Task("B")).Icon(Now));
        Assert.Equal(new IconState(IconKind.Muted), Connected(new TraySettings { Address = "http://127.0.0.1:5180/", Muted = true }).Icon(Now));
        // Muted with tasks waiting still shows the count: muting stops the ring, not the information.
        Assert.Equal(new IconState(IconKind.Awaiting, 1), Connected(new TraySettings { Address = "http://127.0.0.1:5180/", Muted = true }, false, Task("A")).Icon(Now));

        var reconnecting = Connected();
        reconnecting.OnState(new WatcherState(ConnectionStatus.Reconnecting, Ben, "Can't reach the factory.", Now.AddSeconds(8)));
        Assert.Equal(new IconState(IconKind.Offline), reconnecting.Icon(Now));

        var refused = Connected();
        refused.OnState(new WatcherState(ConnectionStatus.SignInNeeded, Ben, "The access token is not valid."));
        Assert.Equal(new IconState(IconKind.SignIn), refused.Icon(Now));
    }

    [Fact]
    public void NotSetUp_ShowsSignIn_AndSaysSo()
    {
        var noAddress = new TrayPresenter(new TraySettings(), false);
        var noToken = new TrayPresenter(new TraySettings { Address = "http://127.0.0.1:5180/" }, false);

        Assert.Equal(new IconState(IconKind.SignIn), noAddress.Icon(Now));
        Assert.Equal("Litos Factory: set up the tray app", noAddress.Tooltip(Now));
        Assert.Equal("Not set up: choose Settings…", noAddress.Menu(Now)[1].Text);
        Assert.Equal("No token for 127.0.0.1:5180: choose Settings…", noToken.Menu(Now)[1].Text);
        Assert.False(noToken.IsSetUp);
    }

    [Fact]
    public void TheTooltip_CountsWhatAwaits()
    {
        Assert.Equal("Litos Factory: 2 tasks await you", Connected(null, false, Task("A"), Task("B")).Tooltip(Now));
    }

    // ---- alerts ----

    [Fact]
    public void OneTaskArriving_RingsAndOpensThatTask()
    {
        var presenter = Connected();
        var task = Task("Add CSV export", reason: "Comma or semicolon?");

        var decision = presenter.OnAlert([task], Now, windowsBusy: false);

        Assert.Equal(new AlertDecision(true, "Add CSV export needs a decision", "Comma or semicolon?", AlertTarget.Task, task.Id), decision);
        Assert.Equal(Now, presenter.LastRing);
    }

    [Fact]
    public void SeveralTasksArriving_RingOnce_AndOpenTheBoard()
    {
        var decision = Connected().OnAlert([Task("A"), Task("B", "Blocked")], Now, windowsBusy: false)!;

        Assert.Equal(("2 tasks need you", AlertTarget.Board, (Guid?)null), (decision.Title, decision.Target, decision.TaskId));
    }

    [Fact]
    public void Muted_NothingRings_ForEverOrForAnHour()
    {
        Assert.Null(Connected(new TraySettings { Address = "http://127.0.0.1:5180/", Muted = true }).OnAlert([Task("A")], Now, false));

        var forAnHour = Connected(new TraySettings { Address = "http://127.0.0.1:5180/", MutedUntil = Now.AddHours(1) });
        Assert.Null(forAnHour.OnAlert([Task("A")], Now, false));
        Assert.NotNull(forAnHour.OnAlert([Task("A")], Now.AddHours(1), false));
    }

    [Theory]
    [InlineData(false, true, AlertSound.Ding, true)]
    [InlineData(true, true, AlertSound.Ding, false)]
    [InlineData(true, false, AlertSound.Ding, true)]
    [InlineData(false, true, AlertSound.None, false)]
    public void TheSound_RespectsBusyWindows_AndNone(bool busy, bool silentWhenBusy, AlertSound sound, bool plays)
    {
        var presenter = Connected(new TraySettings { Address = "http://127.0.0.1:5180/", SilentWhenBusy = silentWhenBusy, Sound = sound });

        var decision = presenter.OnAlert([Task("A")], Now, busy)!;

        // The notification is shown either way: Windows holds it back by itself while busy.
        Assert.Equal(plays, decision.PlaySound);
    }

    [Fact]
    public void ARefusedToken_NotifiesOnce_WithoutSound_AndOpensSettings()
    {
        var presenter = Connected();

        var first = presenter.OnState(new WatcherState(ConnectionStatus.SignInNeeded, Ben, "The access token is not valid."));
        var again = presenter.OnState(new WatcherState(ConnectionStatus.SignInNeeded, Ben, "The access token is not valid."));

        Assert.Equal(new AlertDecision(false, "Litos Factory: sign in needed", "The access token is not valid.", AlertTarget.Settings), first);
        Assert.Null(again);
        Assert.Null(presenter.OnState(new WatcherState(ConnectionStatus.Reconnecting)));
    }

    // ---- reminders ----

    [Fact]
    public void AReminder_RingsAgain_WhileTasksWait_IfAskedFor()
    {
        var never = Connected(null, false, Task("Add CSV export"));
        never.OnAlert([Task("Add CSV export")], Now, false);
        Assert.Null(never.OnReminderTick(Now.AddHours(5), false));

        var settings = new TraySettings { Address = "http://127.0.0.1:5180/", Reminder = ReminderInterval.Every15Minutes };
        var waiting = Task("Add CSV export");
        var presenter = Connected(settings, false, waiting);
        presenter.OnAlert([waiting], Now, false);

        Assert.Null(presenter.OnReminderTick(Now.AddMinutes(14), false));
        var reminder = presenter.OnReminderTick(Now.AddMinutes(15), false);
        Assert.Equal(new AlertDecision(true, "Add CSV export still waits for you", "• Add CSV export: decision needed", AlertTarget.Task, waiting.Id), reminder);
        // The next one is 15 minutes after this one.
        Assert.Null(presenter.OnReminderTick(Now.AddMinutes(29), false));
        Assert.NotNull(presenter.OnReminderTick(Now.AddMinutes(30), false));
    }

    [Fact]
    public void NoReminder_WhenNothingWaits_WhenMuted_OrWhileDisconnected()
    {
        var settings = new TraySettings { Address = "http://127.0.0.1:5180/", Reminder = ReminderInterval.Every15Minutes };
        Assert.Null(Connected(settings).OnReminderTick(Now.AddHours(1), false));
        Assert.Null(Connected(settings with { Muted = true }, false, Task("A")).OnReminderTick(Now.AddHours(1), false));

        var away = Connected(settings, false, Task("A"));
        away.OnState(new WatcherState(ConnectionStatus.Reconnecting));
        Assert.Null(away.OnReminderTick(Now.AddHours(1), false));
    }

    [Fact]
    public void AReminderForSeveral_OpensTheBoard()
    {
        var presenter = Connected(new TraySettings { Address = "http://127.0.0.1:5180/", Reminder = ReminderInterval.Hourly }, false, Task("A"), Task("B", "Blocked"));

        var reminder = presenter.OnReminderTick(Now.AddHours(1), false)!;

        Assert.Equal(("2 tasks still await you", "• A: decision needed\n• B: blocked", AlertTarget.Board), (reminder.Title, reminder.Body, reminder.Target));
    }

    // ---- the menu ----

    [Fact]
    public void TheMenu_ListsWhatAwaits_ThenTheActions()
    {
        var csv = Task("Add CSV export");
        var menu = Connected(null, false, csv, Task("Fix login", "AwaitingHumanTesting")).Menu(Now);

        Assert.Equal(
            ["Litos Software Factory", "Connected · 127.0.0.1:5180 · Ben Okafor", "—",
             "Awaiting you (2)", "Add CSV export — Decision needed", "Fix login — Ready for testing", "—",
             "Open board", "Mute alerts", "Mute for 1 hour", "—",
             "Settings…", "Sign out", "Quit"],
            Texts(menu));
        Assert.Equal((MenuAction.OpenTask, (Guid?)csv.Id), (menu[4].Action, menu[4].TaskId));
        Assert.False(menu[0].Enabled);
        Assert.False(menu[3].Enabled);
    }

    [Fact]
    public void TheMenu_ListsEightTasks_ThenPointsToTheBoard()
    {
        var menu = Connected(null, false, [.. Enumerable.Range(1, 11).Select(i => Task($"Task {i}"))]).Menu(Now);

        Assert.Equal(8, menu.Count(e => e.Action == MenuAction.OpenTask));
        var more = Assert.Single(menu, e => e.Text == "and 3 more on the board");
        Assert.Equal(MenuAction.OpenBoard, more.Action);
    }

    [Fact]
    public void TheMenu_SaysWhenNothingAwaits()
    {
        Assert.Contains("Nothing awaits you", Texts(Connected().Menu(Now)));
    }

    [Fact]
    public void TheMenu_OffersTheLocalFactory_OnlyWhenItIsOnThisPc()
    {
        Assert.DoesNotContain("Factory on this PC", Texts(Connected().Menu(Now)));

        var local = Assert.Single(Connected(local: true).Menu(Now), e => e.Text == "Factory on this PC");
        Assert.Equal(["Start", "Stop", "—", "Status", "Doctor"], Texts(local.Children!));
        Assert.Equal([MenuAction.Start, MenuAction.Stop, MenuAction.None, MenuAction.Status, MenuAction.Doctor], local.Children!.Select(c => c.Action));
    }

    [Fact]
    public void TheMenu_ShowsHowItIsMuted()
    {
        var muted = Connected(new TraySettings { Address = "http://127.0.0.1:5180/", Muted = true }).Menu(Now);
        Assert.True(Assert.Single(muted, e => e.Action == MenuAction.ToggleMute).Checked);
        Assert.False(Assert.Single(muted, e => e.Action == MenuAction.MuteForAnHour).Enabled);

        var until = Now.AddHours(1);
        var forAnHour = Assert.Single(Connected(new TraySettings { Address = "http://127.0.0.1:5180/", MutedUntil = until }).Menu(Now), e => e.Action == MenuAction.MuteForAnHour);
        Assert.Equal(($"Muted until {until.ToLocalTime():HH:mm}", true), (forAnHour.Text, forAnHour.Checked));
    }

    [Fact]
    public void TheMenu_WhileReconnecting_SaysWhenAndWhy_AndListsNoTasks()
    {
        var presenter = Connected(null, false, Task("A"));
        var at = Now.AddSeconds(8);
        presenter.OnState(new WatcherState(ConnectionStatus.Reconnecting, Ben, "Can't reach the factory: refused.", at));

        var menu = Texts(presenter.Menu(Now));

        Assert.Equal($"Reconnecting at {at.ToLocalTime():HH:mm:ss}: Can't reach the factory: refused.", menu[1]);
        Assert.DoesNotContain(menu, t => t.StartsWith("Awaiting you", StringComparison.Ordinal));
    }

    [Fact]
    public void SignOut_IsOfferedOnlyWithAToken()
    {
        var noToken = new TrayPresenter(new TraySettings { Address = "http://127.0.0.1:5180/" }, false);

        Assert.False(Assert.Single(noToken.Menu(Now), e => e.Action == MenuAction.SignOut).Enabled);
        Assert.False(Assert.Single(new TrayPresenter(new TraySettings(), false).Menu(Now), e => e.Action == MenuAction.OpenBoard).Enabled);
    }
}
