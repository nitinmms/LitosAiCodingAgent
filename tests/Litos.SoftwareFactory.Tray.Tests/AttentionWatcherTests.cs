using System.Net;

namespace Litos.SoftwareFactory.Tray.Tests;

/// <summary>
/// The tray app's connection to the factory (§26.3): list, then stream; ring when one of the
/// user's tasks starts waiting; merge bursts; resume after a drop; stop on a refused token.
/// </summary>
public sealed class AttentionWatcherTests : IAsyncLifetime
{
    private readonly FakeFactory _factory = new();
    private readonly RecordingListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _running = Task.CompletedTask;

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await _running.WaitAsync(TimeSpan.FromSeconds(10));
        _factory.Dispose();
    }

    /// <summary>Real time, but short: a 200 ms burst window, retries 20 ms apart.</summary>
    private void Start(WatcherOptions? options = null)
    {
        var watcher = new AttentionWatcher(_factory.Client(), _listener, options ?? new WatcherOptions
        {
            BurstWindow = TimeSpan.FromMilliseconds(200),
            Backoff = _ => TimeSpan.FromMilliseconds(20),
        });
        _running = Task.Run(() => watcher.RunAsync(_stop.Token));
    }

    private Task ConnectedAsync() =>
        RecordingListener.UntilAsync(() => _listener.LastState?.Status == ConnectionStatus.Connected, "connected");

    [Fact]
    public async Task TasksAlreadyWaiting_AreCounted_ButDoNotRing()
    {
        _factory.Tasks.Add(FakeFactory.Waiting(A, "Add CSV export"));
        _factory.Tasks.Add(FakeFactory.Task(B, "Fix login"));
        Start();
        await ConnectedAsync();

        Assert.Equal(["Add CSV export"], _listener.LastAwaiting.Select(t => t.Title));
        Assert.Equal(new WatcherState(ConnectionStatus.Connected, new TrayUser(FakeFactory.Ben, "ben", "Ben Okafor")), _listener.LastState);
        await Task.Delay(400);
        Assert.Equal(0, _listener.AlertCount);
        // The stream starts where the list was read, so nothing between the two is missed.
        Assert.Contains("/api/events?after=10", _factory.Paths);
    }

    [Fact]
    public async Task ATaskThatStartsWaiting_RingsOnce()
    {
        _factory.Tasks.Add(FakeFactory.Task(A, "Add CSV export"));
        Start();
        await ConnectedAsync();

        await _factory.SendAsync((11, FakeFactory.Waiting(A, "Add CSV export", reason: "Comma or semicolon?")));
        await RecordingListener.UntilAsync(() => _listener.AlertCount == 1, "an alert");

        Assert.Equal(["Add CSV export"], _listener.Alerts[0].Select(t => t.Title));
        Assert.Equal("Comma or semicolon?", _listener.Alerts[0][0].StateReason);
        Assert.Single(_listener.LastAwaiting);

        // A further change while it still waits does not ring again.
        await _factory.SendAsync((12, FakeFactory.Waiting(A, "Add CSV export", state: "Blocked")));
        await RecordingListener.UntilAsync(() => _listener.LastAwaiting.Single().State == "Blocked", "the update");
        await Task.Delay(400);
        Assert.Equal(1, _listener.AlertCount);
    }

    [Fact]
    public async Task TasksArrivingTogether_RingOnce_AndAllAreNamed()
    {
        Start();
        await ConnectedAsync();

        await _factory.SendAsync(
            (11, FakeFactory.Waiting(A, "Add CSV export")),
            (12, FakeFactory.Waiting(B, "Fix login", state: "AwaitingHumanTesting")),
            (13, FakeFactory.Waiting(C, "Page through indexes", state: "PausedBudget")));
        await RecordingListener.UntilAsync(() => _listener.AlertCount == 1, "one alert");
        await Task.Delay(400);

        Assert.Equal(1, _listener.AlertCount);
        Assert.Equal(["Add CSV export", "Fix login", "Page through indexes"], _listener.Alerts[0].Select(t => t.Title).Order());
    }

    [Fact]
    public async Task OtherPeoplesTasks_AndATaskThatComesAndGoesAtOnce_DoNotRing()
    {
        Start();
        await ConnectedAsync();

        await _factory.SendAsync(
            (11, FakeFactory.Waiting(A, "Erin's task", owner: FakeFactory.Erin)),
            (12, FakeFactory.Waiting(B, "Answered straight away")),
            (13, FakeFactory.Task(B, "Answered straight away")));
        await RecordingListener.UntilAsync(() => _listener.Awaiting.Count >= 3, "the changes");
        await Task.Delay(400);

        Assert.Equal(0, _listener.AlertCount);
        Assert.Empty(_listener.LastAwaiting);
    }

    [Fact]
    public async Task ATaskThatStopsWaiting_LeavesTheList_AndRingsAgainIfItWaitsAgain()
    {
        Start();
        await ConnectedAsync();
        await _factory.SendAsync((11, FakeFactory.Waiting(A, "Add CSV export")));
        await RecordingListener.UntilAsync(() => _listener.AlertCount == 1, "the first alert");

        await _factory.SendAsync((12, FakeFactory.Task(A, "Add CSV export")));
        await RecordingListener.UntilAsync(() => _listener.LastAwaiting.Count == 0, "it leaves the list");
        await _factory.SendAsync((13, FakeFactory.Waiting(A, "Add CSV export", state: "AwaitingHumanTesting")));

        await RecordingListener.UntilAsync(() => _listener.AlertCount == 2, "the second alert");
    }

    [Fact]
    public async Task AfterTheStreamEnds_ItReconnects_AndResumesAfterTheLastEventItSaw()
    {
        Start();
        await ConnectedAsync();
        await _factory.SendAsync((11, FakeFactory.Task(A, "Add CSV export")));
        // An id-only block, as the host sends past events this user may not see.
        await _factory.SendRawAsync("id: 15\n\n");
        await RecordingListener.UntilAsync(() => _listener.Awaiting.Count >= 1, "the first event");
        await Task.Delay(100);

        await _factory.EndStreamAsync();
        await RecordingListener.UntilAsync(() => _factory.Paths.Contains("/api/events?after=15"), "the resumed stream");
        await RecordingListener.UntilAsync(() => _listener.Count(ConnectionStatus.Connected) == 2, "connected again");

        Assert.Equal(1, _listener.Count(ConnectionStatus.Reconnecting));
        // Resumed from the cursor: the task list is not read again.
        Assert.Single(_factory.Paths, p => p == "/api/threads");

        // What happened meanwhile is replayed, and a task still waiting rings.
        await _factory.SendAsync((16, FakeFactory.Waiting(B, "Fix login")));
        await RecordingListener.UntilAsync(() => _listener.AlertCount == 1, "the replayed alert");
    }

    [Fact]
    public async Task AnUnreachableFactory_IsRetried_SayingWhy()
    {
        _factory.Unreachable = 2;
        Start();

        await ConnectedAsync();

        Assert.Equal(2, _listener.Count(ConnectionStatus.Reconnecting));
        var retry = _listener.States.First(s => s.Status == ConnectionStatus.Reconnecting);
        Assert.StartsWith("Can't reach the factory: No connection could be made", retry.Message);
        Assert.NotNull(retry.RetryAt);
    }

    [Fact]
    public async Task ARefusedToken_StopsIt_WithTheHostsReason()
    {
        _factory.Refuse = (HttpStatusCode.Unauthorized, "The access token is not valid. Create a new one in the web app, under Settings → Tray app.");
        Start();

        await _running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionStatus.SignInNeeded, _listener.LastState!.Status);
        Assert.StartsWith("The access token is not valid.", _listener.LastState.Message);
        Assert.Single(_factory.Paths);
    }

    [Fact]
    public async Task ATokenRevokedWhileConnected_EndsInSignInNeeded()
    {
        Start();
        await ConnectedAsync();

        // The host ends a revoked token's stream; the next call is refused.
        _factory.Refuse = (HttpStatusCode.Unauthorized, "The access token is not valid.");
        await _factory.EndStreamAsync();

        await _running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionStatus.SignInNeeded, _listener.LastState!.Status);
    }

    [Fact]
    public async Task AnAdminWhoMustChangeTheirPassword_IsTold()
    {
        _factory.Refuse = (HttpStatusCode.Forbidden, "Choose a new password before going on.");
        Start();

        await _running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new WatcherState(ConnectionStatus.SignInNeeded, null, "Choose a new password before going on."), _listener.LastState);
    }

    [Fact]
    public async Task AStreamThatGoesSilent_IsTakenAsDead_AndReopened()
    {
        Start(new WatcherOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(300),
            Backoff = _ => TimeSpan.FromMilliseconds(20),
        });
        await ConnectedAsync();

        await RecordingListener.UntilAsync(() => _listener.Count(ConnectionStatus.Reconnecting) >= 1, "a reconnect");

        Assert.Contains("sent nothing", _listener.States.First(s => s.Status == ConnectionStatus.Reconnecting).Message);
    }

    [Fact]
    public async Task Stopping_EndsItQuietly()
    {
        Start();
        await ConnectedAsync();

        await _stop.CancelAsync();

        await _running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionStatus.Connected, _listener.LastState!.Status);
    }

    [Fact]
    public void Backoff_DoublesFromTwoSeconds_UpToAMinute()
    {
        Assert.Equal(
            [2, 4, 8, 16, 32, 60, 60],
            Enumerable.Range(1, 7).Select(n => WatcherOptions.DefaultBackoff(n).TotalSeconds));
    }
}
