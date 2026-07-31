using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Tools.Shell;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Lets the model deliver a file to the user as a Telegram document, e.g. a generated report or
/// screenshot. Telegram-only: reads ChannelContext.ChannelId (set by
/// TelegramSessionDriver.RunTurnAsync) to know which chat to upload into, so it's a no-op error
/// outside a Telegram-originated turn even though it's registered in the shared ToolRegistry —
/// same ChannelContext.Current check TelegramGatingApprovalGate already uses, applied here to
/// tool availability instead of approval routing.
/// </summary>
public sealed class SendFileTool(ITelegramBotClient bot, IToolApprovalGate approvalGate) : ITool
{
    // Telegram's own bot-upload cap (https://core.telegram.org/bots/api#senddocument).
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    public string Name => "send_file";

    public string Description => "Send a file from the workspace back to the user as a Telegram document. Only available in a Telegram chat.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Path to the file to send." },
            caption = new { type = "string", description = "Optional caption to send alongside the file." },
        },
        required = new[] { "path" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        if (ChannelContext.Current != "telegram" || ChannelContext.ChannelId is not { } chatIdText)
            return ToolResult.Error("send_file is only available in a Telegram chat.");

        var path = arguments.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("The 'path' argument is required.");

        var caption = arguments.TryGetProperty("caption", out var captionEl) ? captionEl.GetString() : null;

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxFileSizeBytes)
            return ToolResult.Error($"{path} is {fileInfo.Length / (1024 * 1024)}MB, which exceeds Telegram's 50MB bot-upload limit.");

        var decision = await approvalGate.RequestAsync(
            new ToolInvocationPreview(Name, $"Send {path} to the user", caption), ct);
        if (decision == ApprovalDecision.Deny)
            return ToolResult.Error("User denied sending this file.");

        await using var stream = File.OpenRead(path);
        var chatId = long.Parse(chatIdText);
        await bot.SendDocument(chatId, InputFile.FromStream(stream, Path.GetFileName(path)), caption: caption, cancellationToken: ct);

        return ToolResult.Ok($"Sent {path} to the user.");
    }
}
