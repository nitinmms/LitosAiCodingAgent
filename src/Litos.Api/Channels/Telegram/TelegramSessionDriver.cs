using System.Collections.Concurrent;
using System.Threading.Channels;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Tools.Attachments;
using Litos.Tools.Shell;
using Litos.Tools.Skills;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Turn-driving heart of the bridge (ReadMe_TelegramIntegrationTool.md §6.3) — takes an inbound
/// Telegram Message/CallbackQuery already known to belong to a linked chat (TelegramBridge does
/// the pairing/unlinked-chat filtering before anything reaches here), builds turn content,
/// drives AgentWorker exactly the way TurnsEndpoints does for the HTTP API, and translates the
/// resulting AgentEvent stream back into Telegram sends.
/// </summary>
public sealed class TelegramSessionDriver(
    ITelegramBotClient bot,
    AgentWorker worker,
    ITranscriptStore transcriptStore,
    IChatProviderFactory providerFactory,
    ISkillDiscovery skillDiscovery,
    ToolRegistryFactory toolRegistryFactory,
    Compactor compactor,
    IAttachmentConverter attachmentConverter,
    TelegramConfigStore telegramConfig,
    PendingApprovalStore approvalStore,
    ILogger<TelegramSessionDriver> logger,
    IAudioTranscriber? audioTranscriber = null)
{
    // /skill <name> stages content for the NEXT message rather than acting immediately — mirrors
    // Litos.Gui/Console's own "attach then send" two-step flow, since a chat command has no
    // return value of its own to fold into other than a confirmation reply.
    private readonly ConcurrentDictionary<long, string> _pendingSkills = new();

    public async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var linkedChat = telegramConfig.Current.LinkedChats.FirstOrDefault(c => c.ChatId == chatId);
        if (linkedChat is null)
        {
            await bot.SendMessage(chatId, "This chat isn't linked. Link it from the Litos admin UI first.", cancellationToken: ct);
            return;
        }

        if (message.Text is { } text && TelegramCommand.Parse(text) is { } command)
        {
            await HandleCommandAsync(linkedChat, command, ct);
            return;
        }

        var content = await BuildTurnContentAsync(chatId, message, ct);
        if (content is null)
            return; // unsupported message kind — nothing sendable to the model

        await RunTurnAsync(linkedChat, content, ct);
    }

    public async Task HandleCallbackQueryAsync(CallbackQuery query, CancellationToken ct)
    {
        if (query.Message is null || query.Data is null)
            return;

        var chatId = query.Message.Chat.Id;
        var linkedChat = telegramConfig.Current.LinkedChats.FirstOrDefault(c => c.ChatId == chatId);
        if (linkedChat is null)
        {
            await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
            return;
        }

        // Two kinds of inline keyboard originate from this driver: /resume's session picker
        // (bare sessionId as CallbackData) and TelegramGatingApprovalGate's Approve/Deny buttons
        // (prefixed "approve:{approvalId}"/"deny:{approvalId}") — disambiguated by prefix since
        // both share the same CallbackQuery entry point.
        if (query.Data.StartsWith("approve:", StringComparison.Ordinal) || query.Data.StartsWith("deny:", StringComparison.Ordinal))
        {
            await HandleApprovalCallbackAsync(query, chatId, ct);
            return;
        }

        var newSessionId = query.Data;
        telegramConfig.Update(cfg => cfg with
        {
            LinkedChats = [.. cfg.LinkedChats.Select(c => c.ChatId == chatId ? c with { SessionId = newSessionId } : c)],
        });

        await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        await bot.SendMessage(chatId, $"Resumed session {newSessionId}.", cancellationToken: ct);
    }

    private async Task HandleApprovalCallbackAsync(CallbackQuery query, long chatId, CancellationToken ct)
    {
        var data = query.Data!;
        var isApprove = data.StartsWith("approve:", StringComparison.Ordinal);
        var idText = data[(data.IndexOf(':') + 1)..];

        if (!Guid.TryParse(idText, out var approvalId))
        {
            await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
            return;
        }

        var resolved = approvalStore.Resolve(approvalId, isApprove ? ApprovalDecision.Approve : ApprovalDecision.Deny);
        await bot.AnswerCallbackQuery(query.Id, resolved ? null : "Already resolved or expired.", cancellationToken: ct);

        if (resolved && query.Message is not null)
        {
            await bot.EditMessageText(chatId, query.Message.MessageId,
                $"{query.Message.Text}\n\n{(isApprove ? "✅ Approved" : "❌ Denied")}", cancellationToken: ct);
        }
    }

    private async Task<IReadOnlyList<ContentBlock>?> BuildTurnContentAsync(long chatId, Message message, CancellationToken ct)
    {
        if (message.Photo is { Length: > 0 } photos)
        {
            var largest = photos[^1];
            using var stream = new MemoryStream();
            await bot.GetInfoAndDownloadFile(largest.FileId, stream, ct);
            var caption = string.IsNullOrEmpty(message.Caption) ? "(photo attached)" : message.Caption;
            return [new TextBlock(caption), new ImageBlock("image/jpeg", stream.ToArray())];
        }

        if (message.Document is { } document)
        {
            using var stream = new MemoryStream();
            await bot.GetInfoAndDownloadFile(document.FileId, stream, ct);
            stream.Position = 0;

            var source = new StreamSource(stream, Path.GetExtension(document.FileName), document.MimeType);
            var converted = await attachmentConverter.ConvertAsync(source, ct);
            var wrapped = UntrustedContent.Wrap($"telegram_attachment:{document.FileId}", converted.Markdown);
            var text = $"### Attachment: {converted.Title}\n\n{wrapped}";

            var skillText = ConsumePendingSkill(chatId);
            var caption = message.Caption ?? "";
            return [new TextBlock(skillText + text + (caption.Length > 0 ? "\n\n" + caption : ""))];
        }

        if (message.Voice is { } voice)
        {
            using var stream = new MemoryStream();
            await bot.GetInfoAndDownloadFile(voice.FileId, stream, ct);
            stream.Position = 0;

            string transcriptText;
            if (audioTranscriber is null)
            {
                transcriptText = "[voice message received, transcription failed]";
            }
            else
            {
                try
                {
                    transcriptText = await audioTranscriber.TranscribeAsync(stream, voice.MimeType ?? "audio/ogg", ct);
                }
                catch (Exception)
                {
                    transcriptText = "[voice message received, transcription failed]";
                }
            }

            return [new TextBlock(ConsumePendingSkill(chatId) + $"### Voice message transcript:\n\n{transcriptText}")];
        }

        if (!string.IsNullOrEmpty(message.Text))
            return [new TextBlock(ConsumePendingSkill(chatId) + message.Text)];

        return null;
    }

    private string ConsumePendingSkill(long chatId) =>
        _pendingSkills.TryRemove(chatId, out var skillText) ? skillText : "";

    private async Task RunTurnAsync(LinkedChat linkedChat, IReadOnlyList<ContentBlock> content, CancellationToken ct)
    {
        await bot.SendChatAction(linkedChat.ChatId, ChatAction.Typing, cancellationToken: ct);

        ChannelReader<AgentEvent>? events = null;
        TurnOutcome outcome = default;
        await ChannelContext.RunAsAsync("telegram", linkedChat.ChatId.ToString(), () =>
        {
            events = worker.StartOrSteerTurn(SessionOwner.Telegram, linkedChat.SessionId, content, ct, out outcome);
            return Task.CompletedTask;
        });

        if (outcome != TurnOutcome.Started || events is null)
        {
            // Steered into an already-running turn (AgentWorker.StartOrSteerTurn) — the turn's own
            // still-open StreamRepliesAsync call will eventually deliver the reply once the running
            // round finishes and AgentLoop.TryDeliverFollowUpAsync picks this message up. Without
            // this ack, a Telegram user just sees an unexplained second answer arrive later with no
            // indication their message was received rather than dropped or duplicated.
            await bot.SendMessage(linkedChat.ChatId,
                "⏳ Got it — I'll fold that into what I'm already working on.", cancellationToken: ct);
            return;
        }

        await StreamRepliesAsync(linkedChat.ChatId, events, ct);
    }

    private async Task StreamRepliesAsync(long chatId, ChannelReader<AgentEvent> events, CancellationToken ct)
    {
        var buffer = new System.Text.StringBuilder();
        var lastTypingAt = DateTimeOffset.UtcNow;

        async Task FlushAsync()
        {
            if (buffer.Length == 0)
                return;
            foreach (var chunk in TelegramMessageChunker.Chunk(buffer.ToString()))
                await bot.SendMessage(chatId, chunk, cancellationToken: ct);
            buffer.Clear();
        }

        await foreach (var evt in events.ReadAllAsync(ct))
        {
            if (DateTimeOffset.UtcNow - lastTypingAt > TimeSpan.FromSeconds(4))
            {
                await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                lastTypingAt = DateTimeOffset.UtcNow;
            }

            switch (evt)
            {
                case TextDelta delta:
                    buffer.Append(delta.Text);
                    break;
                case MessageCompleted:
                    await FlushAsync();
                    break;
                case ToolCallCompleted tool:
                    await FlushAsync();
                    await bot.SendMessage(chatId, ToolCallSummary.Describe(tool.ToolName, tool.Arguments), cancellationToken: ct);
                    break;
                case ToolCallSkipped skipped:
                    await bot.SendMessage(chatId, $"⏩ skipped: {skipped.Reason}", cancellationToken: ct);
                    break;
                case ErrorOccurred error:
                    await FlushAsync();
                    await bot.SendMessage(chatId, $"⚠️ {error.Exception.Message}", cancellationToken: ct);
                    break;
                case CompactionOccurred:
                    await bot.SendMessage(chatId, "⚏ context compacted", cancellationToken: ct);
                    break;
            }
        }

        await FlushAsync();
    }

    private async Task HandleCommandAsync(LinkedChat linkedChat, TelegramCommand command, CancellationToken ct)
    {
        var chatId = linkedChat.ChatId;
        switch (command.Name)
        {
            case "/new":
            {
                var newSessionId = Guid.NewGuid().ToString("n");
                telegramConfig.Update(cfg => cfg with
                {
                    LinkedChats = [.. cfg.LinkedChats.Select(c => c.ChatId == chatId ? c with { SessionId = newSessionId } : c)],
                });
                await bot.SendMessage(chatId, "Started a new session.", cancellationToken: ct);
                return;
            }

            case "/resume":
            {
                var sessions = await transcriptStore.ListSessionsAsync(SessionOwner.Telegram, ct);
                if (sessions.Count == 0)
                {
                    await bot.SendMessage(chatId, "No previous sessions to resume.", cancellationToken: ct);
                    return;
                }

                var buttons = sessions
                    .OrderByDescending(s => s.LastUpdatedAt)
                    .Take(10)
                    .Select(s =>
                    {
                        var preview = s.FirstUserMessagePreview is { Length: > 0 } p
                            ? p[..Math.Min(p.Length, 40)]
                            : s.SessionId;
                        var label = $"{s.LastUpdatedAt:MM/dd} — {preview}";
                        return new[] { InlineKeyboardButton.WithCallbackData(label, s.SessionId) };
                    });

                await bot.SendMessage(chatId, "Pick a session to resume:",
                    replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
                return;
            }

            case "/branch":
            {
                if (command.Argument is null || !int.TryParse(command.Argument, out var uptoEntryIndex))
                {
                    await bot.SendMessage(chatId,
                        "Usage: /branch <messageIndex> — branches the current session, keeping the first N transcript entries.",
                        cancellationToken: ct);
                    return;
                }

                var newSessionId = await transcriptStore.BranchAsync(SessionOwner.Telegram, linkedChat.SessionId, uptoEntryIndex, ct);
                telegramConfig.Update(cfg => cfg with
                {
                    LinkedChats = [.. cfg.LinkedChats.Select(c => c.ChatId == chatId ? c with { SessionId = newSessionId } : c)],
                });
                await bot.SendMessage(chatId, $"Branched into new session {newSessionId}.", cancellationToken: ct);
                return;
            }

            case "/skills":
            {
                var skills = await skillDiscovery.DiscoverAsync(ct);
                if (skills.Count == 0)
                {
                    await bot.SendMessage(chatId, "No skills found.", cancellationToken: ct);
                    return;
                }

                var list = string.Join("\n", skills.Select(s => $"{s.Name} — {s.Description}"));
                await bot.SendMessage(chatId, list, cancellationToken: ct);
                return;
            }

            case "/skill":
            {
                if (command.Argument is null)
                {
                    await bot.SendMessage(chatId, "Usage: /skill <name>", cancellationToken: ct);
                    return;
                }

                var argJson = System.Text.Json.JsonSerializer.SerializeToElement(new { name = command.Argument });
                var result = await toolRegistryFactory.Create().Resolve("skill").InvokeAsync(argJson, ct);
                if (result.IsError)
                {
                    await bot.SendMessage(chatId, result.Text, cancellationToken: ct);
                    return;
                }

                _pendingSkills[chatId] = $"### Skill: {command.Argument}\n\n{result.Text}\n\n";
                await bot.SendMessage(chatId, $"Loaded skill '{command.Argument}' — it'll be applied to your next message.", cancellationToken: ct);
                return;
            }

            case "/compact":
            {
                var transcript = await Transcript.LoadAsync(transcriptStore, SessionOwner.Telegram, linkedChat.SessionId, ct);
                var provider = providerFactory.Resolve(worker.ProviderName);
                var model = worker.Model ?? "";
                var compacted = await compactor.ForceCompactAsync(transcript, provider, model, ct);
                if (!compacted)
                {
                    await bot.SendMessage(chatId, "Nothing to compact yet.", cancellationToken: ct);
                    return;
                }

                await transcriptStore.AppendAsync(SessionOwner.Telegram, linkedChat.SessionId, TranscriptEntry.FromMessage(transcript.Messages[0]), ct);
                await bot.SendMessage(chatId, "— context compacted —", cancellationToken: ct);
                return;
            }

            default:
                await bot.SendMessage(chatId, $"Unknown command: {command.Name}", cancellationToken: ct);
                return;
        }
    }
}
