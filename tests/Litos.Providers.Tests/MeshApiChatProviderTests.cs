using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Providers.MeshApi;
using Litos.Providers.Tests.Fakes;

namespace Litos.Providers.Tests;

public class MeshApiChatProviderTests
{
    private static (MeshApiChatProvider Provider, FakeHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.meshapi.ai/v1/") };
        return (new MeshApiChatProvider(httpClient), handler);
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
        var request = new ChatRequest([ChatMessage.User("hello")], [], "openai/gpt-4o-mini");

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
    public async Task StreamAsync_AssistantMessageWithUnrecognizedBlockType_SendsEmptyStringContent_NoThrow()
    {
        // Documents MeshAPI's asymmetry vs. the other native providers: the assistant branch of
        // ToMeshApiMessage only pulls ToolUseBlock/TextBlock via OfType<>() — a
        // CompactionSummaryBlock on an assistant-role message contributes no text and no tool
        // calls, same as OpenRouterChatProvider (this provider's own template). Regression test:
        // this used to serialize as an omitted/null content field, which some models MeshAPI
        // proxies to (e.g. glm-4.7-flash, via AWS Bedrock's Converse API) reject outright
        // ("The content field in the Message object ... is empty") — must send "" instead.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var message = ChatMessage.Assistant([new CompactionSummaryBlock("summary", 100)]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var serialized = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("", serialized.GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamAsync_AssistantMessageWithToolUseOnly_StillOmitsNullContent()
    {
        // The empty-string substitution above must not regress the tool_calls-only case: OpenAI-
        // compatible chat/completions expects null/omitted content alongside tool_calls, so this
        // must remain distinct from the neither-text-nor-tool-calls case.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var toolUseArgs = JsonDocument.Parse("""{"path":"foo.cs"}""").RootElement;
        var message = ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", toolUseArgs)]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var serialized = json.RootElement.GetProperty("messages")[0];
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
        // A tools-bearing request now tries /v1/responses first (see the /v1/responses routing
        // tests below), so this exercises the /v1/chat/completions tool-serialization shape via
        // the real fallback path: a first call against a fresh, uniquely-named model fails with
        // model_capability_not_supported, then the (asserted-on) second call falls back to
        // chat/completions.
        var (provider, handler) = CreateProvider();
        var errorBody = """{"error":{"code":"model_capability_not_supported","message":"nope"}}""";
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorBody, System.Text.Encoding.UTF8, "application/json"),
        });
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("hi")], [schema], $"model-no-tools-routing-{Guid.NewGuid()}");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Equal(2, handler.CapturedRequests.Count);
        var body = handler.CapturedRequests[1].Body!;
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
        var sse = ": MESH_API PROCESSING\n\n" +
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
        var request = new ChatRequest([ChatMessage.User("hi")], [], "anthropic/claude-haiku-4.5");

        var ex = await Assert.ThrowsAsync<ChatProviderRateLimitedException>(
            () => DrainAsync(provider.StreamAsync(request, CancellationToken.None)));

        Assert.Contains("anthropic/claude-haiku-4.5", ex.Message);
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

    // ---- /v1/responses routing (reasoning models + function tools) ----

    private const string MinimalResponsesCompletion =
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}],"usage":{"input_tokens":3,"output_tokens":1}}""";

    [Fact]
    public async Task StreamAsync_ToolsPresent_TriesResponsesApiFirst()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalResponsesCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("hi")], [schema], $"openai/gpt-5.6-luna-{Guid.NewGuid()}");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Single(handler.CapturedRequests);
        Assert.EndsWith("responses", handler.CapturedRequests[0].Uri!.AbsolutePath);
        Assert.Contains(events, e => e is TextDelta { Text: "hi" });
    }

    [Fact]
    public async Task StreamAsync_NoTools_NeverTriesResponsesApi()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var request = new ChatRequest([ChatMessage.User("hi")], [], $"openai/gpt-5.6-luna-{Guid.NewGuid()}");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Single(handler.CapturedRequests);
        Assert.EndsWith("chat/completions", handler.CapturedRequests[0].Uri!.AbsolutePath);
    }

    [Fact]
    public async Task StreamAsync_ResponsesApiParsesFunctionCall_YieldsToolCallEvents()
    {
        var (provider, handler) = CreateProvider();
        var body = """
            {"status":"completed","output":[{"type":"function_call","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"foo.cs\"}"}],"usage":{"input_tokens":5,"output_tokens":2}}
            """;
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(body));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("read foo.cs")], [schema], $"openai/gpt-5.6-luna-{Guid.NewGuid()}");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Contains(events, e => e is ToolCallStarted { CallId: "call_1", ToolName: "read_file" });
        Assert.Contains(events, e => e is ToolCallCompleted { CallId: "call_1", ToolName: "read_file" });
        var completed = Assert.Single(events.OfType<MessageCompleted>());
        Assert.Equal(5, completed.Usage.InputTokens);
        Assert.Equal(2, completed.Usage.OutputTokens);
    }

    [Fact]
    public async Task StreamAsync_ResponsesApiRequestBody_UsesInputNotMessages()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalResponsesCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("hello")], [schema], $"openai/gpt-5.6-luna-{Guid.NewGuid()}");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("input", out _));
        Assert.False(json.RootElement.TryGetProperty("messages", out _));
        var firstInput = json.RootElement.GetProperty("input")[0];
        Assert.Equal("user", firstInput.GetProperty("role").GetString());
        Assert.Equal("hello", firstInput.GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamAsync_ResponsesApiRequestBody_ToolsAreFlatShape_NotNestedUnderFunction()
    {
        // Regression test: the Responses API's function-tool shape is flat (type/name/description/
        // parameters as siblings), unlike /v1/chat/completions' nested {"function":{"name":...}}
        // shape. Sending the nested shape here previously failed Mesh's schema validation for
        // every tool (ResponsesFunctionTool.name "Field required"), which silently fell back to
        // chat/completions on every request instead of surfacing as a routing bug.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalResponsesCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("hello")], [schema], $"openai/gpt-5.6-luna-{Guid.NewGuid()}");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var tool = json.RootElement.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("name").GetString());
        Assert.Equal("Reads a file.", tool.GetProperty("description").GetString());
        Assert.False(tool.TryGetProperty("function", out _));
    }

    [Fact]
    public async Task StreamAsync_ResponsesApi_AssistantMessageWithTextAndToolUse_TextItemPrecedesFunctionCallItem()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalResponsesCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var toolUseArgs = JsonDocument.Parse("""{"path":"foo.cs"}""").RootElement;
        var assistantMessage = ChatMessage.Assistant([new TextBlock("Let me check that file."), new ToolUseBlock("call_1", "read_file", toolUseArgs)]);
        var request = new ChatRequest([assistantMessage], [schema], $"openai/gpt-5.6-luna-{Guid.NewGuid()}");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var input = json.RootElement.GetProperty("input");
        Assert.Equal("assistant", input[0].GetProperty("role").GetString());
        Assert.Equal("Let me check that file.", input[0].GetProperty("content").GetString());
        Assert.Equal("function_call", input[1].GetProperty("type").GetString());
        Assert.Equal("call_1", input[1].GetProperty("call_id").GetString());
        Assert.Equal("read_file", input[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task StreamAsync_ResponsesApi_ToolResultMessage_SerializesAsFunctionCallOutput()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalResponsesCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var toolResultMessage = ChatMessage.ToolResult("call_1", ToolResult.Ok("file contents"));
        var request = new ChatRequest([toolResultMessage], [schema], $"openai/gpt-5.6-luna-{Guid.NewGuid()}");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var item = json.RootElement.GetProperty("input")[0];
        Assert.Equal("function_call_output", item.GetProperty("type").GetString());
        Assert.Equal("call_1", item.GetProperty("call_id").GetString());
        Assert.Equal("file contents", item.GetProperty("output").GetString());
    }

    [Fact]
    public async Task StreamAsync_ResponsesApiErrorBody_NotJson_FallsBackToChatCompletions()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("<html>Bad Request</html>", System.Text.Encoding.UTF8, "text/html"),
        });
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("hi")], [schema], $"openai/gpt-5.6-luna-{Guid.NewGuid()}");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.EndsWith("chat/completions", handler.CapturedRequests[1].Uri!.AbsolutePath);
        Assert.Contains(events, e => e is TextDelta { Text: "hi" });
    }

    [Fact]
    public async Task StreamAsync_ResponsesApiReturnsModelCapabilityNotSupported_FallsBackToChatCompletions()
    {
        var (provider, handler) = CreateProvider();
        var errorBody = """{"error":{"code":"model_capability_not_supported","message":"nope"}}""";
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorBody, System.Text.Encoding.UTF8, "application/json"),
        });
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var request = new ChatRequest([ChatMessage.User("hi")], [schema], $"unsupported-model-{Guid.NewGuid()}");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.EndsWith("responses", handler.CapturedRequests[0].Uri!.AbsolutePath);
        Assert.EndsWith("chat/completions", handler.CapturedRequests[1].Uri!.AbsolutePath);
        Assert.Contains(events, e => e is TextDelta { Text: "hi" });
    }

    [Fact]
    public async Task StreamAsync_ModelPreviouslyConfirmedUnsupported_SkipsResponsesApiOnNextCall()
    {
        var (provider, handler) = CreateProvider();
        var model = $"unsupported-model-{Guid.NewGuid()}";
        var errorBody = """{"error":{"code":"model_capability_not_supported","message":"nope"}}""";
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorBody, System.Text.Encoding.UTF8, "application/json"),
        });
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var schema = new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
        var firstRequest = new ChatRequest([ChatMessage.User("hi")], [schema], model);
        await DrainAsync(provider.StreamAsync(firstRequest, CancellationToken.None));
        Assert.Equal(2, handler.CapturedRequests.Count);

        // Second call for the same model should go straight to chat/completions.
        handler.Enqueue(FakeHttpMessageHandler.SseResponse(MinimalSseCompletion));
        var secondRequest = new ChatRequest([ChatMessage.User("hi again")], [schema], model);
        await DrainAsync(provider.StreamAsync(secondRequest, CancellationToken.None));

        Assert.Equal(3, handler.CapturedRequests.Count);
        Assert.EndsWith("chat/completions", handler.CapturedRequests[2].Uri!.AbsolutePath);
    }

    [Fact]
    public async Task ListModelsAsync_MapsIdAndName_FallsBackToIdWhenNameMissing()
    {
        // MeshAPI's GET /models returns a bare JSON array at the top level, unlike OpenRouter's
        // {"data": [...]} envelope — confirmed against the live API.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            [{"id":"openai/gpt-4o-mini","name":"GPT-4o mini"},{"id":"model-b","name":null}]
            """));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(2, models.Count);
        Assert.Equal("GPT-4o mini", models[0].DisplayName);
        Assert.Equal("model-b", models[1].DisplayName);
        Assert.All(models, m => Assert.False(m.IsDefault));
    }
}
