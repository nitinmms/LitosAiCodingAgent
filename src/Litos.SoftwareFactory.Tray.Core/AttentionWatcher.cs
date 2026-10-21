using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Litos.SoftwareFactory.Tray;

public enum ConnectionStatus
{
    /// <summary>First connection, not yet listening.</summary>
    Connecting,

    /// <summary>Listening to the board's stream.</summary>
    Connected,

    /// <summary>The connection failed or ended; another try is due at <see cref="WatcherState.RetryAt"/>.</summary>
    Reconnecting,

    /// <summary>The factory refused the token. Nothing is retried until a new token is given.</summary>
    SignInNeeded,
}

/// <param name="Message">Why it is reconnecting, or the host's reason for refusing the token.</param>
public sealed record WatcherState(ConnectionStatus Status, TrayUser? User = null, string? Message = null, DateTimeOffset? RetryAt = null);

/// <summary>What the shell is told. Called on the watcher's own thread: a UI marshals to its own.</summary>
public interface IAttentionListener
{
    void OnState(WatcherState state);

    /// <summary>The tasks awaiting the user changed: the icon's count and the menu's list.</summary>
    void OnAwaiting(IReadOnlyList<TrayTask> tasks);

    /// <summary>Tasks that just started waiting on the user, merged when they came close together: ring once.</summary>
    void OnAlert(IReadOnlyList<TrayTask> arrived);
}

public sealed class WatcherOptions
{
    /// <summary>Arrivals this close together make one alert (§26.3: three at once ring once).</summary>
    public TimeSpan BurstWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The host writes a keep-alive every 15 s; three missed means the connection is dead.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Waits between tries: 2 s, doubling, at most a minute.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = DefaultBackoff;

    /// <summary>How waiting is done; tests replace it to control time.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public static TimeSpan DefaultBackoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Max(0, failures - 1))));
}

/// <summary>
/// Keeps the tray app connected to the factory and says when a task starts waiting on the user
/// (ReadMe_LitosSoftwareFactory_V1.md §26.3). It asks who the token belongs to, lists the tasks
/// (those already waiting count, but do not ring), then follows the board's event stream from the
/// list's cursor. When the connection drops it tries again with backoff and resumes after the last
/// event it saw, so the host replays what was missed: a task that started waiting meanwhile and
/// still waits rings once; one that came and went stays silent. A refused token stops it.
/// </summary>
public sealed class AttentionWatcher(FactoryClient client, IAttentionListener listener, WatcherOptions? options = null)
{
    private readonly WatcherOptions _options = options ?? new WatcherOptions();
    private readonly Lock _lock = new();
    private readonly List<TrayTask> _pending = [];
    private AwaitingSet? _awaiting;
    private bool _flushScheduled;

    /// <summary>Runs until <paramref name="ct"/> is cancelled or the token is refused.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        long? cursor = null;
        TrayUser? user = null;
        var failures = 0;
        listener.OnState(new WatcherState(ConnectionStatus.Connecting));

        while (!ct.IsCancellationRequested)
        {
            string reason;
            try
            {
                user = await client.MeAsync(ct);
                if (cursor is null || _awaiting?.UserId != user.Id)
                {
                    var (tasks, start) = await client.ListTasksAsync(ct);
                    lock (_lock)
                        _awaiting = new AwaitingSet(user.Id, tasks);
                    cursor = start;
                    listener.OnAwaiting(Snapshot());
                }

                using var response = await client.OpenEventsAsync(cursor.Value, ct);
                listener.OnState(new WatcherState(ConnectionStatus.Connected, user));
                failures = 0;

                await foreach (var evt in SseReader.ReadAsync(await response.Content.ReadAsStreamAsync(ct), _options.IdleTimeout, ct))
                {
                    if (long.TryParse(evt.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                        cursor = id;
                    if (evt.Type == "thread" && evt.Data is not null)
                        Take(TrayTask.Parse(evt.Data), ct);
                }

                // The host ended the stream: it is stopping, or the token was revoked (the next try says which).
                reason = "The factory closed the connection.";
            }
            catch (TokenRefusedException refused)
            {
                listener.OnState(new WatcherState(ConnectionStatus.SignInNeeded, user, refused.Message));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                reason = Describe(ex);
            }

            failures++;
            var wait = _options.Backoff(failures);
            listener.OnState(new WatcherState(ConnectionStatus.Reconnecting, user, reason, _options.Time.GetUtcNow() + wait));
            try
            {
                await _options.Delay(wait, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private IReadOnlyList<TrayTask> Snapshot()
    {
        lock (_lock)
            return _awaiting?.Tasks ?? [];
    }

    private void Take(TrayTask task, CancellationToken ct)
    {
        AwaitingChange change;
        IReadOnlyList<TrayTask> tasks;
        var flush = false;
        lock (_lock)
        {
            change = _awaiting!.Apply(task);
            tasks = _awaiting.Tasks;
            if (change.Arrived)
            {
                _pending.RemoveAll(t => t.Id == task.Id);
                _pending.Add(task);
                flush = !_flushScheduled;
                _flushScheduled = true;
            }
        }

        if (change.Changed)
            listener.OnAwaiting(tasks);
        if (flush)
            _ = FlushAfterWindowAsync(ct);
    }

    /// <summary>Rings once for everything that arrived in the window and still waits.</summary>
    private async Task FlushAfterWindowAsync(CancellationToken ct)
    {
        try
        {
            await _options.Delay(_options.BurstWindow, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        List<TrayTask> arrived;
        lock (_lock)
        {
            // One that stopped waiting within the window is not worth a ring; take its latest view.
            arrived = [.. _pending.Where(t => _awaiting!.Contains(t.Id)).Select(t => _awaiting!.Tasks.First(w => w.Id == t.Id))];
            _pending.Clear();
            _flushScheduled = false;
        }

        if (arrived.Count > 0)
            listener.OnAlert(arrived);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "The address answers, but not as a Litos factory. Check it in Settings.",
        HttpRequestException { StatusCode: not null } => ex.Message,
        HttpRequestException => $"Can't reach the factory: {ex.InnerException?.Message ?? ex.Message}",
        TimeoutException => ex.Message,
        IOException => "The connection to the factory was lost.",
        _ => "The factory sent something the tray app did not understand.",
    };
}
