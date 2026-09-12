using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Providers.Local;
using Litos.Providers.Tests.Fakes;

namespace Litos.Providers.Tests;

public class LocalChatProviderTests
{
    private static (LocalChatProvider Provider, FakeHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/v1/") };
        return (new LocalChatProvider(httpClient), handler);
    }

    private const string MinimalSseCompletion =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n";

    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (var evt in events)
            list.Add(evt);
        return list;
    }

    // ---- Message mapping via the outgoing request body ----

    [Fact]
    public async Task StreamAsync_TextOnlyUserMessage_SerializesAsPlainStringContent()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var request = new ChatRequest([ChatMessage.User("hello")], [], "local-model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var message = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("hello", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamAsync_AssistantMessageWithToolUse_SerializesAsToolCalls()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var toolUseArgs = JsonDocument.Parse("""{"path":"foo.cs"}""").RootElement;
        var assistantMessage = ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", toolUseArgs)]);
        var request = new ChatRequest([assistantMessage], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var message = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        var toolCall = message.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", toolCall.GetProperty("id").GetString());
        Assert.Equal("read_file", toolCall.GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task StreamAsync_ToolResultMessage_SerializesWithToolRole_AndToolCallId()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var toolResultMessage = ChatMessage.ToolResult("call_1", ToolResult.Ok("file contents"));
        var request = new ChatRequest([toolResultMessage], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var message = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("tool", message.GetProperty("role").GetString());
        Assert.Equal("file contents", message.GetProperty("content").GetString());
        Assert.Equal("call_1", message.GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task StreamAsync_UserMessageWithImage_SerializesAsMultiPartContent_WithDataUri()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var message = ChatMessage.User([new TextBlock("describe this"), new ImageBlock("image/png", [1, 2, 3])]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        var url = content[1].GetProperty("image_url").GetProperty("url").GetString();
        Assert.StartsWith("data:image/png;base64,", url);
    }

    [Fact]
    public async Task StreamAsync_SystemPrompt_PrependedAsSystemRoleMessage()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model", SystemPrompt: "be concise");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var first = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("system", first.GetProperty("role").GetString());
        Assert.Equal("be concise", first.GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamAsync_ToolSchema_SerializesAsFunctionTool()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("hi")], [schema], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var tool = json.RootElement.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task StreamAsync_NoApiKeyConfigured_SendsRequestWithNoAuthorizationHeader()
    {
        // The whole point of "local" is that most servers (LM Studio's default config
        // included) need no key at all — LitosHostBuilder only sets an Authorization header
        // when a key is configured, so a provider built without one must still work.
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/v1/") };
        var provider = new LocalChatProvider(httpClient);
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    // ---- Streamed response parsing ----

    [Fact]
    public async Task StreamAsync_ParsesSseDeltas_IntoTextDeltaEvents()
    {
        var (provider, handler) = CreateProvider();
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"he\"}}]}\n\n" +
                   "data: {\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}\n\n" +
                   "data: [DONE]\n\n";
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var textDeltas = events.OfType<TextDelta>().Select(t => t.Text);
        Assert.Equal(["he", "llo"], textDeltas);
    }

    [Fact]
    public async Task StreamAsync_IgnoresNonJsonSseCommentLines()
    {
        var (provider, handler) = CreateProvider();
        var sse = ": keep-alive\n\n" +
                   "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
                   "data: [DONE]\n\n";
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Contains(events, e => e is TextDelta { Text: "hi" });
    }

    [Fact]
    public async Task StreamAsync_EveryLine_YieldsAStreamHeartbeat_EvenNonContentOnes()
    {
        // AgentLoop's idle-gap timeout only resets on a yielded AgentEvent, not on raw network
        // activity — a "thinking" local model that goes quiet on real content for a long stretch
        // (very much alive, just not done reasoning) needs SOME event to prove the connection is
        // still live, or it looks identical to a genuinely stalled one. A keep-alive comment line
        // is exactly the kind of activity that must not be silently swallowed with nothing yielded.
        var (provider, handler) = CreateProvider();
        var sse = ": keep-alive\n\n" +
                   "data: {\"choices\":[{\"delta\":{}}]}\n\n" + // a delta with no usable content
                   "data: [DONE]\n\n";
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        // Not pinning an exact count — every raw line (including SSE blank-line framing and the
        // [DONE] line itself) yields one, so the exact total is incidental to this test's point:
        // a keep-alive comment and a content-free delta must not pass through with nothing
        // yielded at all, which is what left AgentLoop's idle timer blind to real activity.
        Assert.NotEmpty(events.OfType<StreamHeartbeat>());
        Assert.Empty(events.OfType<TextDelta>());
    }

    [Fact]
    public async Task StreamAsync_YieldsMessageCompleted_WithAccumulatedUsage()
    {
        var (provider, handler) = CreateProvider();
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n" +
                   "data: [DONE]\n\n";
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var completed = Assert.Single(events.OfType<MessageCompleted>());
        Assert.Equal(10, completed.Usage.InputTokens);
        Assert.Equal(5, completed.Usage.OutputTokens);
    }

    [Fact]
    public async Task StreamAsync_ReasoningContent_StreamedAsReasoningDelta_ButExcludedFromFinalMessage()
    {
        // Local "thinking" models (Qwen3, DeepSeek-R1, QwQ, ...) stream chain-of-thought under
        // a separate reasoning_content field, sometimes for a long stretch before Content ever
        // starts. It must reach the UI as its own event (so a UI can render it distinctly and
        // the turn doesn't look hung), but must not end up in the persisted assistant message
        // replayed on future turns, and must not be mistaken for the model's real reply.
        var (provider, handler) = CreateProvider();
        var sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hmm, \"}}]}\n\n" +
                   "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n" +
                   "data: [DONE]\n\n";
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Equal(["hmm, "], events.OfType<ReasoningDelta>().Select(r => r.Text));
        Assert.Equal(["answer"], events.OfType<TextDelta>().Select(t => t.Text));
        var completed = Assert.Single(events.OfType<MessageCompleted>());
        var text = Assert.IsType<TextBlock>(Assert.Single(completed.Message.Content));
        Assert.Equal("answer", text.Text);
    }

    [Fact]
    public async Task StreamAsync_MalformedToolCallArgumentsJson_FallsBackToEmptyObject_WithoutThrowing()
    {
        // A local model can emit truncated/broken argument JSON (e.g. cut off mid-stream by a
        // context-length limit, or just a weaker model producing invalid JSON). Before this fix,
        // JsonDocument.Parse threw uncaught here, which propagated out of the async-iterator body,
        // was caught generically by AgentLoop as a streamError, and ended the whole turn instead
        // of the tool call simply failing like any other bad tool call does.
        var (provider, handler) = CreateProvider();
        var sse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\"}}]}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"path\\\": \\\"foo.cs\\\"\"}}]}}]}\n\n" +
                  "data: [DONE]\n\n";
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var toolCallCompleted = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.Equal(JsonValueKind.Object, toolCallCompleted.Arguments.ValueKind);
        Assert.False(toolCallCompleted.Arguments.EnumerateObject().Any());

        var completed = Assert.Single(events.OfType<MessageCompleted>());
        var toolUse = Assert.IsType<ToolUseBlock>(Assert.Single(completed.Message.Content));
        Assert.Equal(JsonValueKind.Object, toolUse.Arguments.ValueKind);
        Assert.False(toolUse.Arguments.EnumerateObject().Any());
    }

    [Fact]
    public async Task StreamAsync_NoUsageInChunk_DefaultsToZero()
    {
        // Many local servers (LM Studio, older Ollama builds) don't emit an OpenAI-style
        // `usage` object on stream chunks at all — must degrade to 0/0, not throw.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var completed = Assert.Single(events.OfType<MessageCompleted>());
        Assert.Equal(0, completed.Usage.InputTokens);
        Assert.Equal(0, completed.Usage.OutputTokens);
    }

    // ---- Rate limiting ----

    [Fact]
    public async Task StreamAsync_429Response_ThrowsRateLimitedException_WithRetryAfterFromHeader()
    {
        var (provider, handler) = CreateProvider();
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
        handler.Enqueue(response);
        var request = new ChatRequest([ChatMessage.User("hi")], [], "local-model");

        var ex = await Assert.ThrowsAsync<ChatProviderRateLimitedException>(
            () => DrainAsync(provider.StreamAsync(request, CancellationToken.None)));

        Assert.Contains("local-model", ex.Message);
        Assert.Contains("20s", ex.Message);
    }

    [Fact]
    public async Task StreamAsync_OtherNon2xxResponse_ThrowsHttpRequestException_NotRateLimited()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => DrainAsync(provider.StreamAsync(request, CancellationToken.None)));
    }

    // ---- Model listing ----

    [Fact]
    public async Task ListModelsAsync_MapsIdAndName_FallsBackToIdWhenNameMissing()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"data":[{"id":"model-a","name":"Model A"},{"id":"model-b","name":null}]}
            """));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(2, models.Count);
        Assert.Equal("Model A", models[0].DisplayName);
        Assert.Equal("model-b", models[1].DisplayName);
        Assert.All(models, m => Assert.False(m.IsDefault));
    }

    [Fact]
    public async Task ListModelsAsync_NoContextLengthInResponse_NonLmStudioServer_FallsBackToConservativeDefault()
    {
        // Unlike OpenRouter's catalog, a local server's /v1/models response typically has no
        // context_length field at all (LM Studio's plain OpenAI-compatible endpoint included).
        // Leaving this null let the context meter/compaction silently no-op for local sessions —
        // must map to a conservative non-null fallback instead so both stay functional.
        // The second enqueued response is the LM Studio vendor-catalog probe 404ing, exactly as
        // vLLM/llama.cpp/Ollama/LocalAI (none of which expose /api/v1/models) would respond —
        // proving the fallback still applies for real non-LM-Studio servers, not just because
        // nothing was enqueued for that request.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"data":[{"id":"llama-3-8b"}]}
            """));
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(ModelContextWindows.LocalFallbackContextLength, Assert.Single(models).ContextLength);
    }

    [Fact]
    public async Task ListModelsAsync_ContextLengthInResponse_UsesReportedValue_NotFallback()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"data":[{"id":"qwen3","context_length":64000}]}
            """));
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(64_000, Assert.Single(models).ContextLength);
    }

    [Fact]
    public async Task ListModelsAsync_LmStudioVendorCatalogReportsLoadedContextLength_UsesLoadedValue()
    {
        // LM Studio's plain OpenAI-compatible /models omits context_length (as in the test above),
        // but its native /api/v1/models does report it — this proves that value gets used instead
        // of falling all the way through to the static conservative guess.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"data":[{"id":"google/gemma-4-26b-a4b-qat"}]}
            """));
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"models":[{"key":"google/gemma-4-26b-a4b-qat","max_context_length":262144,"loaded_instances":[{"config":{"context_length":22016}}]}]}
            """));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(22_016, Assert.Single(models).ContextLength);
    }

    [Fact]
    public async Task ListModelsAsync_LmStudioVendorCatalogModelNotLoaded_FallsBackToMaxContextLength()
    {
        // A model reported by /api/v1/models with no loaded_instances entry (not currently
        // loaded) has no "currently accepted" context length yet, so max_context_length is the
        // best available signal rather than the static conservative guess.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"data":[{"id":"qwen3.8-27b-mlx-textonly"}]}
            """));
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"models":[{"key":"qwen3.8-27b-mlx-textonly","max_context_length":262144,"loaded_instances":[]}]}
            """));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(262_144, Assert.Single(models).ContextLength);
    }

    [Fact]
    public async Task ListModelsAsync_LmStudioVendorCatalogUnreachable_FallsBackToConservativeDefault()
    {
        // Simulates a connection failure (server down, network error) rather than a clean 404 —
        // TryGetLmStudioContextLengthsAsync's catch block must swallow this too, same as the
        // 404 case, so a flaky/unreachable local server never breaks model listing outright.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"data":[{"id":"llama-3-8b"}]}
            """));
        handler.EnqueueConnectionFailure();

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(ModelContextWindows.LocalFallbackContextLength, Assert.Single(models).ContextLength);
    }

    [Fact]
    public async Task ListModelsAsync_QueriesLmStudioVendorCatalog_AsSiblingOfBaseAddress()
    {
        // The vendor endpoint lives off the server root (.../api/v1/models), not nested under the
        // OpenAI-compatible /v1/ this provider otherwise talks to exclusively — this pins that
        // URI resolution so a future BaseAddress/relative-path change can't silently point it
        // somewhere that happens to still compile but never reaches a real LM Studio server.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""{"data":[]}"""));
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.Equal(new Uri("http://localhost:1234/api/v1/models"), handler.CapturedRequests[1].Uri);
    }

    // ---- stream_options ----

    [Fact]
    public async Task StreamAsync_Request_IncludesStreamOptionsIncludeUsage()
    {
        // Most local OpenAI-compatible servers only emit a `usage` object on stream chunks when
        // this is explicitly requested — omitting it is what left real token usage at 0/0 for
        // every local turn (see ContextUsage/Compaction).
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var streamOptions = json.RootElement.GetProperty("stream_options");
        Assert.True(streamOptions.GetProperty("include_usage").GetBoolean());
    }
}
