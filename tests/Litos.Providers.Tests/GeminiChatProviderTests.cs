using System.Text.Json;
using global::GenerativeAI;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Providers.Gemini;
using Litos.Providers.Tests.Fakes;

namespace Litos.Providers.Tests;

public class GeminiChatProviderTests
{
    // Empirically verified minimal body: Google_GenerativeAI 3.6.6's StreamContentAsync
    // reads the response via JsonSerializer.DeserializeAsyncEnumerable<GenerateContentResponse>
    // directly against the raw stream — a top-level JSON ARRAY is mandatory (not SSE, not
    // NDJSON); a bare object or newline-delimited objects both throw JsonException.
    // StreamContentAsync also silently skips any chunk whose "candidates" is absent.
    private const string MinimalTextResponse =
        """[{"candidates":[{"content":{"parts":[{"text":"hello"}]}}]}]""";

    private static (GeminiChatProvider Provider, FakeHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
        var client = new GoogleAi("fake-key", accessToken: null!, httpClient, logger: null!);
        // Empty catalog (no queued response) makes OpenRouterModelCatalog.ResolveAsync fail and
        // return null, so ListModelsAsync falls back to ModelContextWindows' static table when
        // InputTokenLimit is absent from the fixture — deterministic without a second queue.
        var contextCatalog = new OpenRouterModelCatalog(new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") });
        return (new GeminiChatProvider(client, contextCatalog), handler);
    }

    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (var evt in events)
            list.Add(evt);
        return list;
    }

    [Fact]
    public async Task StreamAsync_TextOnlyUserMessage_SerializesAsTextPart_WithUserRole()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var request = new ChatRequest([ChatMessage.User("hello")], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("contents")[0];
        Assert.Equal("user", content.GetProperty("role").GetString());
        Assert.Equal("hello", content.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task StreamAsync_AssistantRole_MapsToModelRole_NotAssistant()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var request = new ChatRequest([ChatMessage.Assistant([new TextBlock("prior reply")])], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        Assert.Equal("model", json.RootElement.GetProperty("contents")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task StreamAsync_ImageBlock_SerializesAsInlineDataBase64()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.User([new ImageBlock("image/png", [1, 2, 3])]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var part = json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0];
        var inlineData = part.GetProperty("inlineData");
        Assert.Equal("image/png", inlineData.GetProperty("mimeType").GetString());
        Assert.Equal("AQID", inlineData.GetProperty("data").GetString());
    }

    [Fact]
    public async Task StreamAsync_ToolUseBlock_SerializesAsFunctionCall()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var toolArgs = JsonDocument.Parse("""{"path":"foo.cs"}""").RootElement;
        var message = ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", toolArgs)]);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var part = json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0];
        var functionCall = part.GetProperty("functionCall");
        Assert.Equal("read_file", functionCall.GetProperty("name").GetString());
        Assert.Equal("call_1", functionCall.GetProperty("id").GetString());
        Assert.Equal("foo.cs", functionCall.GetProperty("args").GetProperty("path").GetString());
    }

    [Fact]
    public async Task StreamAsync_ToolResultBlock_SerializesAsFunctionResponse_WithResultAndIsErrorWrapper()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.ToolResult("call_1", ToolResult.Error("file not found"));
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var part = json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0];
        var functionResponse = part.GetProperty("functionResponse");
        Assert.Equal("call_1", functionResponse.GetProperty("id").GetString());
        var response = functionResponse.GetProperty("response");
        Assert.Equal("file not found", response.GetProperty("result").GetString());
        Assert.True(response.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task StreamAsync_CompactionSummaryBlock_SerializesAsPlainTextPart_DropsTokensBefore()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var message = ChatMessage.CompactionSummary("summary text", 5000);
        var request = new ChatRequest([message], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var part = json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0];
        Assert.Equal("summary text", part.GetProperty("text").GetString());
        Assert.DoesNotContain("5000", body);
    }

    [Fact]
    public async Task StreamAsync_MultipleToolSchemas_AggregatedIntoOneToolWithFunctionDeclarationsList()
    {
        // Unlike the other three providers (1:1 tool -> tool entry), Gemini aggregates
        // every ToolSchema into a single Tool{FunctionDeclarations:[...]}.
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var schemas = new[]
        {
            new ToolSchema("read_file", "Reads a file.", JsonDocument.Parse("{}").RootElement),
            new ToolSchema("write_file", "Writes a file.", JsonDocument.Parse("{}").RootElement),
        };
        var request = new ChatRequest([ChatMessage.User("hi")], schemas, "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        var tools = json.RootElement.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        var declarations = tools[0].GetProperty("functionDeclarations");
        Assert.Equal(2, declarations.GetArrayLength());
        Assert.Equal("read_file", declarations[0].GetProperty("name").GetString());
        Assert.Equal("write_file", declarations[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task StreamAsync_NoTools_ToolsFieldOmitted()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.CapturedRequests[0].Body!;
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("tools", out _));
    }

    // ---- Response parsing ----

    [Fact]
    public async Task StreamAsync_ParsesTextChunk_IntoTextDeltaEvent()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(MinimalTextResponse));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Contains(events, e => e is TextDelta { Text: "hello" });
        var completed = Assert.Single(events.OfType<MessageCompleted>());
        Assert.Equal(Role.Assistant, completed.Message.Role);
    }

    [Fact]
    public async Task StreamAsync_MultipleChunksInArray_AccumulateTextInOrder()
    {
        var response = """[{"candidates":[{"content":{"parts":[{"text":"he"}]}}]},{"candidates":[{"content":{"parts":[{"text":"llo"}]}}]}]""";
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(response));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var textDeltas = events.OfType<TextDelta>().Select(t => t.Text);
        Assert.Equal(["he", "llo"], textDeltas);
    }

    [Fact]
    public async Task StreamAsync_UsageMetadata_YieldsMessageCompletedWithUsage()
    {
        var response = """[{"candidates":[{"content":{"parts":[{"text":"hi"}]}}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}]""";
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(response));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var completed = Assert.Single(events.OfType<MessageCompleted>());
        Assert.Equal(10, completed.Usage.InputTokens);
        Assert.Equal(5, completed.Usage.OutputTokens);
    }

    [Fact]
    public async Task StreamAsync_FunctionCallInResponse_YieldsToolCallCompleted()
    {
        var response = """
            [{"candidates":[{"content":{"parts":[{"functionCall":{"name":"read_file","args":{"path":"foo.cs"}}}]}}]}]
            """;
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse(response));
        var request = new ChatRequest([ChatMessage.User("hi")], [], "model");

        var events = await DrainAsync(provider.StreamAsync(request, CancellationToken.None));

        var completed = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.Equal("read_file", completed.ToolName);
        Assert.Equal("foo.cs", completed.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ListModelsAsync_MapsNameAndDisplayName_FallsBackToNameWhenDisplayNameMissing()
    {
        var (provider, handler) = CreateProvider();
        handler.Enqueue(FakeHttpMessageHandler.JsonResponse("""
            {"models":[{"name":"models/gemini-pro","displayName":"Gemini Pro"},{"name":"models/gemini-flash","displayName":null}]}
            """));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(2, models.Count);
        Assert.Equal("Gemini Pro", models[0].DisplayName);
        Assert.Equal("models/gemini-flash", models[1].DisplayName);
    }
}
