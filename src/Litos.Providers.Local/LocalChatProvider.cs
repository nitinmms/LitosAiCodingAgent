using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using LM = Litos.Agent.Messages;

namespace Litos.Providers.Local;

/// <summary>
/// Talks to any local OpenAI-compatible chat-completions server (LM Studio, Ollama, vLLM,
/// LocalAI, ...) via the standard `/models` + `/chat/completions` (SSE) wire format. The host
/// and port are entirely up to whoever constructs this provider's HttpClient (see
/// LitosHostBuilder) — this class hardcodes nothing about where "local" actually points.
/// A standalone implementation rather than sharing code with OpenRouterChatProvider (same wire
/// format, different vendor) — kept separate deliberately so this provider's behavior (e.g. no
/// required auth) can diverge without touching the OpenRouter provider's already-tested code.
/// </summary>
public sealed class LocalChatProvider(HttpClient httpClient) : IChatProvider
{
    public string ProviderName => "local";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var response = await httpClient.GetFromJsonAsync<LocalModelListResponse>("models", ct);
        var vendorContextLengths = await TryGetLmStudioContextLengthsAsync(ct);
        return [.. (response?.Data ?? []).Select(m => new ModelInfo(
            m.Id,
            m.Name ?? m.Id,
            IsDefault: false,
            ContextLength: m.ContextLength ?? (vendorContextLengths.TryGetValue(m.Id, out var vendorContextLength) ? (int?)vendorContextLength : null) ?? ModelContextWindows.LocalFallbackContextLength))];
    }

    /// <summary>
    /// Bounds how long the best-effort LM Studio vendor-catalog lookup below may take before
    /// ListModelsAsync gives up on it and falls through to the static fallback. Deliberately short
    /// relative to the app's general HTTP timeouts: this is a nice-to-have enrichment layered on
    /// top of the real, required /models call, not something a non-LM-Studio server (vLLM,
    /// llama.cpp, Ollama, LocalAI, ...) should ever be able to make ListModelsAsync hang on — a
    /// server that accepts the connection but never responds (rather than cleanly 404ing) would
    /// otherwise stall model listing for as long as HttpClient's own default timeout allows.
    /// </summary>
    private static readonly TimeSpan LmStudioVendorLookupTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Best-effort lookup against LM Studio's native /api/v1/models endpoint, which (unlike the
    /// plain OpenAI-compatible /models this provider otherwise talks to exclusively) reports each
    /// model's real context length — so a fallback guess only kicks in for servers that aren't LM
    /// Studio (Ollama, vLLM, LocalAI, ...), where this 404s/fails/times out and is ignored.
    /// Queried as a sibling of the configured base URL (.../v1/) rather than a relative path under
    /// it, since LM Studio serves this endpoint off the server root, not under the OpenAI-
    /// compatible /v1/ this provider otherwise talks to.
    /// Prefers the currently loaded instance's context_length over the model's max_context_length:
    /// LM Studio lets a model be loaded with a smaller context than its architectural max (to fit
    /// available memory), and using the max here would reintroduce the exact silent-overflow risk
    /// LocalFallbackContextLength exists to avoid — the loaded value is what the server will
    /// actually accept right now, not what the model could theoretically support. A model with no
    /// loaded_instances entry (not currently loaded) falls back to its max_context_length instead,
    /// since there's no "currently accepted" value to prefer yet.
    /// </summary>
    private async Task<Dictionary<string, int>> TryGetLmStudioContextLengthsAsync(CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(LmStudioVendorLookupTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var vendorUri = new Uri(httpClient.BaseAddress!, "../api/v1/models");
            using var response = await httpClient.GetAsync(vendorUri, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
                return [];

            var payload = await response.Content.ReadFromJsonAsync<LmStudioModelListResponse>(cancellationToken: linkedCts.Token);
            var result = new Dictionary<string, int>();
            foreach (var m in payload?.Models ?? [])
            {
                var loadedContextLength = m.LoadedInstances?.FirstOrDefault()?.Config?.ContextLength;
                if ((loadedContextLength ?? m.MaxContextLength) is { } contextLength)
                    result[m.Key] = contextLength;
            }
            return result;
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            // Not an LM Studio server, it's unreachable/malformed-JSON, or it hit
            // LmStudioVendorLookupTimeout above — ListModelsAsync's own ?? fallback chain handles
            // the missing values from here. The `when` guard re-throws if the caller's own token
            // (not our internal timeout) was the one that fired, so a genuine
            // caller-requested cancellation still propagates instead of being swallowed.
            return [];
        }
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<LocalMessage>();
        if (request.SystemPrompt is { } systemPrompt)
            messages.Add(new LocalMessage("system", systemPrompt, null, null));
        messages.AddRange(request.Messages.Select(ToLocalMessage));

        var payload = new LocalChatRequest(
            Model: request.Model,
            Messages: messages,
            Stream: true,
            Temperature: request.Temperature,
            MaxTokens: request.MaxOutputTokens,
            Tools: request.Tools.Count == 0 ? null : [.. request.Tools.Select(ToLocalTool)],
            // Most local OpenAI-compatible servers (LM Studio, Ollama, vLLM) only include a
            // `usage` object on stream chunks when this is explicitly requested — without it,
            // real token usage never reaches Transcript.LastUsage, which silently breaks both the
            // context-usage meter and compaction (CompactionPlanner never sees real numbers to
            // compare against the context window). An unrecognized field is ignored by any
            // spec-compliant server, so this is safe to send unconditionally.
            StreamOptions: new LocalStreamOptions(IncludeUsage: true));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta is { } delta
                ? $" Try again in about {(int)Math.Ceiling(delta.TotalSeconds)}s."
                : " Try again in a moment.";
            throw new ChatProviderRateLimitedException($"The local model server at {httpClient.BaseAddress} is rate-limiting requests for {request.Model} right now.{retryAfter}");
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
            // Any line at all — including one this loop is about to skip below (SSE framing,
            // keep-alive comments, a chunk with no usable delta) — proves the connection is alive
            // and the server is actively producing output, even if none of it is real content yet.
            // AgentLoop's idle-gap timeout only resets on a yielded AgentEvent, not on raw network
            // activity, so without this, a "thinking" local model that goes quiet on real content
            // for a long stretch (very much alive, just not done reasoning) looks identical to a
            // genuinely stalled connection from AgentLoop's perspective. See StreamHeartbeat's own
            // doc comment for the full story.
            yield return new StreamHeartbeat();

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payloadText = line["data:".Length..].Trim();
            if (payloadText == "[DONE]")
                break;
            if (string.IsNullOrWhiteSpace(payloadText))
                continue;

            LocalStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<LocalStreamChunk>(payloadText, JsonOptions);
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

            // "Thinking" models served locally (Qwen3, DeepSeek-R1, QwQ, ...) stream their
            // chain-of-thought under this separate field rather than Content — LM Studio can
            // emit thousands of these tokens before Content ever starts. Surfaced as its own
            // event (not TextDelta) so a UI can render it distinctly and so it's never folded
            // into textBuilder below: the accumulated text becomes this turn's permanent
            // assistant message, replayed back to the model on every future turn, and old
            // chain-of-thought has no business being replayed as if it were part of the
            // conversation.
            if (!string.IsNullOrEmpty(delta.ReasoningContent))
                yield return new ReasoningDelta(delta.ReasoningContent);

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

    /// <summary>
    /// Falls back to an empty object on genuinely malformed JSON (truncated/broken argument
    /// blobs), the same as the empty/whitespace case below, rather than letting JsonException
    /// escape into StreamAsync's async-iterator body. Uncaught there, it used to propagate all
    /// the way up through AgentLoop's stream-consumption loop as a generic streamError, ending
    /// the whole turn (ErrorOccurred, which is UI-only and never persisted to the transcript)
    /// instead of the tool call simply failing. Local models are more prone to malformed
    /// tool-call arguments than hosted ones (see JsonElementToolArgumentExtensions), so this
    /// path is hit in practice, not just in theory. An empty object lets the call still reach
    /// InvokeToolSafelyAsync, where each tool's required-argument check (via GetStringOrNull)
    /// produces a normal, model-visible ToolResult.Error instead of crashing the stream — the
    /// same "error becomes a tool result, model decides what to do next" recovery path used
    /// everywhere else in the harness.
    /// </summary>
    private static JsonElement ParseToolArguments(StringBuilder json)
    {
        var text = json.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return JsonDocument.Parse("{}").RootElement;

        try
        {
            return JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static LocalMessage ToLocalMessage(LM.ChatMessage message)
    {
        if (message.Role == LM.Role.Assistant)
        {
            var toolCalls = message.Content.OfType<LM.ToolUseBlock>()
                .Select(u => new LocalToolCall(u.CallId, "function", new LocalFunctionCall(u.ToolName, u.Arguments.GetRawText())))
                .ToList();
            var text = string.Concat(message.Content.OfType<LM.TextBlock>().Select(t => t.Text));
            return new LocalMessage("assistant", text.Length > 0 ? text : null, toolCalls.Count > 0 ? toolCalls : null, null);
        }

        var toolResult = message.Content.OfType<LM.ToolResultBlock>().FirstOrDefault();
        if (toolResult is not null)
            return new LocalMessage("tool", toolResult.Text, null, toolResult.CallId);

        if (message.Content.Any(b => b is LM.ImageBlock))
        {
            var parts = message.Content.Select(block => block switch
            {
                LM.TextBlock t => (LocalContentPart)new LocalTextPart(t.Text),
                LM.ImageBlock i => new LocalImagePart(new LocalImageUrl($"data:{i.MediaType};base64,{Convert.ToBase64String(i.Data)}")),
                LM.CompactionSummaryBlock c => new LocalTextPart(c.Summary),
                _ => null,
            }).OfType<LocalContentPart>().ToList();
            return new LocalMessage("user", parts, null, null);
        }

        var text2 = string.Concat(message.Content.Select(block => block switch
        {
            LM.TextBlock t => t.Text,
            LM.CompactionSummaryBlock c => c.Summary,
            _ => string.Empty,
        }));
        return new LocalMessage("user", text2, null, null);
    }

    private static LocalTool ToLocalTool(ToolSchema schema) =>
        new("function", new LocalFunctionSchema(schema.Name, schema.Description, JsonDocument.Parse(schema.ParameterSchema.GetRawText()).RootElement));
}

internal sealed record LocalChatRequest(
    string Model,
    List<LocalMessage> Messages,
    bool Stream,
    double? Temperature,
    int? MaxTokens,
    List<LocalTool>? Tools,
    LocalStreamOptions StreamOptions);

internal sealed record LocalStreamOptions(bool IncludeUsage);

// Content is either a plain string (text-only message) or a List<LocalContentPart> (a message
// that includes an image) — the OpenAI-compatible wire format accepts both shapes for the same
// field, so this stays object? rather than a single strong type.
internal sealed record LocalMessage(
    string Role,
    object? Content,
    List<LocalToolCall>? ToolCalls,
    string? ToolCallId);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LocalTextPart), "text")]
