using Litos.Tools.Shell;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Per-tool trust-level gate (TelegramConfig.ToolPermissions — Deny/Ask/Full, mirroring
/// OpenClaw's tools.exec.security modes) for Telegram-originated turns. Read-only tools
/// (read_file, list_directory, search_code, web_search) never call IToolApprovalGate at all —
/// confirmed by inspection, only ShellTool/WriteFileTool/EditFileTool do — so gating at this
/// single seam covers every tool that needs it, with no change to Litos.Agent/Litos.Tools.
///
/// A non-Telegram turn (ChannelContext.Current is null — the HTTP API's own session) always
/// auto-approves — there's no other approval UI for it (the browser /approvals page was removed
/// once Telegram's in-chat Approve/Deny buttons became the only gating surface this deployment
/// needs), mirroring Litos.Gui's own spike-only GuiApprovalGate.
///
/// Ask mode seeds an inline Approve/Deny keyboard directly in the originating chat
/// (docs.openclaw.ai/channels/telegram's validated "inline approval buttons" pattern), resolved
/// via PendingApprovalStore — see TelegramSessionDriver.HandleCallbackQueryAsync for the other
/// half of this flow. Bounded by PendingApprovalStore's own timeout so a Telegram `write_file`
/// call can never sit forever with nobody watching (the incident that motivated that timeout).
/// </summary>
public sealed class TelegramGatingApprovalGate(
    TelegramConfigStore telegramConfig, PendingApprovalStore approvalStore, ITelegramBotClient bot,
    ILogger<TelegramGatingApprovalGate> logger)
    : IChannelApprovalGate
{
    public async Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        if (ChannelContext.Current != "telegram")
            return ApprovalDecision.Approve;

        switch (telegramConfig.Current.PermissionFor(preview.ToolName))
        {
            case ToolPermission.Deny:
                return ApprovalDecision.Deny;

            case ToolPermission.Full:
                return ApprovalDecision.Approve;

            default: // Ask
                return await RequestViaChannelAsync(preview, ct);
        }
    }

    /// <summary>
    /// The Ask-mode chat-button flow on its own, without this gate's own TelegramConfig
    /// permission lookup — used by McpAwareApprovalGate, which has already decided a given MCP
    /// tool call needs Ask-mode via McpConfigStore (a separate permission map) and just needs
    /// somewhere to send the resulting prompt. Mirrors RequestAsync's own "auto-approve outside a
    /// Telegram-originated turn" rule so MCP tools behave identically to native tools: gated via
    /// chat buttons on a Telegram turn, ungated (not panel-routed) on any other turn as long as a
    /// Telegram bridge exists at all.
    /// </summary>
    public async Task<ApprovalDecision> RequestViaChannelAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        if (ChannelContext.Current != "telegram")
            return ApprovalDecision.Approve;

        return await RequestViaChatAsync(preview, ct);
    }

    private async Task<ApprovalDecision> RequestViaChatAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        // ChannelId is set by TelegramSessionDriver.RunTurnAsync's ChannelContext.RunAsAsync call
        // for the exact duration of the turn this tool call belongs to — always present here
        // since we already confirmed ChannelContext.Current == "telegram" above.
        var chatId = long.Parse(ChannelContext.ChannelId!);

        var (pending, approvalTask) = approvalStore.AddPending(preview);

        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Approve", $"approve:{pending.Id}"),
                InlineKeyboardButton.WithCallbackData("❌ Deny", $"deny:{pending.Id}"),
            ],
        ]);
        var text = preview.DiffOrCommand is { Length: > 0 } detail
            ? $"🔧 {preview.Summary}\n\n{TelegramMessageChunker.Chunk(detail).First()}"
            : $"🔧 {preview.Summary}";
        var sent = await bot.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: ct);

        // TelegramSessionDriver.HandleApprovalCallbackAsync already edits the message for a human
        // Approve/Deny tap — this handler only needs to cover the timeout path, where nobody ever
        // taps a button and PendingApprovalStore resolves it unattended (see PendingApprovalStore.
        // TimeoutAfterAsync). Filtered by id and unsubscribed via the `finally` below so it doesn't
        // leak a handler per approval or fire for someone else's timeout.
        void OnTimedOut(Guid id)
        {
            if (id != pending.Id)
                return;
            _ = bot.EditMessageText(chatId, sent.MessageId, $"{text}\n\n⏱ Timed out — no response in time, denied.");
        }

        approvalStore.TimedOut += OnTimedOut;
        try
        {
            return await approvalTask;
        }
        finally
        {
            approvalStore.TimedOut -= OnTimedOut;
        }
    }
}
