using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using global::GenerativeAI;
using global::GenerativeAI.Core;
using global::GenerativeAI.Types;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using LM = Litos.Agent.Messages;

namespace Litos.Providers.Gemini;

public sealed class GeminiChatProvider(GoogleAi client, OpenRouterModelCatalog contextCatalog) : IChatProvider
{
    public string ProviderName => "gemini";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var response = await client.ModelClient.ListModelsAsync(pageSize: 100, pageToken: null, ct);
        var models = new List<ModelInfo>();
        foreach (var m in response.Models ?? [])
        {
            var contextLength = await ModelContextResolver.ResolveAsync(contextCatalog, m.Name, m.InputTokenLimit, ct);
            models.Add(new ModelInfo(m.Name, m.DisplayName ?? m.Name, IsDefault: false, ContextLength: contextLength));
        }
        return models;
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var model = client.CreateGenerativeModel(request.Model, systemInstruction: request.SystemPrompt);
        // LitosAiAgent owns tool execution (approval gate, ToolRegistry, transcript persistence),
        // so the SDK must not try to auto-invoke functions itself — it has no registered
        // IFunctionTool implementations and would throw "invalid function" for every call.
        model.FunctionCallingBehaviour = new FunctionCallingBehaviour { AutoCallFunction = false, AutoReplyFunction = false };

        var contentRequest = new GenerateContentRequest
        {
            Contents = [.. request.Messages.Select(ToGeminiContent)],
            Tools = request.Tools.Count == 0 ? null : [ToGeminiTool(request.Tools)],
            GenerationConfig = ToGenerationConfig(request),
        };

        var textBuilder = new System.Text.StringBuilder();
        var toolCalls = new List<(string CallId, string Name, JsonElement Args)>();
        var usage = new UsageInfo(0, 0);
        var callCounter = 0;

        await foreach (var chunk in model.StreamContentAsync(contentRequest, ct))
        {
            if (chunk.UsageMetadata is not null)
                usage = ToUsage(chunk.UsageMetadata);

            if (!string.IsNullOrEmpty(chunk.Text))
            {
                textBuilder.Append(chunk.Text);
                yield return new TextDelta(chunk.Text);
            }

            foreach (var part in chunk.Candidates?.SelectMany(c => c.Content?.Parts ?? []) ?? [])
            {
                if (part.FunctionCall is null)
                    continue;

                var callId = part.FunctionCall.Id ?? $"call_{++callCounter}";
                var argsJson = part.FunctionCall.Args?.ToJsonString() ?? "{}";
                var args = JsonDocument.Parse(argsJson).RootElement;
                toolCalls.Add((callId, part.FunctionCall.Name, args));
                yield return new ToolCallStarted(callId, part.FunctionCall.Name);
                yield return new ToolCallArgsDelta(callId, argsJson);
            }
        }

        foreach (var call in toolCalls)
            yield return new ToolCallCompleted(call.CallId, call.Name, call.Args);

        var contentBlocks = new List<LM.ContentBlock>();
        if (textBuilder.Length > 0)
            contentBlocks.Add(new LM.TextBlock(textBuilder.ToString()));
        foreach (var call in toolCalls)
            contentBlocks.Add(new LM.ToolUseBlock(call.CallId, call.Name, call.Args));

        yield return new MessageCompleted(LM.ChatMessage.Assistant(contentBlocks), usage);
    }

    /// <summary>
    /// The caller's output limit and temperature. Without a limit the model may write as much as
    /// it likes, which a caller that reserves for its reply (the factory's budget) cannot allow.
    /// </summary>
    private static GenerationConfig? ToGenerationConfig(ChatRequest request) =>
        request.MaxOutputTokens is null && request.Temperature is null
            ? null
            : new GenerationConfig { MaxOutputTokens = request.MaxOutputTokens, Temperature = request.Temperature };

    /// <summary>
    /// Gemini's counts in Litos' terms. Its prompt count includes the tokens served from cached
    /// content, which UsageInfo keeps apart (InputTokens is only what was not cached; see
    /// CLAUDE.md on token accounting). Thinking is billed as output but counted apart from the
    /// candidates, so it is added to the output and also reported as reasoning.
    /// </summary>
    internal static UsageInfo ToUsage(UsageMetadata metadata)
    {
        var cached = Count(metadata.CachedContentTokenCount);
        var thoughts = Count(metadata.ThoughtsTokenCount);
        return new UsageInfo(
            InputTokens: Math.Max(0, Count(metadata.PromptTokenCount) - cached),
            OutputTokens: Count(metadata.CandidatesTokenCount) + thoughts,
            CacheReadInputTokens: cached,
            ReasoningTokens: thoughts);
    }

    private static int Count(int? tokens) => tokens ?? 0;

    private static Content ToGeminiContent(LM.ChatMessage message)
    {
        var parts = new List<Part>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case LM.TextBlock t:
                    parts.Add(new Part { Text = t.Text });
                    break;
                case LM.ImageBlock i:
                    parts.Add(new Part { InlineData = new Blob { MimeType = i.MediaType, Data = Convert.ToBase64String(i.Data) } });
                    break;
                case LM.ToolUseBlock u:
                    parts.Add(new Part { FunctionCall = new FunctionCall(u.ToolName) { Id = u.CallId, Args = JsonNode.Parse(u.Arguments.GetRawText()) } });
                    break;
                case LM.ToolResultBlock r:
                    parts.Add(new Part
                    {
                        FunctionResponse = new FunctionResponse(r.CallId)
                        {
                            Id = r.CallId,
                            Response = JsonNode.Parse(JsonSerializer.Serialize(new { result = r.Text, isError = r.IsError })),
                        },
                    });
                    break;
                case LM.CompactionSummaryBlock c:
                    parts.Add(new Part { Text = c.Summary });
                    break;
                default:
                    throw new NotSupportedException($"Unsupported content block: {block.GetType().Name}");
            }
        }

        return new Content(parts, message.Role == LM.Role.Assistant ? "model" : "user");
    }

    private static Tool ToGeminiTool(IReadOnlyList<ToolSchema> tools) => new()
    {
        FunctionDeclarations = [.. tools.Select(t => new FunctionDeclaration
        {
            Name = t.Name,
            Description = t.Description,
            ParametersJsonSchema = JsonNode.Parse(t.ParameterSchema.GetRawText()),
        })],
    };
}
