using System.Net;

namespace Litos.SoftwareFactory.Tray.Tests;

public sealed class FactoryClientTests : IDisposable
{
    private readonly FakeFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Me_SaysWhoTheTokenBelongsTo_SendingTheTokenAsBearer()
    {
        var me = await _factory.Client().MeAsync(default);

        Assert.Equal(new TrayUser(FakeFactory.Ben, "ben", "Ben Okafor"), me);
        Assert.Equal("Ben Okafor", me.Name);
        Assert.Equal("Bearer lsf_test-token", _factory.Requests.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task TheTaskList_ComesWithTheCursorToStreamFrom()
    {
        _factory.Tasks.Add(FakeFactory.Waiting(Guid.NewGuid(), "Add CSV export", reason: "Comma?", minute: 5));
        _factory.Cursor = 42;

        var (tasks, cursor) = await _factory.Client().ListTasksAsync(default);

        var task = Assert.Single(tasks);
        Assert.Equal(("Add CSV export", "AwaitingYou", "AwaitingDecision", "Comma?"), (task.Title, task.Turn, task.State, task.StateReason));
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 5, 0, TimeSpan.Zero), task.UpdatedAt);
        Assert.Equal(42, cursor);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "The access token is not valid.", "The access token is not valid.")]
    [InlineData(HttpStatusCode.Unauthorized, null, "The factory did not accept the access token.")]
    [InlineData(HttpStatusCode.Forbidden, "Choose a new password before going on.", "Choose a new password before going on.")]
    public async Task ARefusal_IsATokenProblem_WithTheHostsReason(HttpStatusCode status, string? error, string expected)
    {
        _factory.Refuse = (status, error);

        var refused = await Assert.ThrowsAsync<TokenRefusedException>(() => _factory.Client().MeAsync(default));

        Assert.Equal(expected, refused.Message);
    }

    [Fact]
    public async Task AServerError_IsWorthRetrying()
    {
        _factory.Refuse = (HttpStatusCode.BadGateway, null);

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => _factory.Client().ListTasksAsync(default));

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal("The factory answered 502.", failed.Message);
    }

    [Fact]
    public async Task AnAddressThatIsNotAFactory_IsSaidSo()
    {
        using var handler = new NotAFactory();
        var client = new FactoryClient(FactoryClient.CreateHttp(new Uri("http://127.0.0.1:9/"), "lsf_x", handler));

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListTasksAsync(default));

        Assert.Contains("Is the address a Litos factory?", failed.Message);
    }

    [Fact]
    public async Task TheStream_IsAskedForAfterTheCursor()
    {
        using var response = await _factory.Client().OpenEventsAsync(42, default);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var request = _factory.Requests.Single();
        Assert.Equal("/api/events?after=42", request.RequestUri!.PathAndQuery);
        Assert.Equal("text/event-stream", request.Headers.Accept.Single().MediaType);
    }

    [Fact]
    public void TheHttpClient_HasNoOverallTimeout_SoTheStreamCanStayOpen()
    {
        using var http = FactoryClient.CreateHttp(FakeFactory.Address, "lsf_x");

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        Assert.Equal(FakeFactory.Address, http.BaseAddress);
    }

    /// <summary>A web server that answers 200 to anything, without the factory's cursor header.</summary>
    private sealed class NotAFactory : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
    }
}
