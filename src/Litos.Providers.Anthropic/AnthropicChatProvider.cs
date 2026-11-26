using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using global::Anthropic.SDK;
using global::Anthropic.SDK.Common;
using global::Anthropic.SDK.Messaging;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using LM = Litos.Agent.Messages;

namespace Litos.Providers.Anthropic;

/// <param name="modelsHttpClient">Sends ListModelsAsync's direct /v1/models request (the SDK keeps
/// its own HttpClient private). Null uses one shared client; tests pass their fake handler's.</param>
public sealed class AnthropicChatProvider(AnthropicClient client, OpenRouterModelCatalog contextCatalog, HttpClient? modelsHttpClient = null) : IChatProvider
{
    private static readonly HttpClient SharedModelsHttpClient = new();

    public string ProviderName => "anthropic";

    /// <summary>
    /// Calls GET /v1/models directly rather than through client.Models: Anthropic.SDK 5.10.0's
    /// model type has no max_input_tokens, and that field is Anthropic's own statement of each
    /// model's window. It is passed as the native hint, the way Gemini's inputTokenLimit is, so it
    /// wins over OpenRouter's catalog. That catalog matches few Anthropic ids (it writes
    /// "claude-opus-5.5" where Anthropic writes "claude-opus-5-5", and has no dated ids), and is
    /// not always right for the direct API either: it lists claude-sonnet-4-5 at 1M where
    /// Anthropic reports 200K. Same URL, key and version header as the SDK's own calls.
    /// </summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var url = string.Format(client.ApiUrlFormat, client.ApiVersion, "models") + "?limit=100";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-api-key", client.Auth.ApiKey);
        request.Headers.Add("anthropic-version", client.AnthropicVersion);
        using var response = await (modelsHttpClient ?? SharedModelsHttpClient).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var list = await JsonSerializer.DeserializeAsync<AnthropicModelList>(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var models = new List<ModelInfo>(list?.Data.Count ?? 0);
        foreach (var m in list?.Data ?? [])
        {
            var contextLength = await ModelContextResolver.ResolveAsync(contextCatalog, m.Id, m.MaxInputTokens, ct);
            models.Add(new ModelInfo(m.Id, m.DisplayName ?? m.Id, IsDefault: false, ContextLength: contextLength));
        }
        return models;
    }

    private sealed record AnthropicModelList([property: System.Text.Json.Serialization.JsonPropertyName("data")] List<AnthropicModel> Data);

