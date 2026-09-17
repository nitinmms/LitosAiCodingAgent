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
            Tools: request.Tools.Count == 0 ? null : [.. request.Tools.Select(ToOpenRouterTool)],
            // See OpenRouterCacheControl. Anthropic models routed through OpenRouter cache nothing
            // without this — the native Anthropic path sets PromptCaching on MessageParameters, but
            // that is SDK-specific and does not apply to OpenRouter's OpenAI-compatible wire format.
            CacheControl: new OpenRouterCacheControl("ephemeral"),
            // Pins sticky routing to one upstream provider from the very first request. Without it
            // OpenRouter only starts pinning *after* it observes a cache hit, so the round that
            // wrote the cache can be answered by a different upstream than the round that would
            // have read it — the write is then paid for and never reused. Also groups the whole
            // conversation in OpenRouter's Logs Sessions view. Capped at 256 chars per their API;
            // session ids here are short (GUID-like), but truncate rather than risk a 400 on a
            // caller that uses something longer.
            SessionId: request.SessionId is { Length: > 256 } id ? id[..256] : request.SessionId);

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
        var cacheReadTokens = 0;
        var cacheWriteTokens = 0;

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
                // OpenRouter normalizes most providers so PromptTokens *includes* the cached
                // counts (its own docs show prompt_tokens=10_339 with cached_tokens=10_318), but
                // that normalization is not reliable across every upstream — Anthropic routes in
                // particular have been observed reporting the cached counts as additional
                // categories *not* folded into prompt_tokens, understating it. UsageInfo's three
                // fields must be mutually exclusive so TotalInputTokens is a plain sum, so rather
                // than trusting either convention, detect which one this response used:
                // PromptTokens larger than the cached counts means inclusive (subtract them out),
                // otherwise it is already the non-cached remainder (pass through untouched).
                // Guessing wrong in the exclusive direction would zero inputTokens and make a
                // fully-cached session look empty, which is what silently disables compaction.
                cacheReadTokens = usage.PromptTokensDetails?.CachedTokens ?? 0;
                cacheWriteTokens = usage.PromptTokensDetails?.CacheWriteTokens ?? 0;
                var cachedTotal = cacheReadTokens + cacheWriteTokens;
                inputTokens = usage.PromptTokens > cachedTotal ? usage.PromptTokens - cachedTotal : usage.PromptTokens;
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

        yield return new MessageCompleted(LM.ChatMessage.Assistant(contentBlocks), new UsageInfo(inputTokens, outputTokens, cacheWriteTokens, cacheReadTokens));
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
    List<OpenRouterTool>? Tools,
    OpenRouterCacheControl? CacheControl,
    [property: JsonPropertyName("session_id")] string? SessionId);

/// <summary>
/// Top-level cache_control, OpenRouter's "automatic" caching mode: it places the breakpoint on
/// the last cacheable block and advances it forward as the conversation grows, which is what an
/// agent loop wants — explicit per-block breakpoints are capped at four and would have to be
/// re-placed by hand every round. Only providers that require explicit breakpoints act on this
/// (Anthropic, Qwen); the ones that cache automatically (OpenAI, Gemini 2.5, Grok, DeepSeek,
/// Groq, Moonshot, Z.AI) ignore it, so sending it unconditionally is safe and costs nothing.
/// </summary>
internal sealed record OpenRouterCacheControl(string Type);

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

/// <summary>
/// PromptTokens is *inclusive* of any cached tokens — OpenRouter reports a 10_339-token prompt
/// with 10_318 of those served from cache as prompt_tokens=10_339, cached_tokens=10_318. This is
/// the opposite of Anthropic's native API, where input_tokens counts only the post-breakpoint
/// remainder and the cached counts must be added to get the true total (see UsageInfo). So the
/// cache split is carried here for visibility only and must NOT be added to PromptTokens.
/// </summary>
internal sealed record OpenRouterUsage(
    int PromptTokens,
    int CompletionTokens,
    [property: JsonPropertyName("prompt_tokens_details")] OpenRouterPromptTokensDetails? PromptTokensDetails);

internal sealed record OpenRouterPromptTokensDetails(
    [property: JsonPropertyName("cached_tokens")] int? CachedTokens,
    [property: JsonPropertyName("cache_write_tokens")] int? CacheWriteTokens);
