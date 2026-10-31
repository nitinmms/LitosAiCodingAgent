using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Tray;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The tray app's core (src/Litos.SoftwareFactory.Tray.Core) against the real host, as a tray app
/// on another PC uses it: an access token, the task list and the board's stream (§26.3). Its own
/// tests use a fake factory; these keep the two in step: field names, the cursor header, the
/// token's scope and what a revoke does.
/// </summary>
public sealed class TrayAppTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _project;
    private readonly Listener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _watching = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync();
        _project = await _host.RegisterProjectAsync();
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await _watching.WaitAsync(TimeSpan.FromSeconds(10));
        await _host.DisposeAsync();
    }

    private async Task<(Guid Id, string Secret)> TokenAsync()
    {
        var created = await _host.PostAsync("api/access-tokens", new { name = "Tray test" }, HttpStatusCode.Created);
        return (created.GetProperty("token").GetProperty("id").GetGuid(), created.GetProperty("secret").GetString()!);
    }

    private void Watch(string secret)
    {
        var client = new FactoryClient(FactoryClient.CreateHttp(_host.Url, secret));
        var watcher = new AttentionWatcher(client, _listener, new WatcherOptions { BurstWindow = TimeSpan.FromMilliseconds(200) });
        _watching = Task.Run(() => watcher.RunAsync(_stop.Token));
    }

    /// <summary>A task of the Admin's that the scripted agent takes to "Ready for testing".</summary>
    private async Task<Guid> HandedOffAsync(string title)
    {
        var id = await _host.CreateThreadAsync(_project, title);
        await _host.DelegateAsync(id);
        await _host.WaitForStateAsync(id, LifecycleState.AwaitingHumanTesting);
        return id;
    }

    private static async Task UntilAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ATaskHandedOffToItsOwner_RingsTheirTrayApp_AndOneAlreadyWaitingDoesNot()
    {
        await HandedOffAsync("Already waiting");
        var (_, secret) = await TokenAsync();
        Watch(secret);
        await UntilAsync(() => _listener.State?.Status == ConnectionStatus.Connected, "connected");

        Assert.Equal(["Already waiting"], _listener.Awaiting.Select(t => t.Title));
        Assert.Equal(await _host.AdminIdAsync(), _listener.State!.User!.Id);

        var id = await HandedOffAsync("Add CSV export");

        await UntilAsync(() => !_listener.Alerts.IsEmpty, "the alert");
        var alert = Assert.Single(Assert.Single(_listener.Alerts));
        Assert.Equal((id, "Add CSV export", "AwaitingHumanTesting"), (alert.Id, alert.Title, alert.State));
        Assert.Equal(("Add CSV export is ready for testing"), AlertText.Notification([alert]).Title);
        await UntilAsync(() => _listener.Awaiting.Count == 2, "both in the menu");
    }

    /// <summary>A work turn that asks a decision once <paramref name="ask"/> lets it, as a real agent's submission does.</summary>
    private void AsksADecision(Task ask) => _host.Workers.Script.Enqueue(async call =>
    {
        await ask.WaitAsync(call.Token);
        await call.Worker.SubmitAsync(call.SessionId, new DecisionSubmission("All rows or this page?", "The request does not say.", ["All rows", "This page"], "All rows"));
        await Task.Delay(Timeout.Infinite, call.Token);
        return FakeWorkerLauncher.Done();
    });

    [Fact]
    public async Task ADecisionAskedMidRun_RingsTheTrayApp_AndTheNextOneRingsAgain()
    {
        var (_, secret) = await TokenAsync();
        Watch(secret);
        await UntilAsync(() => _listener.State?.Status == ConnectionStatus.Connected, "connected");

        AsksADecision(Task.CompletedTask);
        var id = await _host.CreateThreadAsync(_project, "Add CSV export");
        await _host.DelegateAsync(id);

        await UntilAsync(() => !_listener.Alerts.IsEmpty, "the alert");
        var alert = Assert.Single(Assert.Single(_listener.Alerts));
        Assert.Equal((id, "AwaitingDecision"), (alert.Id, alert.State));

        AsksADecision(Task.CompletedTask);
        var decision = Assert.Single((await _host.WaitForStateAsync(id, LifecycleState.AwaitingDecision)).Decisions);
        await _host.PostAsync($"api/decisions/{decision.Id}/answer", new { answer = "All rows." }, HttpStatusCode.OK);

        await UntilAsync(() => _listener.Alerts.Count == 2, "the second alert");
    }

    [Fact]
    public async Task ADecisionAskedWhileAQuestionIsBeingAnswered_StillRingsTheTrayApp()
    {
        var (_, secret) = await TokenAsync();
        Watch(secret);
        await UntilAsync(() => _listener.State?.Status == ConnectionStatus.Connected, "connected");

        var ask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AsksADecision(ask.Task);
        _host.Workers.Script.Enqueue(async call =>
        {
            answering.TrySetResult();
            await answer.Task.WaitAsync(call.Token);
            return new TurnStreamResult(true, 1, null, "It is halfway.");
        });
        var id = await _host.CreateThreadAsync(_project, "Add CSV export");
        await _host.DelegateAsync(id);
        await UntilAsync(() => _host.Workers.Turns.Count == 1, "the work turn");

        await _host.PostAsync($"api/threads/{id}/messages", new { messageId = Guid.NewGuid().ToString(), text = "How far along is it?" }, HttpStatusCode.Accepted);
        await answering.Task.WaitAsync(TimeSpan.FromSeconds(30));
        ask.SetResult();

        await UntilAsync(() => !_listener.Alerts.IsEmpty, "the alert");
        Assert.Equal((id, "AwaitingDecision"), Assert.Single(Assert.Single(_listener.Alerts)) is var alert ? (alert.Id, alert.State) : default);
        answer.SetResult();
    }

    [Fact]
    public async Task RevokingTheToken_TurnsTheTrayAppToSignInNeeded()
    {
        var (tokenId, secret) = await TokenAsync();
        Watch(secret);
        await UntilAsync(() => _listener.State?.Status == ConnectionStatus.Connected, "connected");

        await _host.PostAsync($"api/access-tokens/{tokenId}/revoke", null, HttpStatusCode.NoContent);
        // Any change wakes the stream for its next pass, which finds the token revoked.
        await _host.CreateThreadAsync(_project, "Wake the stream");

        await _watching.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ConnectionStatus.SignInNeeded, _listener.State!.Status);
        Assert.Contains("Settings → Tray app", _listener.State.Message);
    }

    [Fact]
    public async Task TheTrayAppsCalls_AreExactlyWhatATokenMayMake()
    {
        var (_, secret) = await TokenAsync();
        var client = new FactoryClient(FactoryClient.CreateHttp(_host.Url, secret));

        var me = await client.MeAsync(default);
        var (_, cursor) = await client.ListTasksAsync(default);
        using var stream = await client.OpenEventsAsync(cursor, default);

        Assert.Equal("admin", me.UserName);
        Assert.Equal(await _host.Store.LastEventSequenceAsync(default), cursor);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
    }

    private sealed class Listener : IAttentionListener
    {
        private IReadOnlyList<TrayTask> _awaiting = [];
        private WatcherState? _state;

        public ConcurrentQueue<IReadOnlyList<TrayTask>> Alerts { get; } = new();

        public IReadOnlyList<TrayTask> Awaiting => Volatile.Read(ref _awaiting);

        public WatcherState? State => Volatile.Read(ref _state);

        public void OnState(WatcherState state) => Volatile.Write(ref _state, state);

        public void OnAwaiting(IReadOnlyList<TrayTask> tasks) => Volatile.Write(ref _awaiting, tasks);

        public void OnAlert(IReadOnlyList<TrayTask> arrived) => Alerts.Enqueue(arrived);
    }
}
