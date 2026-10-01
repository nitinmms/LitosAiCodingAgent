using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Kernel;
using Litos.Persistence;
using Litos.SoftwareFactory.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// The whole worker, in process: the real WorkerApp listening on a loopback port, the real agent
/// loop and tools, and a fake factory host on another loopback port answering the gateway from a
/// script. Only the model is scripted.
/// </summary>
public sealed class WorkerIntegrationTests : IAsyncLifetime
{
    private readonly TempDirectory _data = new();
    private FakeFactoryHost _host = null!;
    private WebApplication? _worker;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync() => _host = await FakeFactoryHost.StartAsync();

    public async Task DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync();
            await _worker.DisposeAsync();
        }

        await _host.DisposeAsync();
        _data.Dispose();
    }

    private async Task<WorkerOptions> StartWorkerAsync(bool ptc = false, int? parentProcessId = null)
    {
        var options = TestOptions.Create(_data.Path, _host.Url, ptc) with { ParentProcessId = parentProcessId };
        _worker = WorkerApp.Build(options, []);
        _port = await WorkerApp.StartAsync(_worker);
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        _client.DefaultRequestHeaders.Add(FactoryWire.SecretHeader, TestOptions.Secret);
        return options;
    }

    /// <summary>Starts a turn and reads its event stream to the end, as the host does.</summary>
    private async Task<List<JsonElement>> RunTurnAsync(string sessionId, string input, string? turnKind)
    {
        using var response = await _client.PostAsJsonAsync($"sessions/{sessionId}/turns", new { input, turnKind });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return [.. body.Split('\n')
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line[5..]).RootElement.Clone())];
    }

    private static string? ErrorIn(IEnumerable<JsonElement> events) => events
        .Where(e => e.TryGetProperty("Exception", out _))
        .Select(e => e.GetProperty("Exception").GetProperty("Message").GetString())
        .FirstOrDefault();

    private static string[] ToolNames(GatewayRequest request) => [.. request.ChatRequest.Tools.Select(t => t.Name)];

    // ---- Startup ----

    [Fact]
    public async Task Start_ReportsReadyToTheHost_WithItsPort_UsingTheSecret()
    {
        await StartWorkerAsync();

        var ready = Assert.Single(_host.ReadyCalls);
        Assert.Equal(_port, ready.Port);
        Assert.True(ready.McpReady);
        Assert.All(_host.SecretsSeen, secret => Assert.Equal(TestOptions.Secret, secret));
    }

    // ---- Secret and Host checks ----

    [Fact]
    public async Task Request_WithoutTheSecret_Is401_AndStartsNothing()
    {
        await StartWorkerAsync();
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };

        using var response = await anonymous.PostAsJsonAsync("sessions/s/turns", new { input = "run rm -rf", turnKind = "Implement" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_host.GatewayRequests);
    }

    [Fact]
    public async Task Request_WithTheWrongSecret_Is401()
    {
        await StartWorkerAsync();
        using var wrong = new HttpClient { BaseAddress = _client.BaseAddress };
        wrong.DefaultRequestHeaders.Add(FactoryWire.SecretHeader, TestOptions.Secret + "x");

        using var response = await wrong.PostAsync("shutdown", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithTheSecret_IsLetThrough()
    {
        await StartWorkerAsync();

        using var response = await _client.PostAsync("sessions/no-such-session/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // reached the endpoint: nothing to cancel
    }

    /// <summary>Even with the right secret, a request addressed to a name that is not loopback
    /// is refused: that is what a DNS-rebinding page in a local browser would send.</summary>
    [Fact]
    public async Task Request_AddressedToANonLoopbackHostName_Is403()
    {
        await StartWorkerAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/s/cancel");
        request.Headers.Host = "rebound.example";

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- A turn, end to end ----

    [Fact]
    public async Task ImplementTurn_CallsTheGateway_SubmitsWorkToTheHost_AndTellsTheModelItWasRecorded()
    {
        var options = await StartWorkerAsync();
        _host.EnqueueGateway(FakeFactoryHost.ToolCall("submit_work", new { summary = "Added CSV export.", testsAdded = new[] { "Export_Admin_Succeeds" } }));
        _host.EnqueueGateway(FakeFactoryHost.Reply("Submitted."));

        var events = await RunTurnAsync("thread-1", "Add CSV export for Orders.", "Implement");

        Assert.Null(ErrorIn(events));

        // The host has the typed submission, for the right session — taken from the callback.
        var submission = Assert.Single(_host.Submissions);
        Assert.Equal("thread-1", submission.SessionId);
        var work = Assert.IsType<WorkSubmission>(submission.Submission);
        Assert.Equal("Added CSV export.", work.Summary);
        Assert.Equal(["Export_Admin_Succeeds"], work.TestsAdded);

        // Both model calls went through the gateway, for the launch model, with the work tool set.
        var calls = _host.GatewayRequests.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.Equal("vendor/model", c.ChatRequest.Model));
        Assert.Equal(
            ["read_file", "write_file", "edit_file", "list_directory", "search_code", "shell", "submit_work", "request_decision"],
            ToolNames(calls[0]));
        Assert.NotEqual(calls[0].RequestKey, calls[1].RequestKey);

        // The second call carries the tool result the model reads.
        var toolResult = calls[1].ChatRequest.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.Equal("Recorded.", toolResult.Text);
        Assert.False(toolResult.IsError);

        // The transcript is in the factory data directory, not ~/.litos/sessions.
        Assert.True(Directory.Exists(options.SessionsDirectory));
        Assert.NotEmpty(Directory.EnumerateFiles(options.SessionsDirectory, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReviewTurn_SeesOnlyReadOnlyToolsAndSubmitReview()
    {
        await StartWorkerAsync();
        _host.EnqueueGateway(FakeFactoryHost.ToolCall("submit_review", new { findings = Array.Empty<object>() }));
        _host.EnqueueGateway(FakeFactoryHost.Reply("Clean."));

        await RunTurnAsync("review-1", "Review the change.", "Review");

        Assert.Equal(["read_file", "list_directory", "search_code", "submit_review"], ToolNames(_host.GatewayRequests.First()));
        Assert.IsType<ReviewSubmission>(Assert.Single(_host.Submissions).Submission);
    }

    /// <summary>A review turn that tries to edit gets "unknown tool", not an edit.</summary>
    [Fact]
    public async Task ReviewTurn_CannotCallAWritingTool_EvenIfTheModelAsksForOne()
    {
        await StartWorkerAsync();
        var target = Path.Combine(_data.Path, "should-not-exist.txt");
        _host.EnqueueGateway(FakeFactoryHost.ToolCall("write_file", new { path = target, content = "edited by a reviewer" }));
        _host.EnqueueGateway(FakeFactoryHost.Reply("Done."));

        await RunTurnAsync("review-1", "Review the change.", "Review");

        Assert.False(File.Exists(target));
        var toolResult = _host.GatewayRequests.Last().ChatRequest.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.True(toolResult.IsError);
    }

    [Fact]
    public async Task HostRefusesASubmission_TheModelSeesTheRefusalAsAToolError()
    {
        await StartWorkerAsync();
        _host.OnSubmission = _ => new SubmissionResponse(false, "This run is not expecting submit_work.");
        _host.EnqueueGateway(FakeFactoryHost.ToolCall("submit_work", new { summary = "done" }));
        _host.EnqueueGateway(FakeFactoryHost.Reply("Understood."));

        await RunTurnAsync("thread-1", "Go.", "Implement");

        var toolResult = _host.GatewayRequests.Last().ChatRequest.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.True(toolResult.IsError);
        Assert.Contains("This run is not expecting submit_work.", toolResult.Text);
    }

    [Fact]
    public async Task TurnWithNoTurnKind_ReportsAnError_AndCallsNoModel()
    {
        await StartWorkerAsync();

        var events = await RunTurnAsync("thread-1", "Go.", turnKind: null);

        Assert.Contains("Unknown turn kind", ErrorIn(events));
        Assert.Empty(_host.GatewayRequests);
    }

    /// <summary>§9.2: when admission is refused the turn ends with an error naming the refusal;
    /// nothing else is sent.</summary>
    [Fact]
    public async Task GatewayRefusesForBudget_TheTurnEndsWithThatError_AndMakesNoFurtherCalls()
    {
        await StartWorkerAsync();
        _host.EnqueueGateway(new GatewayError(GatewayErrorCodes.BudgetExhausted, "Needs 20,900 tokens; 20,000 remain."));

        var events = await RunTurnAsync("thread-1", "Go.", "Implement");

        Assert.Equal("Needs 20,900 tokens; 20,000 remain.", ErrorIn(events));
        Assert.Single(_host.GatewayRequests);
        Assert.Empty(_host.Submissions);
    }

    [Fact]
    public async Task SecondTurnInTheSameSession_CarriesTheFirstTurnsConversation()
    {
        await StartWorkerAsync();
        _host.EnqueueGateway(FakeFactoryHost.Reply("First answer."));
        _host.EnqueueGateway(FakeFactoryHost.Reply("Second answer."));

        await RunTurnAsync("thread-1", "First brief.", "Implement");
        await RunTurnAsync("thread-1", "Decision answered: all rows.", "Implement");

        var second = _host.GatewayRequests.Last().ChatRequest.Messages.SelectMany(m => m.Content).OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Equal(["First brief.", "First answer.", "Decision answered: all rows."], second);
    }

    // ---- PTC ----

    /// <summary>
    /// PTC on, with a real kernel subprocess: the model sees only run_kernel_code, and submit_work
    /// called from kernel code reaches the host exactly as a direct call does — even though that
    /// call never appears on the turn's event stream.
    /// </summary>
    [Fact]
    public async Task PtcOn_ModelSeesOnlyRunKernelCode_AndSubmitWorkFromKernelCodeReachesTheHost()
    {
        await StartWorkerAsync(ptc: true);
        Assert.True(_worker!.Services.GetRequiredService<Litos.Hosting.AgentWorker>().IsKernelModeAvailable, "The kernel host was not found beside the tests.");
        const string code = "await submit_work(\"{\\\"summary\\\":\\\"Submitted from kernel code.\\\"}\")";
        _host.EnqueueGateway(FakeFactoryHost.ToolCall("run_kernel_code", new { code }));
        _host.EnqueueGateway(FakeFactoryHost.Reply("Submitted."));

        var events = await RunTurnAsync("thread-ptc", "Add CSV export.", "Implement");

        Assert.Null(ErrorIn(events));
        Assert.Equal(["run_kernel_code"], ToolNames(_host.GatewayRequests.First()));

        var submission = Assert.Single(_host.Submissions);
        Assert.Equal("thread-ptc", submission.SessionId);
        Assert.Equal("Submitted from kernel code.", Assert.IsType<WorkSubmission>(submission.Submission).Summary);

        var kernelResult = _host.GatewayRequests.Last().ChatRequest.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.False(kernelResult.IsError, kernelResult.Text);
        Assert.Contains("Recorded.", kernelResult.Text);
    }

    [Fact]
    public async Task PtcOff_ModelSeesTheToolsDirectly()
    {
        await StartWorkerAsync(ptc: false);
        _host.EnqueueGateway(FakeFactoryHost.Reply("ok"));

        await RunTurnAsync("thread-1", "Go.", "Implement");

        Assert.DoesNotContain("run_kernel_code", ToolNames(_host.GatewayRequests.Single()));
    }

    // ---- Compaction ----

    private async Task SeedLongSessionAsync(WorkerOptions options, string sessionId)
    {
        var store = new JsonlTranscriptStore(options.SessionsDirectory);
        await store.AppendAsync(SessionOwner.Local, sessionId, TranscriptEntry.FromMessage(ChatMessage.User(new string('a', 100_000))), default);
        await store.AppendAsync(SessionOwner.Local, sessionId, TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))])), default);
        await store.AppendAsync(SessionOwner.Local, sessionId, TranscriptEntry.FromMessage(ChatMessage.User("recent question")), default);
    }

    [Fact]
    public async Task Compact_SummarizesThroughTheGateway_WithTheHostsInstruction_AndPersistsTheSummary()
    {
        var options = await StartWorkerAsync();
        await SeedLongSessionAsync(options, "thread-1");
        _host.EnqueueGateway(FakeFactoryHost.Reply("## Goal\nAdd CSV export."));

        using var response = await _client.PostAsJsonAsync(
            "sessions/thread-1/compact", new CompactRequest("Keep every acceptance criterion, word for word."), FactoryWire.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<CompactResponse>(FactoryWire.Json))!.Compacted);

        var prompt = _host.GatewayRequests.Single().ChatRequest.Messages[^1].Content.OfType<TextBlock>().Single().Text;
        Assert.Contains("Keep every acceptance criterion, word for word.", prompt);

        var reloaded = await Transcript.LoadAsync(new JsonlTranscriptStore(options.SessionsDirectory), SessionOwner.Local, "thread-1", default);
        Assert.Contains("Add CSV export.", Assert.IsType<CompactionSummaryBlock>(reloaded.Messages[0].Content[0]).Summary);
    }

    [Fact]
    public async Task Compact_NothingOldEnoughToCut_ReportsNotCompacted_AndCallsNoModel()
    {
        await StartWorkerAsync();

        using var response = await _client.PostAsJsonAsync("sessions/empty/compact", new CompactRequest("instruction"), FactoryWire.Json);

        Assert.False((await response.Content.ReadFromJsonAsync<CompactResponse>(FactoryWire.Json))!.Compacted);
        Assert.Empty(_host.GatewayRequests);
    }

    /// <summary>Compaction is charged to the task like any call; when it is refused the host is
    /// told, and the session's history is left as it was.</summary>
    [Fact]
    public async Task Compact_RefusedByTheGateway_Is502_AndLeavesTheSessionUntouched()
    {
        var options = await StartWorkerAsync();
        await SeedLongSessionAsync(options, "thread-1");
        _host.EnqueueGateway(new GatewayError(GatewayErrorCodes.BudgetExhausted, "No allowance left for compaction."));

        using var response = await _client.PostAsJsonAsync("sessions/thread-1/compact", new CompactRequest("instruction"), FactoryWire.Json);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("No allowance left for compaction.", await response.Content.ReadAsStringAsync());
        var reloaded = await Transcript.LoadAsync(new JsonlTranscriptStore(options.SessionsDirectory), SessionOwner.Local, "thread-1", default);
        Assert.Equal(3, reloaded.Messages.Count);
        Assert.DoesNotContain(reloaded.Messages, m => m.Content.OfType<CompactionSummaryBlock>().Any());
    }

    // ---- Shutdown and the parent watcher ----

    [Fact]
    public async Task Shutdown_StopsTheWorker()
    {
        await StartWorkerAsync();

        using var response = await _client.PostAsync("shutdown", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _worker!.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>§13.2: the worker exits when the host that started it is gone.</summary>
    [Fact]
    public async Task ParentProcessGone_TheWorkerStopsItself()
    {
        using var shortLived = Process.Start(new ProcessStartInfo(
            OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        await shortLived.WaitForExitAsync();

        await StartWorkerAsync(parentProcessId: shortLived.Id);

        await _worker!.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ParentProcessAlive_TheWorkerKeepsRunning()
    {
        await StartWorkerAsync(parentProcessId: Environment.ProcessId);

        await Task.Delay(TimeSpan.FromSeconds(3)); // longer than the watcher's poll interval

        using var response = await _client.PostAsync("sessions/s/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void ParentProcessWatcher_IsRunning()
    {
        Assert.True(ParentProcessWatcher.IsRunning(Environment.ProcessId));
        Assert.False(ParentProcessWatcher.IsRunning(int.MaxValue - 11));
    }

    // ---- What the worker is, and is not, given ----

    [Fact]
    public async Task Worker_UsesAFixedModel_TheFactoryToolPolicy_AndTheProcessDirectory()
    {
        await StartWorkerAsync();
        var services = _worker!.Services;

        var selection = services.GetRequiredService<Litos.Hosting.IModelSelection>();
        Assert.IsType<Litos.Hosting.FixedModelSelection>(selection);
        Assert.Equal(new Litos.Hosting.ModelSelectionSnapshot("openrouter", "vendor/model", 200_000), selection.Snapshot());
        Assert.IsType<FactoryToolSetPolicy>(services.GetRequiredService<Litos.Hosting.IToolSetPolicy>());
        Assert.IsType<Litos.Hosting.ProcessWorkingDirectoryResolver>(services.GetRequiredService<Litos.Hosting.IWorkingDirectoryResolver>());
        Assert.IsType<GatewayChatProvider>(services.GetRequiredService<Litos.Agent.Providers.IChatProviderFactory>().Resolve("anthropic"));
    }

    /// <summary>Acceptance scenario 14, from the worker's side: it is configured with no provider keys.</summary>
    [Fact]
    public async Task Worker_HoldsNoProviderKeys()
    {
        await StartWorkerAsync();

        var config = _worker!.Services.GetRequiredService<Litos.Host.LitosConfig>();

        Assert.Empty(config.ApiKeys);
    }
}

/// <summary>
/// The completion tools through a real kernel subprocess, without the rest of the worker: the
/// bridge resolves them from the policy's per-session registry on every call.
/// </summary>
public sealed class KernelBridgedCompletionToolTests : IDisposable
{
    private readonly TempDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private sealed class NamedTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => name;
        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok(name));
    }

    private static readonly string[] BuiltIn = ["read_file", "write_file", "edit_file", "list_directory", "search_code", "shell"];

    private (KernelSession Session, FakeHttpMessageHandler Handler) Create(string sessionId, string turnKind)
    {
        var handler = new FakeHttpMessageHandler();
        var policy = new FactoryToolSetPolicy(BuiltIn.Select(n => new NamedTool(n)), TestOptions.HostClient(handler));
        policy.Create(sessionId, turnKind);
        var session = new KernelSession(
            sessionId, Path.GetTempPath(), _scratch.Path, () => policy.CreateForBridge(sessionId), mcpToolProvider: null,
            hardTimeout: TimeSpan.FromMinutes(2));
        return (session, handler);
    }

    [Fact]
    public async Task RequestDecision_FromKernelCode_PostsToTheHost_AndReturnsTheStopInstruction()
    {
        var (session, handler) = Create("thread-9", "Implement");
        await using var _ = session;
        handler.EnqueueJson(new SubmissionResponse(true, ""));
        const string arguments = "{\\\"question\\\":\\\"All rows or the current page?\\\",\\\"whyItBlocks\\\":\\\"The request does not say.\\\",\\\"options\\\":[\\\"All rows\\\",\\\"Current page\\\"]}";

        var result = await session.RunAsync($"await request_decision(\"{arguments}\")", default);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Recorded. Stop now and wait for the answer", result.Text);
        var posted = JsonSerializer.Deserialize<SubmissionRequest>(Assert.Single(handler.Requests).Body!, FactoryWire.Json)!;
        Assert.Equal("thread-9", posted.SessionId);
        Assert.Equal("All rows or the current page?", Assert.IsType<DecisionSubmission>(posted.Submission).Question);
    }

    [Fact]
    public async Task SubmitReview_FromKernelCode_InAReviewSession_PostsTheFindings()
    {
        var (session, handler) = Create("review-9", "Review");
        await using var _ = session;
        handler.EnqueueJson(new SubmissionResponse(true, ""));
        const string arguments = "{\\\"findings\\\":[{\\\"severity\\\":\\\"blocking\\\",\\\"file\\\":\\\"src/Orders.cs\\\",\\\"line\\\":42,\\\"text\\\":\\\"Crashes on an empty list.\\\"}]}";

        var result = await session.RunAsync($"await submit_review(\"{arguments}\")", default);

        Assert.False(result.IsError, result.Text);
        var posted = JsonSerializer.Deserialize<SubmissionRequest>(Assert.Single(handler.Requests).Body!, FactoryWire.Json)!;
        Assert.Equal(
            new ReviewFinding(FindingSeverity.Blocking, "src/Orders.cs", 42, "Crashes on an empty list."),
            Assert.Single(Assert.IsType<ReviewSubmission>(posted.Submission).Findings));
    }

    [Fact]
    public async Task BadArguments_FromKernelCode_SurfaceAsAnErrorTheModelCanCorrect_AndNothingIsPosted()
    {
        var (session, handler) = Create("thread-9", "Implement");
        await using var _ = session;

        var result = await session.RunAsync("await submit_work(\"{}\")", default);

        Assert.True(result.IsError);
        Assert.Contains("'summary' is required", result.Text);
        Assert.Empty(handler.Requests);
    }
}
