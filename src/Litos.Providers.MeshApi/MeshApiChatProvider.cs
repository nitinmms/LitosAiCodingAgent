using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using LM = Litos.Agent.Messages;

namespace Litos.Providers.MeshApi;

public sealed class MeshApiChatProvider(HttpClient httpClient) : IChatProvider
{
    // Models confirmed (via a 400 model_capability_not_supported error) not to support MeshAPI's
    // /v1/responses endpoint. Process-lifetime only, deliberately not persisted: once a model
    // fails here we stop paying for the extra round-trip on every later call, and a restart just
    // re-learns it for the (presumably rare) unsupported case rather than risking a stale on-disk
    // "unsupported" verdict outliving a capability MeshAPI adds later.
    private static readonly ConcurrentDictionary<string, bool> ModelsUnsupportedByResponsesApi = new();

    public string ProviderName => "mesh_api";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        // Unlike OpenRouter's {"data": [...]} envelope, MeshAPI's GET /models returns a bare JSON
        // array at the top level (confirmed against the live API — its docs' single-object example
        // didn't make this obvious).
        var response = await httpClient.GetFromJsonAsync<List<MeshApiModel>>("models", ct);
        return [.. (response ?? []).Select(m => new ModelInfo(m.Id, m.Name ?? m.Id, IsDefault: false, ContextLength: m.ContextLength))];
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // gpt-5.6-luna (and presumably other reasoning models proxied through MeshAPI) reject
        // function tools on /v1/chat/completions with reasoning enabled: "Function tools with
        // reasoning_effort are not supported ... in /v1/chat/completions. To use function tools,
        // use /v1/responses or set reasoning_effort to 'none'." OpenRouter and the direct OpenAI
        // provider don't hit this because OpenRouter's proxy evidently doesn't inject a conflicting
        // reasoning_effort default, and OpenAiChatProvider already talks to /v1/responses natively.
        // So: only when tools are attached (the only case that can trigger the conflict) and the
        // model hasn't already been confirmed unsupported there, try MeshAPI's /v1/responses first;
        // fall back to the existing /v1/chat/completions path otherwise or on failure.
        if (request.Tools.Count > 0 && !ModelsUnsupportedByResponsesApi.ContainsKey(request.Model))
        {
            var responsesResult = await TryStreamViaResponsesApiAsync(request, ct);
            if (responsesResult is { } events)
            {
                foreach (var evt in events)
                    yield return evt;
                yield break;
            }
        }

        var messages = new List<MeshApiMessage>();
        if (request.SystemPrompt is { } systemPrompt)
            messages.Add(new MeshApiMessage("system", systemPrompt, null, null));
        messages.AddRange(request.Messages.Select(ToMeshApiMessage));

        var payload = new MeshApiChatRequest(
            Model: request.Model,
            Messages: messages,
            Stream: true,
            Temperature: request.Temperature,
            MaxTokens: request.MaxOutputTokens,
            Tools: request.Tools.Count == 0 ? null : [.. request.Tools.Select(ToMeshApiTool)]);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // Same rationale as OpenRouterChatProvider's identical branch: this is surfaced verbatim
            // to the user via AgentEvent.ErrorOccurred, so it's written for a human reading it in
            // chat rather than a raw EnsureSuccessStatusCode() dump.
            var retryAfter = response.Headers.RetryAfter?.Delta is { } delta
                ? $" Try again in about {(int)Math.Ceiling(delta.TotalSeconds)}s."
                : " Try again in a moment.";
            throw new ChatProviderRateLimitedException($"MeshAPI is rate-limiting requests for {request.Model} right now.{retryAfter}");
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

            MeshApiStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<MeshApiStreamChunk>(payloadText, JsonOptions);
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

