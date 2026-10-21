namespace Litos.SoftwareFactory.Tray.Tests;

public sealed class AwaitingSetTests
{
    private static TrayTask Task(Guid id, string title, string turn = "AgentWorking", Guid? owner = null, int minute = 0, string state = "Running") =>
        new(id, owner ?? FakeFactory.Ben, title, turn, state, "Implement", null, new DateTimeOffset(2026, 10, 21, 9, minute, 0, TimeSpan.Zero));

    private static TrayTask Waiting(Guid id, string title, Guid? owner = null, int minute = 0) =>
        Task(id, title, TrayTask.AwaitingYou, owner, minute, "AwaitingDecision");

    [Fact]
    public void StartsWithTheUsersWaitingTasks_OnlyTheirs()
    {
        var set = new AwaitingSet(FakeFactory.Ben, [
            Waiting(Guid.NewGuid(), "Mine"),
            Waiting(Guid.NewGuid(), "Erin's", FakeFactory.Erin),
            Task(Guid.NewGuid(), "Running"),
        ]);

        Assert.Equal(["Mine"], set.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void ATaskRings_OnlyWhenItStartsWaiting()
    {
        var id = Guid.NewGuid();
        var set = new AwaitingSet(FakeFactory.Ben, [Task(id, "Add CSV export")]);

        Assert.Equal(new AwaitingChange(false, false), set.Apply(Task(id, "Add CSV export")));
        Assert.Equal(new AwaitingChange(true, true), set.Apply(Waiting(id, "Add CSV export")));
        Assert.Equal(new AwaitingChange(true, false), set.Apply(Waiting(id, "Add CSV export, renamed")));
        Assert.Equal("Add CSV export, renamed", set.Tasks.Single().Title);
        Assert.Equal(new AwaitingChange(true, false), set.Apply(Task(id, "Add CSV export")));
        Assert.Equal(0, set.Count);
        Assert.Equal(new AwaitingChange(true, true), set.Apply(Waiting(id, "Add CSV export")));
    }

    [Fact]
    public void SomeoneElsesTask_NeverRings()
    {
        var set = new AwaitingSet(FakeFactory.Ben, []);

        Assert.Equal(new AwaitingChange(false, false), set.Apply(Waiting(Guid.NewGuid(), "Erin's", FakeFactory.Erin)));
    }

    [Fact]
    public void TheMostRecentlyChanged_ComesFirst()
    {
        var set = new AwaitingSet(FakeFactory.Ben, [Waiting(Guid.NewGuid(), "Older", minute: 1), Waiting(Guid.NewGuid(), "Newer", minute: 5)]);
        set.Apply(Waiting(Guid.NewGuid(), "Newest", minute: 9));

        Assert.Equal(["Newest", "Newer", "Older"], set.Tasks.Select(t => t.Title));
    }
}

public sealed class AlertTextTests
{
    private static TrayTask Task(string title, string state, string? reason = null) =>
        new(Guid.NewGuid(), FakeFactory.Ben, title, TrayTask.AwaitingYou, state, "Implement", reason, default);

    [Theory]
    [InlineData("AwaitingDecision", "Add CSV export needs a decision", "Decision needed")]
    [InlineData("AwaitingHumanTesting", "Add CSV export is ready for testing", "Ready for testing")]
    [InlineData("Blocked", "Add CSV export is blocked", "Blocked")]
    [InlineData("PausedBudget", "Add CSV export hit its budget", "Budget paused")]
    [InlineData("Interrupted", "Add CSV export was interrupted", "Interrupted")]
    [InlineData("Draft", "Add CSV export has a spec to approve", "Spec to approve")]
    public void OneTask_IsNamedWithItsReason(string state, string title, string body)
    {
        Assert.Equal((title, body), AlertText.Notification([Task("Add CSV export", state)]));
    }

    [Fact]
    public void TheHostsReason_IsTheBody_WhenItGaveOne()
    {
        Assert.Equal("Comma or semicolon?", AlertText.Notification([Task("Add CSV export", "AwaitingDecision", "  Comma or semicolon? ")]).Body);
    }

    [Fact]
    public void SeveralTasks_AreListed()
    {
        var (title, body) = AlertText.Notification([Task("Add CSV export", "AwaitingDecision"), Task("Fix login", "Blocked")]);

        Assert.Equal("2 tasks need you", title);
        Assert.Equal("• Add CSV export: decision needed\n• Fix login: blocked", body);
    }

    [Fact]
    public void LongTexts_AreCutToWhatWindowsShows()
    {
        var (title, body) = AlertText.Notification([Task(new string('x', 100), "Blocked", new string('y', 400))]);

        Assert.Equal(AlertText.TitleLimit, title.Length);
        Assert.EndsWith("…", title);
        Assert.Equal(AlertText.BodyLimit, body.Length);
        Assert.Equal("short", AlertText.Fit("short", 10));
    }

    [Theory]
    [InlineData(ConnectionStatus.Connected, 0, false, "Litos Factory: nothing awaits you")]
    [InlineData(ConnectionStatus.Connected, 1, false, "Litos Factory: 1 task awaits you")]
    [InlineData(ConnectionStatus.Connected, 3, true, "Litos Factory: 3 tasks await you (muted)")]
    [InlineData(ConnectionStatus.Reconnecting, 3, false, "Litos Factory: reconnecting…")]
    [InlineData(ConnectionStatus.SignInNeeded, 0, false, "Litos Factory: sign in needed")]
    [InlineData(ConnectionStatus.Connecting, 0, false, "Litos Factory: connecting…")]
    public void TheTooltip_SaysTheState(ConnectionStatus status, int awaiting, bool muted, string expected)
    {
        Assert.Equal(expected, AlertText.Tooltip(new WatcherState(status), awaiting, muted));
    }
}
