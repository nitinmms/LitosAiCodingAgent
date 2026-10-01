using System.Net;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Infrastructure.Workers;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Workers;

public class HttpWorkerClientTests
{
    private static (HttpWorkerClient Client, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new HttpWorkerClient(new HttpClient(handler), new Uri("http://127.0.0.1:43210/"), "launch-secret"), handler);
    }

    private static string Sse(params string[] payloads) =>
        string.Concat(payloads.Select(p => $"event: agent-event\ndata: {p}\n\n"));

    private const string Text = """{"Text":"Reading the file."}""";
    private const string ToolStarted = """{"CallId":"c1","ToolName":"read_file"}""";
    private const string ToolCompleted = """{"CallId":"c1","ToolName":"read_file","Arguments":{"path":"a.txt"}}""";
    private const string ToolResult = """{"CallId":"c1","ToolName":"read_file","Result":{"Text":"contents","IsError":false}}""";
    private const string KeepAlive = """{"KeepAlive":true}""";
    private const string Completed = """{"Message":{"Role":1,"Content":[]},"Usage":{"InputTokens":10,"OutputTokens":5}}""";

    [Fact]
    public async Task RunTurnAsync_PostsTheBriefAndTurnKind_WithTheSecret()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, Sse(Text, Completed), "text/event-stream");

        await client.RunTurnAsync("session 1", TurnKind.Review, "Review this change.", maxToolCalls: 200, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:43210/sessions/session%201/turns", request.Uri.AbsoluteUri);
        Assert.Equal("launch-secret", request.Headers["X-Factory-Secret"]);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Review this change.", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("Review", body.RootElement.GetProperty("turnKind").GetString());
    }

    [Fact]
    public async Task RunTurnAsync_StreamEnds_IsCompleted_AndCountsToolResults()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, Sse(Text, ToolStarted, ToolCompleted, ToolResult, KeepAlive, ToolResult, Completed), "text/event-stream");

        var result = await client.RunTurnAsync("s", TurnKind.Implement, "brief", 200, default);

        Assert.True(result.Completed);
        Assert.Equal(2, result.ToolCalls); // results only: started/completed are the same call
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RunTurnAsync_ErrorEvent_IsNotCompleted_AndCarriesTheMessage()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, Sse(Text, """{"Exception":{"Message":"The model gateway refused the call: budget_exhausted"}}"""), "text/event-stream");

        var result = await client.RunTurnAsync("s", TurnKind.Implement, "brief", 200, default);

        Assert.False(result.Completed);
        Assert.Equal("The model gateway refused the call: budget_exhausted", result.Error);
    }

    /// <summary>§8.5: at most N tool calls per turn. Past the cap the turn is cancelled.</summary>
    [Fact]
    public async Task RunTurnAsync_TooManyToolCalls_CancelsTheTurn()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, Sse(ToolResult, ToolResult, ToolResult, ToolResult), "text/event-stream");
        handler.Enqueue(HttpStatusCode.OK);

        var result = await client.RunTurnAsync("s", TurnKind.Implement, "brief", maxToolCalls: 2, default);

        Assert.False(result.Completed);
        Assert.Equal(3, result.ToolCalls);
        Assert.Equal("The turn exceeded 2 tool calls.", result.Error);
        var cancel = handler.Requests[1];
        Assert.Equal("/sessions/s/cancel", cancel.Uri.AbsolutePath);
        Assert.Equal("launch-secret", cancel.Headers["X-Factory-Secret"]);
    }

    [Fact]
    public async Task RunTurnAsync_ExactlyAtTheCap_IsAllowed()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, Sse(ToolResult, ToolResult, Completed), "text/event-stream");

        var result = await client.RunTurnAsync("s", TurnKind.Implement, "brief", maxToolCalls: 2, default);

        Assert.True(result.Completed);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RunTurnAsync_Accepted_MeansATurnWasAlreadyRunning_WhichIsAnError()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.Accepted, "\"Message delivered to the in-progress turn.\"");

        var result = await client.RunTurnAsync("s", TurnKind.Implement, "brief", 200, default);

        Assert.False(result.Completed);
        Assert.Contains("already running", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task RunTurnAsync_WorkerRefuses_IsNotCompleted(HttpStatusCode status, string code)
    {
        var (client, handler) = Create();
        handler.Enqueue(status);

        var result = await client.RunTurnAsync("s", TurnKind.Implement, "brief", 200, default);

        Assert.False(result.Completed);
        Assert.Contains(code, result.Error);
    }

    [Fact]
    public async Task RunTurnAsync_IgnoresLinesThatAreNotEventData()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, ": comment\nevent: agent-event\ndata: not json\n\ndata: [1,2]\n\n" + Sse(Completed), "text/event-stream");

        var result = await client.RunTurnAsync("s", TurnKind.Implement, "brief", 200, default);

        Assert.True(result.Completed);
        Assert.Equal(0, result.ToolCalls);
    }

    [Theory]
    [InlineData(ToolResult, (int)HttpWorkerClient.TurnEvent.ToolResult)]
    [InlineData(ToolStarted, (int)HttpWorkerClient.TurnEvent.Other)]
    [InlineData(ToolCompleted, (int)HttpWorkerClient.TurnEvent.Other)]
    [InlineData(Text, (int)HttpWorkerClient.TurnEvent.Other)]
    [InlineData(KeepAlive, (int)HttpWorkerClient.TurnEvent.Other)]
    [InlineData(Completed, (int)HttpWorkerClient.TurnEvent.Other)]
    [InlineData("""{"CallId":"c1","Reason":"steered"}""", (int)HttpWorkerClient.TurnEvent.Other)]
    [InlineData("""{"Exception":{"Message":"boom"}}""", (int)HttpWorkerClient.TurnEvent.Error)]
    [InlineData("not json", (int)HttpWorkerClient.TurnEvent.Other)]
    public void Classify_TellsEventsApartByShape(string json, int expected)
    {
        Assert.Equal((HttpWorkerClient.TurnEvent)expected, HttpWorkerClient.Classify(json, out _));
    }

    [Fact]
    public void Classify_ErrorWithoutAMessage_StillReportsAFailure()
    {
        Assert.Equal(HttpWorkerClient.TurnEvent.Error, HttpWorkerClient.Classify("""{"Exception":null}""", out var message));
        Assert.Equal("The turn failed.", message);
    }

    [Fact]
    public async Task SteerAsync_PostsTheMessageToTheRunningTurn()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.Accepted);

        await client.SteerAsync("s", "Also handle empty results.", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/sessions/s/turns", request.Uri.AbsolutePath);
        Assert.Equal("Also handle empty results.", JsonDocument.Parse(request.Body!).RootElement.GetProperty("input").GetString());
    }

    /// <summary>With no turn running, the worker would start a turn from the steering text.
    /// That is not steering, so it is stopped at once.</summary>
    [Fact]
    public async Task SteerAsync_NoTurnWasRunning_CancelsTheTurnItAccidentallyStarted()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, Sse(Text), "text/event-stream");
        handler.Enqueue(HttpStatusCode.OK);

        await client.SteerAsync("s", "message", default);

        Assert.Equal("/sessions/s/cancel", handler.Requests[1].Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task CancelAsync_ReportsWhetherATurnWasRunning(HttpStatusCode status, bool expected)
    {
        var (client, handler) = Create();
        handler.Enqueue(status);

        Assert.Equal(expected, await client.CancelAsync("s", default));
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompactAsync_SendsTheInstruction_AndReturnsWhetherAnythingWasCompacted(bool compacted)
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, JsonSerializer.Serialize(new CompactResponse(compacted), FactoryWire.Json));

        var result = await client.CompactAsync("session-1", "Keep the acceptance criteria.", default);

        Assert.Equal(compacted, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/sessions/session-1/compact", request.Uri.AbsolutePath);
        Assert.Equal("launch-secret", request.Headers["X-Factory-Secret"]);
        Assert.Equal(
            new CompactRequest("Keep the acceptance criteria."),
            JsonSerializer.Deserialize<CompactRequest>(request.Body!, FactoryWire.Json));
    }

    [Fact]
    public async Task CompactAsync_WorkerFails_Throws_SoTheHostDoesNotStartALargeTurnUncompacted()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CompactAsync("s", "instruction", default));
    }

    [Fact]
    public async Task ShutdownAsync_PostsToShutdown()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK);

        await client.ShutdownAsync(default);

        Assert.Equal("/shutdown", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task ShutdownAsync_ConnectionDropsAsTheWorkerExits_IsNotAnError()
    {
        var (client, handler) = Create();
        handler.EnqueueFailure();

        await client.ShutdownAsync(default);
    }
}