    private static MeshApiMessage ToMeshApiMessage(LM.ChatMessage message)
    {
        if (message.Role == LM.Role.Assistant)
        {
            var toolCalls = message.Content.OfType<LM.ToolUseBlock>()
                .Select(u => new MeshApiToolCall(u.CallId, "function", new MeshApiFunctionCall(u.ToolName, u.Arguments.GetRawText())))
                .ToList();
            var text = string.Concat(message.Content.OfType<LM.TextBlock>().Select(t => t.Text));
            // Some models MeshAPI proxies to (e.g. glm-4.7-flash, via AWS Bedrock's Converse API
            // underneath) reject a message whose content is entirely omitted/null — Bedrock requires
            // every message to carry a non-empty content array. An assistant message that has
            // neither text nor a tool call (e.g. one holding only a CompactionSummaryBlock or other
            // block type this mapping doesn't recognize — see
            // StreamAsync_AssistantMessageWithUnrecognizedBlockType_SilentlyDropsIt_NoThrow) must
            // still send an empty string rather than null so the message isn't content-less on the
            // wire; null content is only safe when accompanied by tool_calls, which is why this only
            // substitutes "" in the neither-text-nor-tool-calls case.
            var content = text.Length > 0 ? text : toolCalls.Count > 0 ? null : "";
            return new MeshApiMessage("assistant", content, toolCalls.Count > 0 ? toolCalls : null, null);
        }

        var toolResult = message.Content.OfType<LM.ToolResultBlock>().FirstOrDefault();
        if (toolResult is not null)
            return new MeshApiMessage("tool", toolResult.Text, null, toolResult.CallId);

        // Plain string content only covers text; a message containing an image must use the
        // OpenAI-compatible multi-part content array (image_url as a base64 data URI), mirroring
        // OpenRouterChatProvider's own fix for the same wire format.
        if (message.Content.Any(b => b is LM.ImageBlock))
        {
            var parts = message.Content.Select(block => block switch
            {
                LM.TextBlock t => (MeshApiContentPart)new MeshApiTextPart(t.Text),
                LM.ImageBlock i => new MeshApiImagePart(new MeshApiImageUrl($"data:{i.MediaType};base64,{Convert.ToBase64String(i.Data)}")),
                LM.CompactionSummaryBlock c => new MeshApiTextPart(c.Summary),
                _ => null,
            }).OfType<MeshApiContentPart>().ToList();
            return new MeshApiMessage("user", parts, null, null);
        }

        var text2 = string.Concat(message.Content.Select(block => block switch
        {
            LM.TextBlock t => t.Text,
            LM.CompactionSummaryBlock c => c.Summary,
            _ => string.Empty,
        }));
        return new MeshApiMessage("user", text2, null, null);
    }

    private static MeshApiTool ToMeshApiTool(ToolSchema schema) =>
        new("function", new MeshApiFunctionSchema(schema.Name, schema.Description, JsonDocument.Parse(schema.ParameterSchema.GetRawText()).RootElement));

    private static MeshApiResponsesTool ToMeshApiResponsesTool(ToolSchema schema) =>
        new("function", schema.Name, schema.Description, JsonDocument.Parse(schema.ParameterSchema.GetRawText()).RootElement);

