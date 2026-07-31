using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Providers.OpenAI;
using Litos.Providers.Tests.Fakes;
using global::OpenAI;

namespace Litos.Providers.Tests;

#pragma warning disable OPENAI001

public class OpenAiChatProviderTests
{
    private const string MinimalTextResponse = """
        {
          "id": "resp_1", "object": "response", "created_at": 1, "status": "completed", "model": "m",
          "output": [
            { "type": "message", "id": "msg_1", "status": "completed", "role": "assistant",
              "content": [ { "type": "output_text", "text": "hello", "annotations": [] } ] }
          ],
          "usage": { "input_tokens": 10, "output_tokens": 5, "total_tokens": 15 },
          "parallel_tool_calls": true, "tool_choice": "auto", "tools": []
        }
        """;

    private const string MinimalToolCallResponse = """
        {
          "id": "resp_2", "object": "response", "created_at": 1, "status": "completed", "model": "m",
          "output": [
            { "type": "function_call", "id": "fc_1", "call_id": "call_1", "name": "read_file",
              "arguments": "{\"path\":\"foo.cs\"}", "status": "completed" }
          ],
          "usage": { "input_tokens": 20, "output_tokens": 8, "total_tokens": 28 },
          "parallel_tool_calls": true, "tool_choice": "auto", "tools": []
        }
        """;