[JsonDerivedType(typeof(LocalImagePart), "image_url")]
internal abstract record LocalContentPart;

internal sealed record LocalTextPart(string Text) : LocalContentPart;

internal sealed record LocalImagePart(LocalImageUrl ImageUrl) : LocalContentPart;

internal sealed record LocalImageUrl(string Url);

internal sealed record LocalToolCall(string Id, string Type, LocalFunctionCall Function);

internal sealed record LocalFunctionCall(string Name, string Arguments);

internal sealed record LocalTool(string Type, LocalFunctionSchema Function);

internal sealed record LocalFunctionSchema(string Name, string Description, JsonElement Parameters);

internal sealed record LocalModelListResponse(List<LocalModel> Data);

internal sealed record LocalModel(string Id, string? Name, [property: JsonPropertyName("context_length")] int? ContextLength);

// LM Studio's native /api/v1/models shape — distinct from, and not nested under, the OpenAI-
// compatible LocalModelListResponse/LocalModel above. Keyed by "key" rather than "id".
internal sealed record LmStudioModelListResponse(List<LmStudioModel>? Models);

internal sealed record LmStudioModel(
    string Key,
    [property: JsonPropertyName("max_context_length")] int? MaxContextLength,
    [property: JsonPropertyName("loaded_instances")] List<LmStudioLoadedInstance>? LoadedInstances);

internal sealed record LmStudioLoadedInstance(LmStudioLoadedInstanceConfig? Config);

internal sealed record LmStudioLoadedInstanceConfig([property: JsonPropertyName("context_length")] int? ContextLength);

internal sealed record LocalStreamChunk(List<LocalStreamChoice>? Choices, LocalUsage? Usage);

internal sealed record LocalStreamChoice(LocalDelta? Delta);

internal sealed record LocalDelta(string? Content, string? ReasoningContent, List<LocalToolCallDelta>? ToolCalls);

internal sealed record LocalToolCallDelta(int Index, string? Id, LocalFunctionCallDelta? Function);

internal sealed record LocalFunctionCallDelta(string? Name, string? Arguments);

internal sealed record LocalUsage(int PromptTokens, int CompletionTokens);