    // Returns the AgentEvents to yield on success, or null if the caller should fall back to
    // /v1/chat/completions (either because this model was just confirmed unsupported, or a
    // transient failure occurred that the existing chat/completions path is already set up to
    // report — e.g. rate limiting — so we don't duplicate that handling here).
    //
    // Non-streaming (stream:false) on purpose: MeshAPI's docs only specify the SSE event shape
    // for plain text deltas on /v1/responses, not for function-call argument streaming, so
    // parsing a stream here would be guessing at an undocumented wire format. A single JSON
    // response body is fully documented and lets this synthesize the same TextDelta/
    // ToolCallStarted/ToolCallArgsDelta/ToolCallCompleted/MessageCompleted sequence callers
    // already expect from the streaming path, just delivered in one batch instead of token-by-token.
    private async Task<List<AgentEvent>?> TryStreamViaResponsesApiAsync(ChatRequest request, CancellationToken ct)
    {
        var input = new List<MeshApiResponsesInputItem>();
        if (request.SystemPrompt is { } systemPrompt)
            input.Add(new MeshApiResponsesInputItem("system", systemPrompt));
        input.AddRange(request.Messages.SelectMany(ToMeshApiResponsesInputItems));

        var payload = new MeshApiResponsesRequest(
            Model: request.Model,
            Input: input,
            Stream: false,
            Temperature: request.Temperature,
            MaxOutputTokens: request.MaxOutputTokens,
            Tools: [.. request.Tools.Select(ToMeshApiResponsesTool)]);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        using var response = await httpClient.SendAsync(httpRequest, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity || response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // A 400/422 here can be model_capability_not_supported, but it can just as easily be
            // an unrelated validation error (bad tool schema, malformed input item, ...) whose body
            // doesn't match MeshApiErrorResponse's shape at all — that must still fall back to
            // chat/completions below rather than throwing out of a JSON/content-type mismatch,
            // so a deserialization failure is treated the same as "not this specific error".
            MeshApiErrorResponse? errorBody = null;
            try
            {
                errorBody = await response.Content.ReadFromJsonAsync<MeshApiErrorResponse>(JsonOptions, ct);
            }
            catch (JsonException)
            {
            }

            if (errorBody?.Error?.Code == "model_capability_not_supported")
            {
                ModelsUnsupportedByResponsesApi.TryAdd(request.Model, true);
                return null;
            }
        }
        // Any other non-success status (rate limiting, server errors, ...) falls back to
        // /v1/chat/completions rather than being reported from here, so the existing
        // TooManyRequests-to-ChatProviderRateLimitedException translation stays the single
        // place that happens, instead of duplicating it for this second endpoint.
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadFromJsonAsync<MeshApiResponsesResponse>(JsonOptions, ct);
        if (body is null)
            return null;

        var events = new List<AgentEvent>();
        var textBuilder = new StringBuilder();
        var contentBlocks = new List<LM.ContentBlock>();

        foreach (var item in body.Output ?? [])
        {
            if (item.Type == "message")
            {
                foreach (var part in item.Content ?? [])
                {
                    if (string.IsNullOrEmpty(part.Text))
                        continue;
                    textBuilder.Append(part.Text);
                    events.Add(new TextDelta(part.Text));
                }
            }
            else if (item.Type == "function_call")
            {
                var callId = item.CallId ?? string.Empty;
                var toolName = item.Name ?? string.Empty;
                var argsText = item.Arguments ?? "{}";
                var parsedArgs = ParseToolArguments(argsText);

                events.Add(new ToolCallStarted(callId, toolName));
                events.Add(new ToolCallArgsDelta(callId, argsText));
                events.Add(new ToolCallCompleted(callId, toolName, parsedArgs));
                contentBlocks.Add(new LM.ToolUseBlock(callId, toolName, parsedArgs));
            }
        }

        if (textBuilder.Length > 0)
            contentBlocks.Insert(0, new LM.TextBlock(textBuilder.ToString()));

        events.Add(new MessageCompleted(
            LM.ChatMessage.Assistant(contentBlocks),
            new UsageInfo(body.Usage?.InputTokens ?? 0, body.Usage?.OutputTokens ?? 0)));

        return events;
    }

    private static JsonElement ParseToolArguments(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(json).RootElement;

    // Mirrors ToMeshApiMessage's role/content handling, but tool use/result become distinct
    // function_call / function_call_output input items (the Responses API's shape) rather than
    // a chat message's tool_calls/tool_call_id fields.
    private static IEnumerable<MeshApiResponsesInputItem> ToMeshApiResponsesInputItems(LM.ChatMessage message)
    {
        if (message.Role == LM.Role.Assistant)
        {
            // Text before tool calls, matching the chronology every other assistant-message
            // construction in this file uses (the MessageCompleted content-block ordering on
            // both the chat/completions and /v1/responses response-parsing paths, and
            // OpenRouterChatProvider/OpenAiChatProvider) — the model said something, then acted.
            var text = string.Concat(message.Content.OfType<LM.TextBlock>().Select(t => t.Text));
            if (text.Length > 0)
                yield return new MeshApiResponsesInputItem("assistant", text);

            foreach (var toolUse in message.Content.OfType<LM.ToolUseBlock>())
                yield return MeshApiResponsesInputItem.FunctionCall(toolUse.CallId, toolUse.ToolName, toolUse.Arguments.GetRawText());
            yield break;
        }

        var toolResult = message.Content.OfType<LM.ToolResultBlock>().FirstOrDefault();
        if (toolResult is not null)
        {
            yield return MeshApiResponsesInputItem.FunctionCallOutput(toolResult.CallId, toolResult.Text);
            yield break;
        }

        if (message.Content.Any(b => b is LM.ImageBlock))
        {
            var parts = message.Content.Select(block => block switch
            {
                LM.TextBlock t => (MeshApiContentPart)new MeshApiTextPart(t.Text),
                LM.ImageBlock i => new MeshApiImagePart(new MeshApiImageUrl($"data:{i.MediaType};base64,{Convert.ToBase64String(i.Data)}")),
                LM.CompactionSummaryBlock c => new MeshApiTextPart(c.Summary),
                _ => null,
            }).OfType<MeshApiContentPart>().ToList();
            yield return new MeshApiResponsesInputItem("user", parts);
            yield break;
        }

        var text2 = string.Concat(message.Content.Select(block => block switch
        {
            LM.TextBlock t => t.Text,
            LM.CompactionSummaryBlock c => c.Summary,
            _ => string.Empty,
        }));
        yield return new MeshApiResponsesInputItem("user", text2);
    }
}

internal sealed record MeshApiChatRequest(
    string Model,
    List<MeshApiMessage> Messages,
    bool Stream,
    double? Temperature,
    int? MaxTokens,
    List<MeshApiTool>? Tools);

// Content is either a plain string (text-only message) or a List<MeshApiContentPart> (a message
// that includes an image) — MeshAPI's OpenAI-compatible wire format accepts both shapes for the
// same field, so this stays object? rather than a single strong type (mirrors OpenRouterMessage).
internal sealed record MeshApiMessage(
    string Role,
    object? Content,
    List<MeshApiToolCall>? ToolCalls,
    string? ToolCallId);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MeshApiTextPart), "text")]
