using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using LM = Litos.Agent.Messages;

namespace Litos.Providers.OpenRouter;

public sealed class OpenRouterChatProvider(HttpClient httpClient) : IChatProvider
{
    public string ProviderName => "openrouter";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var response = await httpClient.GetFromJsonAsync<OpenRouterModelListResponse>("models", ct);
        return [.. (response?.Data ?? []).Select(m => new ModelInfo(m.Id, m.Name ?? m.Id, IsDefault: false, ContextLength: m.ContextLength))];
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<OpenRouterMessage>();
        if (request.SystemPrompt is { } systemPrompt)
            messages.Add(new OpenRouterMessage("system", systemPrompt, null, null));
        messages.AddRange(request.Messages.Select(ToOpenRouterMessage));

        var payload = new OpenRouterChatRequest(
            Model: request.Model,
            Messages: messages,
            Stream: true,
            Temperature: request.Temperature,
            MaxTokens: request.MaxOutputTokens,
            Tools: request.Tools.Count == 0 ? null : [.. request.Tools.Select(ToOpenRouterTool)]);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // Surfaced verbatim to the user via AgentEvent.ErrorOccurred (e.g. TelegramSessionDriver.
            // StreamRepliesAsync just sends error.Exception.Message as-is), so this message is
            // written for a human reading it in chat, not a raw EnsureSuccessStatusCode() dump —
            // OpenRouter and the underlying model both enforce their own rate limits independently
            // of anything this app controls, so "try again" is the only actionable guidance to give.
            var retryAfter = response.Headers.RetryAfter?.Delta is { } delta
                ? $" Try again in about {(int)Math.Ceiling(delta.TotalSeconds)}s."
                : " Try again in a moment.";
            throw new ChatProviderRateLimitedException($"OpenRouter is rate-limiting requests for {request.Model} right now.{retryAfter}");
        }
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var textBuilder = new StringBuilder();
        var toolCallIds = new Dictionary<int, string>();
        var toolCallNames = new Dictionary<int, string>();
        var toolCallJson = new Dictionary<int, StringBuilder>();
        var toolCallOrder = new List<int>();
        var inputTokens = 0;
        var outputTokens = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payloadText = line["data:".Length..].Trim();
            if (payloadText == "[DONE]")
                break;
            if (string.IsNullOrWhiteSpace(payloadText))
                continue;

            OpenRouterStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OpenRouterStreamChunk>(payloadText, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // SSE "comment" payloads are not JSON and should be ignored
            }

            if (chunk is null)
                continue;

            if (chunk.Usage is { } usage)
            {
                inputTokens = usage.PromptTokens;
                outputTokens = usage.CompletionTokens;
            }

            var delta = chunk.Choices?.FirstOrDefault()?.Delta;
            if (delta is null)
                continue;

            if (!string.IsNullOrEmpty(delta.Content))
            {
                textBuilder.Append(delta.Content);
                yield return new TextDelta(delta.Content);
            }

            foreach (var toolCallDelta in delta.ToolCalls ?? [])
            {
                var index = toolCallDelta.Index;
                if (!toolCallJson.ContainsKey(index))
                {
                    toolCallIds[index] = toolCallDelta.Id ?? string.Empty;
                    toolCallNames[index] = toolCallDelta.Function?.Name ?? string.Empty;
                    toolCallJson[index] = new StringBuilder();
                    toolCallOrder.Add(index);
                    yield return new ToolCallStarted(toolCallIds[index], toolCallNames[index]);
                }

                var argsFragment = toolCallDelta.Function?.Arguments;
                if (!string.IsNullOrEmpty(argsFragment))
                {
                    toolCallJson[index].Append(argsFragment);
                    yield return new ToolCallArgsDelta(toolCallIds[index], argsFragment);
                }
            }
        }

        foreach (var index in toolCallOrder)
            yield return new ToolCallCompleted(toolCallIds[index], toolCallNames[index], ParseToolArguments(toolCallJson[index]));

        var contentBlocks = new List<LM.ContentBlock>();
        if (textBuilder.Length > 0)
            contentBlocks.Add(new LM.TextBlock(textBuilder.ToString()));
        foreach (var index in toolCallOrder)
            contentBlocks.Add(new LM.ToolUseBlock(toolCallIds[index], toolCallNames[index], ParseToolArguments(toolCallJson[index])));

        yield return new MessageCompleted(LM.ChatMessage.Assistant(contentBlocks), new UsageInfo(inputTokens, outputTokens));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonElement ParseToolArguments(StringBuilder json)
    {
        var text = json.ToString();
        return string.IsNullOrWhiteSpace(text)
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(text).RootElement;
    }

    private static OpenRouterMessage ToOpenRouterMessage(LM.ChatMessage message)
    {
        if (message.Role == LM.Role.Assistant)
        {
            var toolCalls = message.Content.OfType<LM.ToolUseBlock>()
                .Select(u => new OpenRouterToolCall(u.CallId, "function", new OpenRouterFunctionCall(u.ToolName, u.Arguments.GetRawText())))
                .ToList();
            var text = string.Concat(message.Content.OfType<LM.TextBlock>().Select(t => t.Text));
            return new OpenRouterMessage("assistant", text.Length > 0 ? text : null, toolCalls.Count > 0 ? toolCalls : null, null);
        }

        var toolResult = message.Content.OfType<LM.ToolResultBlock>().FirstOrDefault();
        if (toolResult is not null)
            return new OpenRouterMessage("tool", toolResult.Text, null, toolResult.CallId);

        // Plain string content only covers text; a message containing an image must use
        // the OpenAI-compatible multi-part content array (image_url as a base64 data URI)
        // or the image silently disappears — the same failure this provider had for every
        // ImageBlock before this fix (dropped by the old string-only concatenation below).
        if (message.Content.Any(b => b is LM.ImageBlock))
        {
            var parts = message.Content.Select(block => block switch
            {
                LM.TextBlock t => (OpenRouterContentPart)new OpenRouterTextPart(t.Text),
                LM.ImageBlock i => new OpenRouterImagePart(new OpenRouterImageUrl($"data:{i.MediaType};base64,{Convert.ToBase64String(i.Data)}")),
                LM.CompactionSummaryBlock c => new OpenRouterTextPart(c.Summary),
                _ => null,
            }).OfType<OpenRouterContentPart>().ToList();
            return new OpenRouterMessage("user", parts, null, null);
        }

        var text2 = string.Concat(message.Content.Select(block => block switch
        {
            LM.TextBlock t => t.Text,
            LM.CompactionSummaryBlock c => c.Summary,
            _ => string.Empty,
        }));
        return new OpenRouterMessage("user", text2, null, null);
    }

    private static OpenRouterTool ToOpenRouterTool(ToolSchema schema) =>
        new("function", new OpenRouterFunctionSchema(schema.Name, schema.Description, JsonDocument.Parse(schema.ParameterSchema.GetRawText()).RootElement));
}

