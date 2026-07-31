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
        };

        var textBuilder = new System.Text.StringBuilder();
        var toolCalls = new List<(string CallId, string Name, JsonElement Args)>();
        var promptTokens = 0;
        var candidateTokens = 0;
        var callCounter = 0;

        await foreach (var chunk in model.StreamContentAsync(contentRequest, ct))
        {
            if (chunk.UsageMetadata is not null)
            {
                promptTokens = chunk.UsageMetadata.PromptTokenCount;
                candidateTokens = chunk.UsageMetadata.CandidatesTokenCount;
            }

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

        yield return new MessageCompleted(LM.ChatMessage.Assistant(contentBlocks), new UsageInfo(promptTokens, candidateTokens));
    }

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
