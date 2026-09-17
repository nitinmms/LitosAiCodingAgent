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

public sealed class AnthropicChatProvider(AnthropicClient client, OpenRouterModelCatalog contextCatalog) : IChatProvider
{
    public string ProviderName => "anthropic";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var list = await client.Models.ListModelsAsync(null, null, 100, ct);
        var models = new List<ModelInfo>(list.Models.Count);
        foreach (var m in list.Models)
        {
            var contextLength = await ModelContextResolver.ResolveAsync(contextCatalog, m.Id, nativeHint: null, ct);
            models.Add(new ModelInfo(m.Id, m.DisplayName, IsDefault: false, ContextLength: contextLength));
        }
        return models;
    }

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
