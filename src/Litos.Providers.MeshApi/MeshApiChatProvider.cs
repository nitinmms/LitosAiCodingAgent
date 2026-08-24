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
            return new MeshApiMessage("assistant", text.Length > 0 ? text : null, toolCalls.Count > 0 ? toolCalls : null, null);
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

internal sealed record MeshApiDelta(string? Content, List<MeshApiToolCallDelta>? ToolCalls);

internal sealed record MeshApiToolCallDelta(int Index, string? Id, MeshApiFunctionCallDelta? Function);

internal sealed record MeshApiFunctionCallDelta(string? Name, string? Arguments);

internal sealed record MeshApiUsage(int PromptTokens, int CompletionTokens);