[JsonDerivedType(typeof(MeshApiImagePart), "image_url")]
internal abstract record MeshApiContentPart;

internal sealed record MeshApiTextPart(string Text) : MeshApiContentPart;

internal sealed record MeshApiImagePart(MeshApiImageUrl ImageUrl) : MeshApiContentPart;

internal sealed record MeshApiImageUrl(string Url);

internal sealed record MeshApiToolCall(string Id, string Type, MeshApiFunctionCall Function);

internal sealed record MeshApiFunctionCall(string Name, string Arguments);

internal sealed record MeshApiTool(string Type, MeshApiFunctionSchema Function);

internal sealed record MeshApiFunctionSchema(string Name, string Description, JsonElement Parameters);

internal sealed record MeshApiModel(string Id, string? Name, [property: JsonPropertyName("context_length")] int? ContextLength);

internal sealed record MeshApiStreamChunk(List<MeshApiStreamChoice>? Choices, MeshApiUsage? Usage);

internal sealed record MeshApiStreamChoice(MeshApiDelta? Delta);

internal sealed record MeshApiResponsesRequest(
    string Model,
    List<MeshApiResponsesInputItem> Input,
    bool Stream,
    double? Temperature,
    int? MaxOutputTokens,
    List<MeshApiResponsesTool>? Tools);

// The Responses API's function-tool shape is flat (type/name/description/parameters as siblings),
// unlike /v1/chat/completions' MeshApiTool which nests name/description/parameters under a
// "function" object — sending the nested chat/completions shape here fails Mesh's schema
// validation (ResponsesFunctionTool.name "Field required") for every tool, which isn't the
// model_capability_not_supported error TryStreamViaResponsesApiAsync's fallback specifically
// detects, so it was silently falling back to chat/completions on every request instead.
internal sealed record MeshApiResponsesTool(string Type, string Name, string Description, JsonElement Parameters);

// Role is null for function_call/function_call_output items (the Responses API keys those by
// Type instead), and Content is either a plain string, a List<MeshApiContentPart> (image
// support, mirroring MeshApiMessage), or null for the function-call item shapes below.
internal sealed record MeshApiResponsesInputItem(
    string? Role,
    object? Content,
    string? Type = null,
    string? CallId = null,
    string? Name = null,
    string? Arguments = null,
    string? Output = null)
{
    public static MeshApiResponsesInputItem FunctionCall(string callId, string name, string arguments) =>
        new(Role: null, Content: null, Type: "function_call", CallId: callId, Name: name, Arguments: arguments);

    public static MeshApiResponsesInputItem FunctionCallOutput(string callId, string output) =>
        new(Role: null, Content: null, Type: "function_call_output", CallId: callId, Output: output);
}

internal sealed record MeshApiResponsesResponse(string? Status, List<MeshApiResponsesOutputItem>? Output, MeshApiResponsesUsage? Usage);

internal sealed record MeshApiResponsesOutputItem(
    string Type,
    string? Role,
    List<MeshApiResponsesContentPart>? Content,
    [property: JsonPropertyName("call_id")] string? CallId,
    string? Name,
    string? Arguments);

internal sealed record MeshApiResponsesContentPart(string Type, string? Text);

internal sealed record MeshApiResponsesUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);

internal sealed record MeshApiErrorResponse(MeshApiErrorDetail? Error);

internal sealed record MeshApiErrorDetail(string? Code, string? Message);

internal sealed record MeshApiDelta(string? Content, List<MeshApiToolCallDelta>? ToolCalls);

internal sealed record MeshApiToolCallDelta(int Index, string? Id, MeshApiFunctionCallDelta? Function);

internal sealed record MeshApiFunctionCallDelta(string? Name, string? Arguments);

internal sealed record MeshApiUsage(int PromptTokens, int CompletionTokens);
