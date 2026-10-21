using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Litos.SoftwareFactory.Tray.Tests;

/// <summary>
/// The three routes of a factory host a token may call, in memory: who the token is, the task
/// list with its cursor, and the board's event stream, which the test writes to as the host would.
/// Each GET /api/events opens a new stream; the test can end one, as a host stopping does.
/// </summary>
public sealed class FakeFactory : HttpMessageHandler
{
    public static readonly Guid Ben = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Erin = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public const string Token = "lsf_test-token";

    public static readonly Uri Address = new("http://127.0.0.1:5180/");

    private readonly ConcurrentQueue<Pipe> _streams = new();
    private TaskCompletionSource<Pipe> _nextStream = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid UserId { get; set; } = Ben;

    public List<object> Tasks { get; } = [];

    public long Cursor { get; set; } = 10;

    /// <summary>Answers every call with this status (and { error }) instead, e.g. a revoked token's 401.</summary>
    public (HttpStatusCode Status, string? Error)? Refuse { get; set; }

    /// <summary>The next calls fail as an unreachable host would, until this many have failed.</summary>
    public int Unreachable { get; set; }

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public IEnumerable<string> Paths => Requests.Select(r => r.RequestUri!.PathAndQuery);

    public FactoryClient Client() => new(FactoryClient.CreateHttp(Address, Token, this));

    /// <summary>A thread view as the host sends it (FactoryApi.ThreadView), with what the tray app reads.</summary>
    public static object Task(Guid id, string title, string turn = "AgentWorking", string state = "Running", Guid? owner = null,
        string? reason = null, int minute = 0) => new
    {
        id,
        projectId = Guid.Empty,
        ownerId = owner ?? Ben,
        title,
        turn,
        stage = "Implement",
        state,
        stateReason = reason,
        updatedAt = new DateTimeOffset(2026, 10, 21, 9, minute, 0, TimeSpan.Zero),
    };

    public static object Waiting(Guid id, string title, string state = "AwaitingDecision", Guid? owner = null, string? reason = null, int minute = 0) =>
        Task(id, title, "AwaitingYou", state, owner, reason, minute);

    /// <summary>The stream the tray app opened most recently, waiting for it to be opened if need be.</summary>
    public async Task<Pipe> StreamAsync()
    {
        var pipe = await _nextStream.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return pipe;
    }

    /// <summary>Writes thread events, as the host's board stream frames them.</summary>
    public async Task SendAsync(params (long Id, object Thread)[] events)
    {
        var pipe = await StreamAsync();
        var text = new StringBuilder();
        foreach (var (id, thread) in events)
            text.Append($"id: {id}\nevent: thread\ndata: {JsonSerializer.Serialize(thread)}\n\n");
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()));
    }

    public async Task SendRawAsync(string text) => await (await StreamAsync()).Writer.WriteAsync(Encoding.UTF8.GetBytes(text));

    /// <summary>Ends the open stream, as a host that stops does; the next one is a new stream.</summary>
    public async Task EndStreamAsync()
    {
        var pipe = await StreamAsync();
        _nextStream = new TaskCompletionSource<Pipe>(TaskCreationOptions.RunContinuationsAsynchronously);
        await pipe.Writer.CompleteAsync();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (request.Headers.Authorization?.ToString() != $"Bearer {Token}")
            return Answer(HttpStatusCode.Unauthorized, new { error = "No token." });
        if (Unreachable > 0)
        {
            Unreachable--;
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
        }

        if (Refuse is { } refusal)
            return Answer(refusal.Status, refusal.Error is null ? null : new { error = refusal.Error });

        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/auth/me")
            return Answer(HttpStatusCode.OK, new { id = UserId, userName = "ben", displayName = "Ben Okafor", roles = new[] { "Member" } });
        if (path == "/api/threads")
        {
            var response = Json(HttpStatusCode.OK, Tasks);
            response.Headers.Add(FactoryClient.EventCursorHeader, Cursor.ToString());
            return System.Threading.Tasks.Task.FromResult(response);
        }

        if (path == "/api/events")
        {
            var pipe = new Pipe();
            _streams.Enqueue(pipe);
            var content = new StreamContent(pipe.Reader.AsStream());
            content.Headers.ContentType = new("text/event-stream");
            _nextStream.TrySetResult(pipe);
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        return Answer(HttpStatusCode.NotFound, null);
    }

    private static Task<HttpResponseMessage> Answer(HttpStatusCode status, object? body) => System.Threading.Tasks.Task.FromResult(Json(status, body));

    private static HttpResponseMessage Json(HttpStatusCode status, object? body) => new(status)
    {
        Content = body is null ? new StringContent("") : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>Records what the watcher tells the shell, and waits for it.</summary>
public sealed class RecordingListener : IAttentionListener
{
    private readonly Lock _lock = new();

    public List<WatcherState> States { get; } = [];

    public List<IReadOnlyList<TrayTask>> Awaiting { get; } = [];

    public List<IReadOnlyList<TrayTask>> Alerts { get; } = [];

    public void OnState(WatcherState state)
    {
        lock (_lock)
            States.Add(state);
    }

    public void OnAwaiting(IReadOnlyList<TrayTask> tasks)
    {
        lock (_lock)
            Awaiting.Add(tasks);
    }

    public void OnAlert(IReadOnlyList<TrayTask> arrived)
    {
        lock (_lock)
            Alerts.Add(arrived);
    }

    public IReadOnlyList<TrayTask> LastAwaiting
    {
        get
        {
            lock (_lock)
                return Awaiting.Count == 0 ? [] : Awaiting[^1];
        }
    }

    public WatcherState? LastState
    {
        get
        {
            lock (_lock)
                return States.Count == 0 ? null : States[^1];
        }
    }

    public int AlertCount
    {
        get
        {
            lock (_lock)
                return Alerts.Count;
        }
    }

    public int Count(ConnectionStatus status)
    {
        lock (_lock)
            return States.Count(s => s.Status == status);
    }

    /// <summary>Waits until the condition holds, failing the test after a few seconds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
