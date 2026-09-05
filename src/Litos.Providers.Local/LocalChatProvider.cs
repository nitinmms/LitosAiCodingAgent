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
    /// <summary>
    /// Used when a local server's /models response omits context_length (the common case — LM
    /// Studio's plain OpenAI-compatible endpoint doesn't include it; that only appears on its
    /// separate, vendor-specific /api/v0/models). Deliberately conservative rather than reusing
    /// ModelContextWindows.FallbackContextLength (128K, calibrated for hosted models): guessing
    /// too high here silently defeats the context meter and compaction for exactly the small
    /// local models most likely to hit this fallback, which is the failure mode this exists to
    /// prevent.
    /// 8_000 (the original guess here) turned out to cut the margin between the compaction
    /// threshold and the assumed ceiling too thin: CompactionSettings.ForContextWindow caps
    /// ReserveTokens/KeepRecentTokens at contextWindowTokens/4, so an 8_000 window left only a
    /// 2_000-token gap between "compaction just fired" and "assumed full" — observed live to be
    /// smaller than a single large tool result (e.g. a real file read), so the very next
    /// tool-calling round after a compaction could still blow straight past the assumed window
    /// before compaction got another chance to run. 16_000 doubles that gap to 4_000, giving one
    /// more large tool result room to land before the next compaction check. Still far below
    /// ModelContextWindows.FallbackContextLength for the same reason as above — this is a floor
    /// to keep compaction functional, not an attempt to guess a specific model's real window.
    /// </summary>
    private const int FallbackContextLength = 16_000;

    public string ProviderName => "local";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var response = await httpClient.GetFromJsonAsync<LocalModelListResponse>("models", ct);
        return [.. (response?.Data ?? []).Select(m => new ModelInfo(m.Id, m.Name ?? m.Id, IsDefault: false, ContextLength: m.ContextLength ?? FallbackContextLength))];
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

    private static JsonElement ParseToolArguments(StringBuilder json)
    {
        var text = json.ToString();
        return string.IsNullOrWhiteSpace(text)
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(text).RootElement;
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

internal sealed record LocalStreamChunk(List<LocalStreamChoice>? Choices, LocalUsage? Usage);

internal sealed record LocalStreamChoice(LocalDelta? Delta);

internal sealed record LocalDelta(string? Content, string? ReasoningContent, List<LocalToolCallDelta>? ToolCalls);

internal sealed record LocalToolCallDelta(int Index, string? Id, LocalFunctionCallDelta? Function);

internal sealed record LocalFunctionCallDelta(string? Name, string? Arguments);

internal sealed record LocalUsage(int PromptTokens, int CompletionTokens);
