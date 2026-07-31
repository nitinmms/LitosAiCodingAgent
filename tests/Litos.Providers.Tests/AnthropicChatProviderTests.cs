using System.Text.Json;
using global::Anthropic.SDK;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Providers.Anthropic;
using Litos.Providers.Tests.Fakes;

namespace Litos.Providers.Tests;

public class AnthropicChatProviderTests
{
    // Empirically verified minimal SSE body against Anthropic.SDK 5.10.0's hand-rolled
    // parser (Anthropic.SDK.EndpointBase.HttpStreamingRequestMessages): only
    // content_block_delta is required to produce one text delta and complete cleanly.
    // The blank line after each "data:" line is mandatory — it's what triggers dispatch.
    private const string MinimalTextSse =
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\n";

    private static (AnthropicChatProvider Provider, FakeHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") };
        var client = new AnthropicClient(new APIAuthentication("fake-key"), httpClient);
        // Empty catalog (no queued response) makes OpenRouterModelCatalog.ResolveAsync fail and
        // return null, so ListModelsAsync falls back to ModelContextWindows' static table —
        // deterministic without a second FakeHttpMessageHandler queue to manage per test.
        var contextCatalog = new OpenRouterModelCatalog(new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") });
        return (new AnthropicChatProvider(client, contextCatalog), handler);
    }

    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (var evt in events)
            list.Add(evt);
        return list;
    }

    [Fact]
    public async Task StreamAsync_TextOnlyUserMessage_SerializesAsTextContent()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var request = new ChatRequest([ChatMessage.User("hello")], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var message = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        var content = message.GetProperty("content")[0];
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal("hello", content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task StreamAsync_ImageBlock_SerializesAsBase64ImageContent()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var message = ChatMessage.User([new ImageBlock("image/png", [1, 2, 3])]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("image", content.GetProperty("type").GetString());
        var source = content.GetProperty("source");
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal("AQID", source.GetProperty("data").GetString());
    }

    [Fact]
    public async Task StreamAsync_ToolResultBlock_SerializesWithToolUseIdAndIsError_WrappedInNestedTextContent()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var message = ChatMessage.ToolResult("call_1", ToolResult.Error("file not found"));
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("tool_result", content.GetProperty("type").GetString());
        Assert.Equal("call_1", content.GetProperty("tool_use_id").GetString());
        Assert.True(content.GetProperty("is_error").GetBoolean());
        // Result text is wrapped in a nested single-element text-content array.
        var nestedText = content.GetProperty("content")[0];
        Assert.Equal("file not found", nestedText.GetProperty("text").GetString());
    }

    [Fact]
    public async Task StreamAsync_ToolUseBlock_SerializesWithIdNameAndInput()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var toolArgs = JsonDocument.Parse("""{"path":"foo.cs"}""").RootElement;
        var message = ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", toolArgs)]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("tool_use", content.GetProperty("type").GetString());
        Assert.Equal("call_1", content.GetProperty("id").GetString());
        Assert.Equal("read_file", content.GetProperty("name").GetString());
        Assert.Equal("foo.cs", content.GetProperty("input").GetProperty("path").GetString());
    }

    [Fact]
    public async Task StreamAsync_CompactionSummaryBlock_SerializesAsTextContent_DropsTokensBefore()
    {
        // Unlike OpenAI/OpenRouter, Anthropic's mapping drops TokensBefore entirely —
        // CompactionSummaryBlock maps to a plain TextContent{Text=c.Summary} with no
        // embedded token count.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var message = ChatMessage.CompactionSummary("summary text", 5000);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("summary text", content.GetProperty("text").GetString());
        Assert.DoesNotContain("5000", body);
    }

    [Fact]
    public async Task StreamAsync_AssistantRole_MapsToAssistantRoleType()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var request = new ChatRequest([ChatMessage.Assistant([new TextBlock("prior reply")])], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        Assert.Equal("assistant", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task StreamAsync_SystemPrompt_SentAsSystemArray()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model", SystemPrompt: "be concise");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var system = json.RootElement.GetProperty("system")[0];
        Assert.Equal("be concise", system.GetProperty("text").GetString());
    }

    [Fact]
    public async Task StreamAsync_ToolSchema_SerializesWithNameDescriptionAndSchema()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("hi")], [schema], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var tool = json.RootElement.GetProperty("tools")[0];
        Assert.Equal("read_file", tool.GetProperty("name").GetString());
        Assert.Equal("Reads a file.", tool.GetProperty("description").GetString());
    }

    [Fact]
    public async Task StreamAsync_NoTools_ToolsFieldOmitted()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("tools", out _));
    }

    // ---- Response parsing ----

    [Fact]
    public async Task StreamAsync_ParsesTextDelta_FromSse()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalTextSse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Contains(events, e => e is TextDelta { Text: "hello" });
        var completed = Assert.Single(events.OfType<MessageCompleted>());
        Assert.Equal(Role.Assistant, completed.Message.Role);
    }

    [Fact]
    public async Task StreamAsync_FullEventSequence_YieldsMessageCompleted_WithUsage()
    {
        var sse =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-3-5-sonnet\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var completed = Assert.Single(events.OfType<MessageCompleted>());
        var textBlock = Assert.IsType<TextBlock>(Assert.Single(completed.Message.Content));
        Assert.Equal("hello", textBlock.Text);
    }

    [Fact]
    public async Task StreamAsync_ToolUseInResponse_YieldsToolCallCompleted()
    {
        var sse =
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"read_file\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"foo.cs\\\"}\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n";
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var completed = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.Equal("call_1", completed.CallId);
        Assert.Equal("read_file", completed.ToolName);
        Assert.Equal("foo.cs", completed.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task StreamAsync_ErrorEvent_Throws()
    {
        var sse =
            "event: error\n" +
            "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(sse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        await Assert.ThrowsAnyAsync<Exception>(() => DrainAsync(provider.StreamAsync(request, CancellationToken.None)));
    }

    [Fact]
    public async Task ListModelsAsync_MapsIdAndDisplayName()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {
              "data": [
                { "id": "claude-3-5-sonnet-20241022", "display_name": "Claude 3.5 Sonnet", "type": "model", "created_at": "2024-10-22T00:00:00Z" },
                { "id": "claude-3-opus-20240229", "display_name": "Claude 3 Opus", "type": "model", "created_at": "2024-02-29T00:00:00Z" }
              ],
              "has_more": false,
              "first_id": "claude-3-5-sonnet-20241022",
              "last_id": "claude-3-opus-20240229"
            }
            """));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["claude-3-5-sonnet-20241022", "claude-3-opus-20240229"], models.Select(m => m.Id));
        Assert.Equal(["Claude 3.5 Sonnet", "Claude 3 Opus"], models.Select(m => m.DisplayName));
        Assert.All(models, m => Assert.False(m.IsDefault));
    }
}