    private static (OpenAiChatProvider Provider, FakeHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(httpClient) };
        var client = new OpenAIClient(new ApiKeyCredential("fake-key"), options);
        // Empty catalog (no queued response) makes OpenRouterModelCatalog.ResolveAsync fail and
        // return null, so ListModelsAsync falls back to ModelContextWindows' static table —
        // deterministic without a second FakeHttpMessageHandler queue to manage per test.
        var contextCatalog = new OpenRouterModelCatalog(new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") });
        return (new OpenAiChatProvider(client, contextCatalog), handler);
    }

    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (var evt in events)
            list.Add(evt);
        return list;
    }

    private static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement;

    // ---- Text-only mapping (no image) ----

    [Fact]
    public async Task StreamAsync_TextOnlyUserMessage_SerializesAsInputTextPart()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var request = new ChatRequest([ChatMessage.User("hello")], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var input = json.RootElement.GetProperty("input")[0];
        Assert.Equal("message", input.GetProperty("type").GetString());
        Assert.Equal("user", input.GetProperty("role").GetString());
        // Even the plain-string path wraps content as a single input_text content part
        // array, not a bare string — CreateUserMessageItem(string) does this internally.
        var contentPart = input.GetProperty("content")[0];
        Assert.Equal("input_text", contentPart.GetProperty("type").GetString());
        Assert.Equal("hello", contentPart.GetProperty("text").GetString());
    }

    [Fact]
    public async Task StreamAsync_ToolUseBlock_SerializesAsSeparateFunctionCallItem_NotWrappedInMessageText()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.Assistant([new TextBlock("thinking"), new ToolUseBlock("call_1", "read_file", EmptyArgs())]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var inputItems = json.RootElement.GetProperty("input").EnumerateArray().ToList();
        // Two separate items: the text-only assistant message, and the function_call item.
        Assert.Equal(2, inputItems.Count);
        Assert.Contains(inputItems, i => i.GetProperty("type").GetString() == "function_call" && i.GetProperty("call_id").GetString() == "call_1");
    }

    [Fact]
    public async Task StreamAsync_ToolResultBlock_SerializesAsFunctionCallOutputItem()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.ToolResult("call_1", ToolResult.Ok("file contents"));
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var item = json.RootElement.GetProperty("input")[0];
        Assert.Equal("function_call_output", item.GetProperty("type").GetString());
        Assert.Equal("call_1", item.GetProperty("call_id").GetString());
        Assert.Equal("file contents", item.GetProperty("output").GetString());
    }

    [Fact]
    public async Task StreamAsync_TextOnlyMessage_NoContent_YieldsNoInputItem()
    {
        // A message whose only block is e.g. a ToolUseBlock has textLines.Count == 0, so
        // no separate text-message item is added — just the function_call item.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.Assistant([new ToolUseBlock("call_1", "tool", EmptyArgs())]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var inputItems = json.RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Single(inputItems);
        Assert.Equal("function_call", inputItems[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task StreamAsync_CompactionSummaryBlock_EmbedsTokensBeforeInText()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.CompactionSummary("summary text", 5000);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        Assert.Contains("[compaction_summary tokens_before=5000] summary text", body);
    }

    [Fact]
    public async Task StreamAsync_AssistantRoleText_SerializesAsAssistantMessageItem()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var request = new ChatRequest([ChatMessage.Assistant([new TextBlock("prior reply")])], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var item = json.RootElement.GetProperty("input")[0];
        Assert.Equal("assistant", item.GetProperty("role").GetString());
    }

    // ---- Image mapping (the newest code, highest-priority coverage) ----

    [Fact]
    public async Task StreamAsync_MessageWithImage_SerializesAsInputImagePart_WithDataUri()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.User([new TextBlock("describe"), new ImageBlock("image/png", [1, 2, 3])]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var parts = json.RootElement.GetProperty("input")[0].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("input_text", parts[0].GetProperty("type").GetString());
        Assert.Equal("describe", parts[0].GetProperty("text").GetString());
        Assert.Equal("input_image", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task StreamAsync_AssistantMessageWithImage_StillSerializedAsUserMessageItem()
    {
        // Documents a real, possibly-surprising behavior pinned by a code comment in
        // OpenAiChatProvider: image content parts always go out as a "user" message item
        // regardless of the ChatMessage's own Role, because the Responses API only accepts
        // image content parts on user turns and assistants never legitimately produce images.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.Assistant([new ImageBlock("image/png", [9])]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var item = json.RootElement.GetProperty("input")[0];
        Assert.Equal("user", item.GetProperty("role").GetString());
    }

    [Fact]
    public async Task StreamAsync_MessageWithImageAndToolUse_ToolUseStillSeparateItem_NotAContentPart()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.Assistant([new ImageBlock("image/png", [1]), new ToolUseBlock("call_1", "tool", EmptyArgs())]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var inputItems = json.RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Contains(inputItems, i => i.GetProperty("type").GetString() == "function_call");
        Assert.Contains(inputItems, i => i.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task StreamAsync_CompactionSummaryWithImage_EmbedsTokensBeforeAsTextPart()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.User([new CompactionSummaryBlock("summary", 999), new ImageBlock("image/png", [1])]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        Assert.Contains("[compaction_summary tokens_before=999] summary", body);
    }

    // ---- Response parsing ----

    [Fact]
    public async Task StreamAsync_TextResponse_YieldsTextDeltaAndMessageCompleted()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Contains(events, e => e is TextDelta { Text: "hello" });
        var completed = Assert.Single(events.OfType<MessageCompleted>());
        Assert.Equal(10, completed.Usage.InputTokens);
        Assert.Equal(5, completed.Usage.OutputTokens);
    }

    [Fact]
    public async Task StreamAsync_ToolCallResponse_YieldsToolCallEvents_WithParsedArguments()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalToolCallResponse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var completed = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.Equal("call_1", completed.CallId);
        Assert.Equal("read_file", completed.ToolName);
        Assert.Equal("foo.cs", completed.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ListModelsAsync_FiltersToGptO1O3O4_ExcludesOtherModelFamilies()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {
              "object": "list",
              "data": [
                { "id": "gpt-5.4-mini", "object": "model", "created": 1, "owned_by": "openai" },
                { "id": "o3-mini", "object": "model", "created": 1, "owned_by": "openai" },
                { "id": "dall-e-3", "object": "model", "created": 1, "owned_by": "openai" },
                { "id": "text-embedding-3-large", "object": "model", "created": 1, "owned_by": "openai" },
                { "id": "whisper-1", "object": "model", "created": 1, "owned_by": "openai" }
              ]
            }
            """));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["gpt-5.4-mini", "o3-mini"], models.Select(m => m.Id));
        Assert.All(models, m => Assert.Equal(m.Id, m.DisplayName));
        Assert.All(models, m => Assert.False(m.IsDefault));
    }
}

#pragma warning restore OPENAI001
