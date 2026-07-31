using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Providers.OpenRouter;
using Litos.Providers.Tests.Fakes;

namespace Litos.Providers.Tests;

public class OpenRouterChatProviderTests
{
    private static (OpenRouterChatProvider Provider, FakeHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        return (new OpenRouterChatProvider(httpClient), handler);
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
        var request = new ChatRequest([ChatMessage.User("hello")], [], "some-model");

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
    public async Task StreamAsync_MultipleToolResultBlocksInOneMessage_OnlyFirstIsUsed()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var message = new ChatMessage(Role.User,
        [
            new ToolResultBlock("call_1", "first result"),
            new ToolResultBlock("call_2", "second result"),
        ]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var serialized = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("first result", serialized.GetProperty("content").GetString());
        Assert.Equal("call_1", serialized.GetProperty("tool_call_id").GetString());
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
        Assert.Equal("describe this", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        var url = content[1].GetProperty("image_url").GetProperty("url").GetString();
        Assert.StartsWith("data:image/png;base64,", url);
    }

    [Fact]
    public async Task StreamAsync_AssistantMessageWithUnrecognizedBlockType_SilentlyDropsIt_NoThrow()
    {
        // Documents OpenRouter's asymmetry vs. the other three providers: the assistant
        // branch of ToOpenRouterMessage only pulls ToolUseBlock/TextBlock via OfType<>() —
        // a CompactionSummaryBlock on an assistant-role message vanishes with no error.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var message = ChatMessage.Assistant([new CompactionSummaryBlock("summary", 100)]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var serialized = json.RootElement.GetProperty("messages")[0];
        // DefaultIgnoreCondition.WhenWritingNull omits a null Content property entirely
        // rather than serializing "content":null.
        Assert.False(serialized.TryGetProperty("content", out _));
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
    public async Task StreamAsync_SnakeCaseNamingPolicy_AppliedToOutgoingJson()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model", MaxOutputTokens: 100);

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        Assert.Contains("\"max_tokens\":100", body);
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
        var sse = ": OPENROUTER PROCESSING\n\n" +
                   "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
                   "data: [DONE]\n\n";
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Contains(events, e => e is TextDelta { Text: "hi" });
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

    // ---- Rate limiting ----

    [Fact]
    public async Task StreamAsync_429Response_ThrowsRateLimitedException_WithRetryAfterFromHeader()
    {
        var (provider, handler) = CreateProvider();
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
        handler.Enqueue(response);
        var request = new ChatRequest([ChatMessage.User("hi")], [], "qwen/qwen3.7-flash");

        var ex = await Assert.ThrowsAsync<ChatProviderRateLimitedException>(
            () => DrainAsync(provider.StreamAsync(request, CancellationToken.None)));

        Assert.Contains("qwen/qwen3.7-flash", ex.Message);
        Assert.Contains("20s", ex.Message);
    }

    [Fact]
    public async Task StreamAsync_429Response_WithoutRetryAfterHeader_ThrowsRateLimitedException_WithGenericGuidance()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var ex = await Assert.ThrowsAsync<ChatProviderRateLimitedException>(
            () => DrainAsync(provider.StreamAsync(request, CancellationToken.None)));

        Assert.Contains("try again", ex.Message, StringComparison.OrdinalIgnoreCase);
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
}