    private sealed record AnthropicModel(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("display_name")] string? DisplayName,
        [property: System.Text.Json.Serialization.JsonPropertyName("max_input_tokens")] int? MaxInputTokens);

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = new MessageParameters
        {
            Model = request.Model,
            Messages = [.. request.Messages.Select(ToAnthropicMessage)],
            MaxTokens = request.MaxOutputTokens ?? 4096,
            Temperature = request.Temperature.HasValue ? (decimal)request.Temperature.Value : null,
            Stream = true,
            Tools = request.Tools.Count == 0 ? null : [.. request.Tools.Select(ToAnthropicTool)],
            System = request.SystemPrompt is null ? null : [new SystemMessage(request.SystemPrompt)],
            // Caches the tools + system-prompt prefix, which is byte-identical on every request of
            // every turn in a session (AgentLoop builds the system prompt once per turn and passes
            // the same tool schemas each round) and — importantly — is untouched by compaction:
            // Transcript.ApplyCompaction only rewrites messages, so a cut invalidates the message
            // suffix but never this prefix. Anthropic caching is opt-in; without this, every round
            // of every turn re-paid full input price for an identical prefix. OpenAI and Gemini
            // cache equivalent prefixes automatically, so this brings the Anthropic path in line
            // with what those providers were already doing for free.
            //
            // Prefixes below the model-specific minimum (512-4096 tokens) are silently not cached
            // rather than erroring, which is harmless: usage then simply reports no cache activity.
            PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
        };
        MarkConversationCacheBreakpoint(parameters.Messages);

        var textBuilder = new StringBuilder();
        var toolCallNames = new Dictionary<string, string>();
        var toolCallJson = new Dictionary<string, StringBuilder>();
        var toolCallOrder = new List<string>();
        Usage? lastUsage = null;

        await foreach (var chunk in client.Messages.StreamClaudeMessageAsync(parameters, ct))
        {
            if (chunk.Usage is not null)
                lastUsage = chunk.Usage;

            if (chunk.ContentBlock is { Type: nameof(ContentType.tool_use) } startBlock && startBlock.Id is not null)
            {
                var toolName = startBlock.Name ?? string.Empty;
                toolCallNames[startBlock.Id] = toolName;
                toolCallJson[startBlock.Id] = new StringBuilder();
                toolCallOrder.Add(startBlock.Id);
                yield return new ToolCallStarted(startBlock.Id, toolName);
                continue;
            }

            if (chunk.Delta is null)
                continue;

            switch (chunk.Delta.Type)
            {
                case "text_delta" when chunk.Delta.Text is not null:
                    textBuilder.Append(chunk.Delta.Text);
                    yield return new TextDelta(chunk.Delta.Text);
                    break;

                case "input_json_delta" when chunk.Delta.PartialJson is not null:
                    var callId = toolCallOrder[^1];
                    toolCallJson[callId].Append(chunk.Delta.PartialJson);
                    yield return new ToolCallArgsDelta(callId, chunk.Delta.PartialJson);
                    break;
            }
        }

        foreach (var callId in toolCallOrder)
            yield return new ToolCallCompleted(callId, toolCallNames[callId], ParseToolArguments(toolCallJson[callId]));

        var contentBlocks = new List<LM.ContentBlock>();
        if (textBuilder.Length > 0)
            contentBlocks.Add(new LM.TextBlock(textBuilder.ToString()));
        foreach (var callId in toolCallOrder)
            contentBlocks.Add(new LM.ToolUseBlock(callId, toolCallNames[callId], ParseToolArguments(toolCallJson[callId])));

        // input_tokens, cache_creation_input_tokens and cache_read_input_tokens are mutually
        // exclusive counts (input_tokens covers only what follows the last cache breakpoint), so
        // the cached portions must be carried separately rather than dropped — see UsageInfo, and
        // ContextAccountant/CompactionPlanner which need the total to measure window occupancy.
        var usage = new UsageInfo(
            lastUsage?.InputTokens ?? 0,
            lastUsage?.OutputTokens ?? 0,
            lastUsage?.CacheCreationInputTokens ?? 0,
            lastUsage?.CacheReadInputTokens ?? 0);
        yield return new MessageCompleted(LM.ChatMessage.Assistant(contentBlocks), usage);
    }

    // AutomaticToolsAndSystem only marks the tools and system prompt, so on its own the message
    // history — which on a tool-heavy turn soon outgrows that prefix, and is re-sent in full every
    // round — was billed as uncached input every round (observed live: ~60% of factory input
    // uncached). A third breakpoint on the final block caches the whole conversation so far; the
    // next round's request extends it by one exchange, and the API's breakpoint lookback finds
    // this round's entry and serves everything up to it as a cache read. Compaction rewrites the
    // messages and so costs one fresh write, which is unavoidable. The SDK leaves a manually-set
    // block CacheControl alone in this mode, and three breakpoints stay under the API's limit of 4.
    private static void MarkConversationCacheBreakpoint(List<Message> messages)
    {
        if (messages.Count == 0 || messages[^1].Content is not { Count: > 0 } content)
            return;
        content[^1].CacheControl = new CacheControl { Type = CacheControlType.ephemeral };
    }

    private static JsonElement ParseToolArguments(StringBuilder json)
    {
        var text = json.ToString();
        return string.IsNullOrWhiteSpace(text)
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(text).RootElement;
    }

    private static Message ToAnthropicMessage(LM.ChatMessage message)
    {
        var content = new List<ContentBase>();
        foreach (var block in message.Content)
        {
            content.Add(block switch
            {
                LM.TextBlock t => new TextContent { Text = t.Text },
                LM.ImageBlock i => new ImageContent
                {
                    Source = new ImageSource { MediaType = i.MediaType, Data = Convert.ToBase64String(i.Data) },
                },
                LM.ToolResultBlock r => new ToolResultContent
                {
                    ToolUseId = r.CallId,
                    IsError = r.IsError,
                    Content = [new TextContent { Text = r.Text }],
                },
                LM.ToolUseBlock u => new ToolUseContent
                {
                    Id = u.CallId,
                    Name = u.ToolName,
                    Input = JsonNode.Parse(u.Arguments.GetRawText()),
                },
                LM.CompactionSummaryBlock c => new TextContent { Text = c.Summary },
                _ => throw new NotSupportedException($"Unsupported content block: {block.GetType().Name}"),
            });
        }

        return new Message
        {
            Role = message.Role == LM.Role.Assistant ? RoleType.Assistant : RoleType.User,
            Content = content,
        };
    }

    private static global::Anthropic.SDK.Common.Tool ToAnthropicTool(ToolSchema schema) =>
        new(new Function(schema.Name, schema.Description, JsonNode.Parse(schema.ParameterSchema.GetRawText())));
}
