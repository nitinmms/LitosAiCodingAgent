using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// IChannelBridge implementation (ReadMe_TelegramIntegrationTool.md §6.1-§6.4) — owns the
/// Telegram.Bot long-polling receiver lifecycle, pairing-code linking flow, and routes every
/// inbound update to TelegramSessionDriver (or, for /start &lt;code&gt; and unlinked chats,
/// handles them directly here rather than forwarding). TelegramSessionDriver owns everything
/// downstream of "this update belongs to an already-linked chat."
/// </summary>
public sealed class TelegramBridge(
    ITelegramBotClient bot,
    TelegramConfigStore telegramConfig,
    TelegramPairing pairing,
    TelegramSessionDriver sessionDriver,
    ILogger<TelegramBridge> logger) : IChannelBridge
{
    private CancellationTokenSource? _receiving;
    private string? _botUsername;

    // Message updates for the same chat must be handled strictly in arrival order — e.g. /new
    // followed immediately by the next task message — otherwise the second update can read
    // telegramConfig.Current before /new's config write lands and gets attached to the stale
    // session (see ReadMe_TelegramIntegrationTool.md §6.3). Each chat's updates are chained onto
    // this per-chat tail task instead of being dispatched independently; different chats still
    // run fully concurrently since each has its own dictionary entry. CallbackQuery updates are
    // deliberately NOT routed through this queue — an Approve/Deny tap must reach
    // TelegramSessionDriver immediately even while that same chat's turn is running, otherwise a
    // turn awaiting its own unblocking tap would deadlock behind itself.
    private readonly ConcurrentDictionary<long, Task> _chatQueues = new();

    public string ChannelName => "telegram";

    public Task StartAsync(CancellationToken ct)
    {
        _receiving?.Cancel();
        _receiving = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var receiverOptions = new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] };
        bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, _receiving.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _receiving?.Cancel();
        _receiving = null;
        return Task.CompletedTask;
    }

    public async Task<PairingHandle> BeginPairingAsync(CancellationToken ct)
    {
        _botUsername ??= (await bot.GetMe(ct)).Username;
        var code = pairing.IssueCode();
        var deepLink = $"https://t.me/{_botUsername}?start={code}";
        var qrPng = QrCodeGenerator.GeneratePng(deepLink);
        return new PairingHandle(deepLink, qrPng);
    }

    // Telegram.Bot's receiver loop awaits this delegate to completion before polling for the next
    // update, so it must never block on an in-flight turn — an Approve/Deny button tap arrives as
    // its own CallbackQuery update, and a turn awaiting that tap (via TelegramGatingApprovalGate)
    // would otherwise deadlock the very update that could unblock it. Each update is instead
    // dispatched as background work. Message updates for the same chat are chained through
    // _chatQueues so they still run in arrival order (see field remarks); CallbackQuery updates
    // and messages for different chats run independently of one another.
    private Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message is { } message)
        {
            var chatId = message.Chat.Id;
            Task? tail = null;
            tail = _chatQueues.AddOrUpdate(
                chatId,
                _ => RunQueuedAsync(client, update, ct, chatId, () => tail!),
                (_, previous) => RunQueuedAsync(client, update, ct, chatId, () => tail!, previous));
            return Task.CompletedTask;
        }

        _ = HandleUpdateCoreAsync(client, update, ct).ContinueWith(
            t => logger.LogError(t.Exception, "Unhandled error processing update {UpdateId}", update.Id),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    private async Task RunQueuedAsync(
        ITelegramBotClient client, Update update, CancellationToken ct, long chatId, Func<Task> self, Task? previous = null)
    {
        if (previous is not null)
        {
            // The previous update for this chat may have faulted — its own continuation already
            // logged that, so just make sure we don't run before it's actually done.
            try { await previous; } catch { /* logged by the faulted update's own continuation */ }
        }

        try
        {
            await HandleUpdateCoreAsync(client, update, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing update {UpdateId}", update.Id);
        }
        finally
        {
            // Drop this chat's entry once its chain is empty again, so a bot that's been up for a
            // long time doesn't accumulate one dictionary entry per distinct chat forever. Only
            // remove the entry that's still literally this task — if a newer update has already
            // chained onto (and replaced) it, that one owns cleanup instead.
            _chatQueues.TryRemove(new KeyValuePair<long, Task>(chatId, self()));
        }
    }

    private async Task HandleUpdateCoreAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            await sessionDriver.HandleCallbackQueryAsync(callback, ct);
            return;
        }

        if (update.Message is not { } message)
            return;

        if (message.Text is { } text && text.StartsWith("/start ", StringComparison.Ordinal))
        {
            await HandlePairingAttemptAsync(message.Chat.Id, text["/start ".Length..].Trim(), ct);
            return;
        }

        var isLinked = telegramConfig.Current.LinkedChats.Any(c => c.ChatId == message.Chat.Id);
        if (!isLinked)
        {
            await bot.SendMessage(message.Chat.Id, "This chat isn't linked to Litos. Link it from the admin UI.", cancellationToken: ct);
            return;
        }

        await sessionDriver.HandleMessageAsync(message, ct);
    }

    private async Task HandlePairingAttemptAsync(long chatId, string code, CancellationToken ct)
    {
        if (!pairing.TryConsume(code))
        {
            logger.LogWarning("Rejected pairing attempt from chat {ChatId}: code invalid or expired", chatId);
            await bot.SendMessage(chatId, "That pairing code is invalid or has expired.", cancellationToken: ct);
            return;
        }

        var sessionId = Guid.NewGuid().ToString("n");
        telegramConfig.Update(cfg => cfg with
        {
            LinkedChats = [.. cfg.LinkedChats, new LinkedChat(chatId, sessionId, DateTimeOffset.UtcNow)],
        });

        logger.LogInformation("Linked Telegram chat {ChatId}", chatId);
        await bot.SendMessage(chatId, "Linked to Litos ✅", cancellationToken: ct);
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        logger.LogError(exception, "Telegram polling error ({Source})", source);
        return Task.CompletedTask;
    }
}
