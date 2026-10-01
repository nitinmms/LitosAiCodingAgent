using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>A scratch directory deleted when the test ends.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("litos-factory-worker-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A kernel subprocess killed a moment ago can still hold a handle.
        }
    }
}

/// <summary>Serves queued responses and records every request, for tests that need no real socket.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public void Enqueue(System.Net.HttpStatusCode status, string body = "", string contentType = "application/json") =>
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    public void EnqueueJson<T>(T value) => Enqueue(System.Net.HttpStatusCode.OK, JsonSerializer.Serialize(value, FactoryWire.Json));

    public void EnqueueFailure() => _responses.Enqueue(() => throw new HttpRequestException("Connection refused."));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new CapturedRequest(
            request.Method, request.RequestUri!, body,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));

        if (_responses.Count == 0)
            throw new InvalidOperationException($"No response queued for {request.Method} {request.RequestUri}.");
        return _responses.Dequeue()();
    }
}

public sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers);

public static class TestOptions
{
    public const string Secret = "launch-secret-for-tests";

    public static WorkerOptions Create(string dataDirectory, Uri? hostUrl = null, bool ptc = false) => new(
        RunId: "run-1", Secret: Secret, HostUrl: hostUrl ?? new Uri("http://127.0.0.1:5180/"), Provider: "openrouter",
        Model: "vendor/model", ContextLength: 200_000, DataDirectory: dataDirectory, ParentProcessId: null, PtcEnabled: ptc);

    public static FactoryHostClient HostClient(FakeHttpMessageHandler handler, string dataDirectory = "data") =>
        new(new HttpClient(handler), Create(dataDirectory));
}

/// <summary>
/// A stand-in for the factory host, listening on a real loopback port: it records submissions
/// and the ready call, and answers the model gateway from a script of NDJSON responses.
/// </summary>
public sealed class FakeFactoryHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<GatewayEvent[]> _gatewayScript = new();

    public ConcurrentQueue<SubmissionRequest> Submissions { get; } = new();

    public ConcurrentQueue<GatewayRequest> GatewayRequests { get; } = new();

    public ConcurrentQueue<WorkerReady> ReadyCalls { get; } = new();

    public ConcurrentQueue<string> SecretsSeen { get; } = new();

    /// <summary>What the host answers to a submission; accept by default.</summary>
    public Func<SubmissionRequest, SubmissionResponse> OnSubmission { get; set; } = _ => new SubmissionResponse(true, "");

    public Uri Url { get; private set; } = null!;

    private FakeFactoryHost(WebApplication app) => _app = app;

    public static async Task<FakeFactoryHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var host = new FakeFactoryHost(app);

        app.Use(async (context, next) =>
        {
            host.SecretsSeen.Enqueue(context.Request.Headers[FactoryWire.SecretHeader].ToString());
            await next(context);
        });

        app.MapPost("/internal/runs/{runId}/submissions", async (HttpRequest request) =>
        {
            var submission = (await request.ReadFromJsonAsync<SubmissionRequest>(FactoryWire.Json))!;
            host.Submissions.Enqueue(submission);
            return Results.Json(host.OnSubmission(submission), FactoryWire.Json);
        });

        app.MapPost("/internal/runs/{runId}/ready", async (HttpRequest request) =>
        {
            host.ReadyCalls.Enqueue((await request.ReadFromJsonAsync<WorkerReady>(FactoryWire.Json))!);
            return Results.Ok();
        });

        app.MapPost("/internal/runs/{runId}/gateway", async (HttpContext context) =>
        {
            host.GatewayRequests.Enqueue((await context.Request.ReadFromJsonAsync<GatewayRequest>(FactoryWire.Json))!);
            context.Response.ContentType = "application/x-ndjson";
            var events = host._gatewayScript.TryDequeue(out var scripted)
                ? scripted
                : Reply("ok");
            foreach (var evt in events)
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(evt, FactoryWire.Json) + "\n");
                await context.Response.Body.FlushAsync();
            }
        });

        await app.StartAsync();
        host.Url = new Uri(app.Urls.First().TrimEnd('/') + "/");
        return host;
    }

    public void EnqueueGateway(params GatewayEvent[] events) => _gatewayScript.Enqueue(events);

    /// <summary>A plain text reply that ends the turn.</summary>
    public static GatewayEvent[] Reply(string text) =>
        [new GatewayTextDelta(text), new GatewayMessageCompleted(ChatMessage.Assistant([new TextBlock(text)]), new UsageInfo(100, 10))];

    /// <summary>The model calling one tool.</summary>
    public static GatewayEvent[] ToolCall(string toolName, object arguments, string callId = "call-1")
    {
        var json = JsonSerializer.SerializeToElement(arguments);
        return
        [
            new GatewayToolCallStarted(callId, toolName),
            new GatewayToolCallCompleted(callId, toolName, json),
            new GatewayMessageCompleted(ChatMessage.Assistant([new ToolUseBlock(callId, toolName, json)]), new UsageInfo(100, 20)),
        ];
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
