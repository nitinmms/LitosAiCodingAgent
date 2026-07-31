using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using global::OpenAI;
using global::OpenAI.Responses;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using LM = Litos.Agent.Messages;

#pragma warning disable OPENAI001

namespace Litos.Providers.OpenAI;

public sealed class OpenAiChatProvider(OpenAIClient client, OpenRouterModelCatalog contextCatalog) : IChatProvider
{
    public string ProviderName => "openai";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var models = await client.GetOpenAIModelClient().GetModelsAsync(ct);
        var relevant = models.Value
            .Where(m => m.Id.Contains("gpt") || m.Id.Contains("o1") || m.Id.Contains("o3") || m.Id.Contains("o4"))
            .ToList();

        var result = new List<ModelInfo>(relevant.Count);
        foreach (var m in relevant)
        {
            var contextLength = await ModelContextResolver.ResolveAsync(contextCatalog, m.Id, nativeHint: null, ct);
            result.Add(new ModelInfo(m.Id, m.Id, IsDefault: false, ContextLength: contextLength));
        }
        return result;
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var responsesClient = client.GetResponsesClient();
        var options = new CreateResponseOptions
        {
            Model = request.Model,
        };
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            options.Instructions = request.SystemPrompt;
        if (request.Temperature is { } temperature)
            options.Temperature = (float)temperature;
        if (request.MaxOutputTokens is { } maxTokens)
            options.MaxOutputTokenCount = maxTokens;

        // Preserve transcript context, translating tool calls/results into the Responses API's
        // paired function_call / function_call_output items so call_ids line up across turns.
        foreach (var message in request.Messages)
            foreach (var item in SerializeMessageForResponsesInput(message))
                options.InputItems.Add(item);

        foreach (var tool in request.Tools)
            options.Tools.Add(ResponseTool.CreateFunctionTool(
                tool.Name,
                BinaryData.FromString(tool.ParameterSchema.GetRawText()),
                strictModeEnabled: null,
                functionDescription: tool.Description));

        var textBuilder = new StringBuilder();
        var contentBlocks = new List<LM.ContentBlock>();
        var response = (ResponseResult)await responsesClient.CreateResponseAsync(options, ct);

        foreach (var outputItem in response.OutputItems)
        {
            if (outputItem is MessageResponseItem messageItem)
            {
                foreach (ResponseContentPart part in messageItem.Content)
                {
                    if (string.IsNullOrEmpty(part.Text))
                        continue;

                    textBuilder.Append(part.Text);
                    yield return new TextDelta(part.Text);
                }
            }
            else if (outputItem is FunctionCallResponseItem functionCall)
            {
                var callId = functionCall.CallId ?? string.Empty;
                var functionName = functionCall.FunctionName ?? string.Empty;
                var argsText = functionCall.FunctionArguments?.ToString() ?? "{}";

                yield return new ToolCallStarted(callId, functionName);
                if (!string.IsNullOrWhiteSpace(argsText))
                    yield return new ToolCallArgsDelta(callId, argsText);
                var parsedArgs = ParseToolArguments(argsText);
                yield return new ToolCallCompleted(callId, functionName, parsedArgs);
                contentBlocks.Add(new LM.ToolUseBlock(callId, functionName, parsedArgs));
            }
        }

        if (textBuilder.Length > 0)
            contentBlocks.Add(new LM.TextBlock(textBuilder.ToString()));
        yield return new MessageCompleted(
            LM.ChatMessage.Assistant(contentBlocks),
            new UsageInfo(response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0));
    }

    private static JsonElement ParseToolArguments(string json)
    {
        return string.IsNullOrWhiteSpace(json)
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(json).RootElement;
    }

    private static IEnumerable<ResponseItem> SerializeMessageForResponsesInput(LM.ChatMessage message)
    {
        var hasImage = message.Content.Any(b => b is LM.ImageBlock);

        // Only assistant/user text ever needs the plain-string path; images require the
        // Responses API's typed content-part list (CreateInputImagePart) to actually reach
        // the model as vision input — stringifying an image (e.g. "[image bytes=...]") sends
        // the model a text description of metadata, not the picture, and it correctly reports
        // it cannot see anything.
        if (!hasImage)
        {
            var textLines = new List<string>();
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case LM.TextBlock t:
                        textLines.Add(t.Text);
                        break;
                    case LM.ToolUseBlock t:
                        yield return ResponseItem.CreateFunctionCallItem(
                            t.CallId, t.ToolName, BinaryData.FromString(t.Arguments.GetRawText()));
                        break;
                    case LM.ToolResultBlock t:
                        yield return ResponseItem.CreateFunctionCallOutputItem(t.CallId, t.Text);
                        break;
                    case LM.CompactionSummaryBlock c:
                        textLines.Add($"[compaction_summary tokens_before={c.TokensBefore}] {c.Summary}");
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported content block: {block.GetType().Name}");
                }
            }

            if (textLines.Count == 0)
                yield break;

            var text = string.Join("\n", textLines);
            yield return message.Role == LM.Role.Assistant
                ? ResponseItem.CreateAssistantMessageItem(text)
                : ResponseItem.CreateUserMessageItem(text);
            yield break;
        }

        var parts = new List<ResponseContentPart>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case LM.TextBlock t:
                    parts.Add(ResponseContentPart.CreateInputTextPart(t.Text));
                    break;
                case LM.ImageBlock i:
                    parts.Add(ResponseContentPart.CreateInputImagePart(BinaryData.FromBytes(i.Data, i.MediaType), imageDetailLevel: null));
                    break;
                case LM.ToolUseBlock t:
                    yield return ResponseItem.CreateFunctionCallItem(
                        t.CallId, t.ToolName, BinaryData.FromString(t.Arguments.GetRawText()));
                    break;
                case LM.ToolResultBlock t:
                    yield return ResponseItem.CreateFunctionCallOutputItem(t.CallId, t.Text);
                    break;
                case LM.CompactionSummaryBlock c:
                    parts.Add(ResponseContentPart.CreateInputTextPart($"[compaction_summary tokens_before={c.TokensBefore}] {c.Summary}"));
                    break;
                default:
                    throw new NotSupportedException($"Unsupported content block: {block.GetType().Name}");
            }
        }

        if (parts.Count == 0)
            yield break;

        // Image content parts are only valid on user messages in the Responses API; an
        // assistant turn never legitimately contains an ImageBlock (providers only ever
        // produce text/tool-use), so this path is reached exclusively for user messages.
        yield return ResponseItem.CreateUserMessageItem(parts);
    }
}

#pragma warning restore OPENAI001