internal sealed record OpenRouterChatRequest(
    string Model,
    List<OpenRouterMessage> Messages,
    bool Stream,
    double? Temperature,
    int? MaxTokens,
    List<OpenRouterTool>? Tools);

// Content is either a plain string (text-only message) or a List<OpenRouterContentPart>
// (a message that includes an image) — OpenRouter's OpenAI-compatible wire format accepts
// both shapes for the same field, so this stays object? rather than a single strong type.
internal sealed record OpenRouterMessage(
    string Role,
    object? Content,
    List<OpenRouterToolCall>? ToolCalls,
    string? ToolCallId);

// [JsonPolymorphic] is required here: elements are stored in a List<OpenRouterContentPart>
// (declared as the abstract base), and without a configured discriminator System.Text.Json
// would serialize each element using only OpenRouterContentPart's own members (none) rather
// than the concrete OpenRouterTextPart/OpenRouterImagePart shape — losing Text/ImageUrl
// entirely. TypeDiscriminatorPropertyName "type" matches the field name OpenRouter's
// OpenAI-compatible content-part schema expects ("text" / "image_url").
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenRouterTextPart), "text")]
[JsonDerivedType(typeof(OpenRouterImagePart), "image_url")]
internal abstract record OpenRouterContentPart;

internal sealed record OpenRouterTextPart(string Text) : OpenRouterContentPart;

internal sealed record OpenRouterImagePart(OpenRouterImageUrl ImageUrl) : OpenRouterContentPart;

internal sealed record OpenRouterImageUrl(string Url);

internal sealed record OpenRouterToolCall(string Id, string Type, OpenRouterFunctionCall Function);

internal sealed record OpenRouterFunctionCall(string Name, string Arguments);

internal sealed record OpenRouterTool(string Type, OpenRouterFunctionSchema Function);

internal sealed record OpenRouterFunctionSchema(string Name, string Description, JsonElement Parameters);

internal sealed record OpenRouterModelListResponse(List<OpenRouterModel> Data);

internal sealed record OpenRouterModel(string Id, string? Name, [property: JsonPropertyName("context_length")] int? ContextLength);

internal sealed record OpenRouterStreamChunk(List<OpenRouterStreamChoice>? Choices, OpenRouterUsage? Usage);

internal sealed record OpenRouterStreamChoice(OpenRouterDelta? Delta);

internal sealed record OpenRouterDelta(string? Content, List<OpenRouterToolCallDelta>? ToolCalls);

internal sealed record OpenRouterToolCallDelta(int Index, string? Id, OpenRouterFunctionCallDelta? Function);

internal sealed record OpenRouterFunctionCallDelta(string? Name, string? Arguments);

internal sealed record OpenRouterUsage(int PromptTokens, int CompletionTokens);
