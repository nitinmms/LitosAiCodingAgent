using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The board's live stream, GET /api/events (m2-architecture.md §6): every thread change in the
/// projects the user can see, as the thread's whole view, replacing the board's polling.
/// </summary>
public sealed class BoardStreamTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private HttpClient _ben = null!;
    private Guid _sales, _filedb, _benId;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _sales = await _host.RegisterProjectAsync("salesapp");
        _filedb = await _host.RegisterProjectAsync("filedb-sharp");
        _benId = await _host.CreateMemberAsync("ben", _sales);
        _ben = await _host.SignedInAsync("ben");
    }

    public async Task DisposeAsync()
    {
        _ben.Dispose();
        await _host.DisposeAsync();
    }

    /// <summary>Reads SSE "thread" events until <paramref name="count"/> arrive or the time is up.</summary>
    private static async Task<List<(long Id, JsonElement Thread)>> ReadThreadsAsync(HttpResponseMessage response, int count, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));
        var events = new List<(long, JsonElement)>();
        long id = 0;
        string? type = null;
        try
        {
            while (events.Count < count && await reader.ReadLineAsync(cancel.Token) is { } line)
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal))
                    id = long.Parse(line[4..]);
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                    type = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal) && type == "thread")
                    events.Add((id, JsonDocument.Parse(line[6..]).RootElement.Clone()));
                else if (line.Length == 0)
                    type = null;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return events;
    }

    private static Task<HttpResponseMessage> OpenAsync(HttpClient client, long after) =>
        client.GetAsync($"api/events?after={after}", HttpCompletionOption.ResponseHeadersRead);

    private async Task<long> CursorAsync(HttpClient client)
    {
        using var list = await client.GetAsync("api/threads");
        return long.Parse(list.Headers.GetValues("X-Event-Cursor").Single());
    }

    [Fact]
    public async Task TheThreadList_SaysWhereTheStreamStarts()
    {
        await _host.CreateThreadAsync(_sales);

        Assert.Equal(await _host.Store.LastEventSequenceAsync(default), await CursorAsync(_host.Client));
    }

    [Fact]
    public async Task AMember_HearsTheirProjectsThreads_AsWholeViews_AndNothingFromOtherProjects()
    {
        var cursor = await CursorAsync(_ben);
        using var response = await OpenAsync(_ben, cursor);
        var reading = ReadThreadsAsync(response, 2, TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        await _host.CreateThreadAsync(_filedb, "Compact the file");
        var threadId = await _host.CreateThreadAsync(_sales, "Add CSV export");
        await _host.DelegateAsync(threadId);

        var heard = await reading;
        Assert.All(heard, h => Assert.Equal(threadId, h.Thread.GetProperty("id").GetGuid()));
        Assert.Equal("Add CSV export", heard[0].Thread.GetProperty("title").GetString());
        Assert.Equal(_sales, heard[0].Thread.GetProperty("projectId").GetGuid());
        Assert.Equal("AwaitingAgent", heard[^1].Thread.GetProperty("turn").GetString());
        Assert.True(heard[0].Id > cursor);
    }

    [Fact]
    public async Task TheAdmin_HearsEveryProject()
    {
        var cursor = await CursorAsync(_host.Client);
        using var response = await OpenAsync(_host.Client, cursor);
        var reading = ReadThreadsAsync(response, 2, TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        await _host.CreateThreadAsync(_filedb);
        await _host.CreateThreadAsync(_sales);

        Assert.Equal(new[] { _filedb, _sales }, (await reading).Select(h => h.Thread.GetProperty("projectId").GetGuid()));
    }

    /// <summary>A thread changed several times before the stream reads is sent once, as it is now.</summary>
    [Fact]
    public async Task SeveralChanges_ArriveAsTheThreadsLatestView()
    {
        var threadId = await _host.CreateThreadAsync(_sales, "First title");
        await _host.Client.PatchAsJsonAsync($"api/threads/{threadId}", new { title = "Second title" });
        await _host.Client.PatchAsJsonAsync($"api/threads/{threadId}", new { title = "Third title" });

        using var response = await OpenAsync(_ben, 0);
        var heard = Assert.Single(await ReadThreadsAsync(response, 1, TimeSpan.FromSeconds(10)));

        Assert.Equal("Third title", heard.Thread.GetProperty("title").GetString());
    }

    /// <summary>Who can see what is asked again on every pass: joining a project shows its changes.</summary>
    [Fact]
    public async Task JoiningAProjectWhileListening_ShowsItsChangesFromThen()
    {
        var cursor = await CursorAsync(_ben);
        using var response = await OpenAsync(_ben, cursor);
        var reading = ReadThreadsAsync(response, 1, TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        await _host.PostAsync($"api/projects/{_filedb}/members", new { userId = _benId }, HttpStatusCode.Created);
        var threadId = await _host.CreateThreadAsync(_filedb, "Compact the file");

        Assert.Equal(threadId, Assert.Single(await reading).Thread.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task NobodySignedIn_CannotListen()
    {
        using var response = await _host.NewClient().GetAsync("api/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
