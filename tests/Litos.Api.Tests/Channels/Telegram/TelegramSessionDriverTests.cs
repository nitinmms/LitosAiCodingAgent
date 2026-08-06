using System.Text.Json;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Api.Channels;
using Litos.Api.Channels.Telegram;
using Litos.Api.Tests.Fakes;
using Litos.Host;
using Litos.Tools.Shell;
using Litos.Tools.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;

namespace Litos.Api.Tests.Channels.Telegram;

public class TelegramSessionDriverTests
{
    private sealed class NoopSystemPromptProvider : ISystemPromptProvider
    {
        public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) => Task.FromResult<SystemPromptSections?>(null);
    }

    private sealed class FakeSkillDiscovery : ISkillDiscovery
    {
        public Task<IReadOnlyList<SkillMetadata>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SkillMetadata>>([]);
    }

    private static (TelegramSessionDriver Driver, FakeTelegramBotClient Bot, TelegramConfigStore Config) CreateDriver(
        IAudioTranscriber? transcriber = null)
    {
        var bot = new FakeTelegramBotClient();
        var provider = new FakeChatProvider();
        var factory = new FakeChatProviderFactory(provider);
        var toolRegistryFactory = new ToolRegistryFactory([], []);
        var transcriptStore = new FakeTranscriptStore();
        var loopFactory = new AgentLoopFactory(
            transcriptStore, new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings()));
        var config = new LitosConfig(
            DefaultProvider: "fake", DefaultModel: "fake-model", LastWorkingDirectory: null,
            ApiKeys: new Dictionary<string, string> { ["fake"] = "unused" });
        var worker = new AgentWorker(factory, loopFactory, toolRegistryFactory, transcriptStore, config);

        var telegramConfig = new TelegramConfigStore(Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}", "telegram.json"));
        telegramConfig.Update(cfg => cfg with
        {
            Enabled = true,
            LinkedChats = [new LinkedChat(42, "session-42", DateTimeOffset.UtcNow)],
        });

        var driver = new TelegramSessionDriver(
            bot, worker, transcriptStore, factory, new FakeSkillDiscovery(), toolRegistryFactory,
            new Compactor(new CompactionSettings()), new NullAttachmentConverter(), telegramConfig,
            new PendingApprovalStore(), NullLogger<TelegramSessionDriver>.Instance, transcriber);

        return (driver, bot, telegramConfig);
    }

    private sealed class NullAttachmentConverter : Litos.Tools.Attachments.IAttachmentConverter
    {
        public Task<Litos.Tools.Attachments.DocumentMarkdown> ConvertAsync(Litos.Tools.Attachments.AttachmentSource source, CancellationToken ct) =>
            Task.FromResult(new Litos.Tools.Attachments.DocumentMarkdown("doc", "content", []));
    }

    [Fact]
    public async Task HandleMessageAsync_UnlinkedChat_RepliesNotLinked_DoesNotStartATurn()
    {
        var (driver, bot, _) = CreateDriver();
        var message = new Message { Chat = new Chat { Id = 999 }, Text = "hello" };

        await driver.HandleMessageAsync(message, CancellationToken.None);

        Assert.Contains(bot.SentMessages, m => m.Contains("isn't linked"));
    }

    [Fact]
    public async Task HandleMessageAsync_VoiceMessage_NoTranscriberConfigured_FallsBackToPlaceholderText()
    {
        var (driver, bot, _) = CreateDriver(transcriber: null);
        var message = new Message
        {
            Chat = new Chat { Id = 42 },
            Voice = new Voice { FileId = "voice1", FileUniqueId = "u1", Duration = 3, MimeType = "audio/ogg" },
        };

        await driver.HandleMessageAsync(message, CancellationToken.None);

        // The turn ran (FakeChatProvider's default reply "ok" should have been sent) — the
        // important assertion is that transcription-unavailable didn't throw or block the turn.
        Assert.Contains(bot.SentMessages, m => m.Contains("ok"));
    }

    [Fact]
    public async Task HandleMessageAsync_VoiceMessage_TranscriberThrows_FallsBackGracefully()
    {
        var throwingTranscriber = new ThrowingAudioTranscriber();
        var (driver, bot, _) = CreateDriver(transcriber: throwingTranscriber);
        var message = new Message
        {
            Chat = new Chat { Id = 42 },
            Voice = new Voice { FileId = "voice1", FileUniqueId = "u1", Duration = 3, MimeType = "audio/ogg" },
        };

        await driver.HandleMessageAsync(message, CancellationToken.None);

        // Should not throw, and the turn should still have run to completion.
        Assert.Contains(bot.SentMessages, m => m.Contains("ok"));
    }

    private sealed class ThrowingAudioTranscriber : IAudioTranscriber
    {
        public Task<string> TranscribeAsync(Stream audio, string mimeType, CancellationToken ct) =>
            throw new InvalidOperationException("transcription boom");
    }

    [Fact]
    public async Task HandleMessageAsync_UnknownCommand_RepliesUnknownCommand()
    {
        var (driver, bot, _) = CreateDriver();
        var message = new Message { Chat = new Chat { Id = 42 }, Text = "/frobnicate" };

        await driver.HandleMessageAsync(message, CancellationToken.None);

        Assert.Contains(bot.SentMessages, m => m.Contains("Unknown command: /frobnicate"));
    }

    [Fact]
    public async Task HandleMessageAsync_NewCommand_RepointsSessionAndConfirms()
    {
        var (driver, bot, telegramConfig) = CreateDriver();
        var originalSessionId = telegramConfig.Current.LinkedChats[0].SessionId;
        var message = new Message { Chat = new Chat { Id = 42 }, Text = "/new" };

        await driver.HandleMessageAsync(message, CancellationToken.None);

        Assert.Contains(bot.SentMessages, m => m.Contains("Started a new session"));
        Assert.NotEqual(originalSessionId, telegramConfig.Current.LinkedChats[0].SessionId);
    }

    [Fact]
    public async Task HandleMessageAsync_BranchWithoutArgument_RepliesUsage()
    {
        var (driver, bot, _) = CreateDriver();
        var message = new Message { Chat = new Chat { Id = 42 }, Text = "/branch" };

        await driver.HandleMessageAsync(message, CancellationToken.None);

        Assert.Contains(bot.SentMessages, m => m.Contains("Usage: /branch"));
    }

    // Regression test for the deadlock TelegramBridge.HandleUpdateAsync used to be able to cause:
    // a gated tool call suspends HandleMessageAsync awaiting an Approve/Deny tap, and that tap only
    // ever arrives as its own CallbackQuery update. Previously TelegramBridge awaited each update's
    // handler to completion before dispatching the next one, so the callback query could never be
    // processed while the message handler was still suspended — a true deadlock, only ever broken
    // by PendingApprovalStore's 10-minute timeout. This test drives both updates concurrently (as
    // the fixed fire-and-forget TelegramBridge.HandleUpdateAsync now does) and asserts the turn
    // completes well within that timeout, proving the callback isn't starved by the in-flight
    // message handler.
    [Fact]
    public async Task HandleMessageAsync_GatedToolCall_ResolvesViaConcurrentCallbackQuery_DoesNotDeadlock()
    {
        var bot = new FakeTelegramBotClient();
        var provider = new FakeChatProvider();
        var toolArgs = JsonDocument.Parse("""{"command":"echo hi"}""").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "shell", toolArgs));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));
        var factory = new FakeChatProviderFactory(provider);

        var approvalStore = new PendingApprovalStore();
        var telegramConfig = new TelegramConfigStore(Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}", "telegram.json"));
        telegramConfig.Update(cfg => cfg with
        {
            Enabled = true,
            ToolPermissions = new Dictionary<string, ToolPermission> { ["shell"] = ToolPermission.Ask },
            LinkedChats = [new LinkedChat(42, "session-42", DateTimeOffset.UtcNow)],
        });
        var gate = new TelegramGatingApprovalGate(telegramConfig, approvalStore, bot, NullLogger<TelegramGatingApprovalGate>.Instance);
        var toolRegistryFactory = new ToolRegistryFactory([new ShellTool(gate)], []);

        var transcriptStore = new FakeTranscriptStore();
        var loopFactory = new AgentLoopFactory(
            transcriptStore, new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings()));
        var config = new LitosConfig(
            DefaultProvider: "fake", DefaultModel: "fake-model", LastWorkingDirectory: null,
            ApiKeys: new Dictionary<string, string> { ["fake"] = "unused" });
        var worker = new AgentWorker(factory, loopFactory, toolRegistryFactory, transcriptStore, config);

        var driver = new TelegramSessionDriver(
            bot, worker, transcriptStore, factory, new FakeSkillDiscovery(), toolRegistryFactory,
            new Compactor(new CompactionSettings()), new NullAttachmentConverter(), telegramConfig,
            approvalStore, NullLogger<TelegramSessionDriver>.Instance);

        var message = new Message { Chat = new Chat { Id = 42 }, Text = "run echo hi" };

        // Mirrors TelegramBridge.HandleUpdateAsync's fixed fire-and-forget dispatch: the message
        // handler (which will suspend mid-turn on the approval gate) and the callback query handler
        // (which resolves it) are started concurrently, neither awaited before the other starts.
        var messageTask = driver.HandleMessageAsync(message, CancellationToken.None);

        await WaitUntilAsync(() => approvalStore.List().Count > 0);
        var pending = Assert.Single(approvalStore.List());
        var query = new CallbackQuery
        {
            Id = "cb1",
            Data = $"approve:{pending.Id}",
            Message = new Message { Chat = new Chat { Id = 42 }, Id = 1, Text = "prompt" },
        };
        var callbackTask = driver.HandleCallbackQueryAsync(query, CancellationToken.None);

        var bothDone = Task.WhenAll(messageTask, callbackTask);
        var completed = await Task.WhenAny(bothDone, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == bothDone, "Turn did not complete — callback query was starved by the in-flight message handler (deadlock regression).");

        // The approval was actually granted (not auto-denied by PendingApprovalStore's timeout) —
        // confirmed by the chat message TelegramSessionDriver.HandleApprovalCallbackAsync edits in
        // once PendingApprovalStore.Resolve succeeds.
        Assert.Contains(bot.EditedMessages, m => m.Contains("Approved"));
    }

    // Regression test for the silent-steering UX gap this fire-and-forget dispatch exposed: two
    // Telegram messages for the same chat can now genuinely overlap (previously the old awaited
    // dispatch serialized them), and AgentWorker.StartOrSteerTurn folds the second message into the
    // still-running first turn (TurnOutcome.Steered) rather than starting an independent turn. Before
    // this fix, TelegramSessionDriver.RunTurnAsync's steered branch returned silently — a Telegram
    // user saw nothing acknowledging their second message until it eventually appeared appended to
    // the first turn's reply, indistinguishable from an unexplained duplicate answer.
    [Fact]
    public async Task HandleMessageAsync_SecondMessageArrivesWhileFirstTurnRunning_SendsSteeringAck()
    {
        var bot = new FakeTelegramBotClient();
        var provider = new FakeChatProvider();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task,
            new TextDelta("first "), new MessageCompleted(ChatMessage.Assistant([new TextBlock("first reply")]), new UsageInfo(1, 1)));
        var factory = new FakeChatProviderFactory(provider);

        var toolRegistryFactory = new ToolRegistryFactory([], []);
        var transcriptStore = new FakeTranscriptStore();
        var loopFactory = new AgentLoopFactory(
            transcriptStore, new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings()));
        var config = new LitosConfig(
            DefaultProvider: "fake", DefaultModel: "fake-model", LastWorkingDirectory: null,
            ApiKeys: new Dictionary<string, string> { ["fake"] = "unused" });
        var worker = new AgentWorker(factory, loopFactory, toolRegistryFactory, transcriptStore, config);

        var telegramConfig = new TelegramConfigStore(Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}", "telegram.json"));
        telegramConfig.Update(cfg => cfg with
        {
            Enabled = true,
            LinkedChats = [new LinkedChat(42, "session-42", DateTimeOffset.UtcNow)],
        });

        var driver = new TelegramSessionDriver(
            bot, worker, transcriptStore, factory, new FakeSkillDiscovery(), toolRegistryFactory,
            new Compactor(new CompactionSettings()), new NullAttachmentConverter(), telegramConfig,
            new PendingApprovalStore(), NullLogger<TelegramSessionDriver>.Instance);

        // Mirrors TelegramBridge.HandleUpdateAsync's fixed fire-and-forget dispatch: both messages
        // are started concurrently, neither awaited before the other starts.
        var firstTask = driver.HandleMessageAsync(new Message { Chat = new Chat { Id = 42 }, Text = "first message" }, CancellationToken.None);

        // AgentWorker.StartOrSteerTurn registers the turn in _activeTurns synchronously before the
        // provider call that's gated gets a chance to run, so no polling wait is needed here — the
        // second call is guaranteed to observe the first turn as already active.
        var secondTask = driver.HandleMessageAsync(new Message { Chat = new Chat { Id = 42 }, Text = "second message" }, CancellationToken.None);

        gate.SetResult();
        await Task.WhenAll(firstTask, secondTask);

        Assert.Contains(bot.SentMessages, m => m.Contains("fold that into what I'm already working on"));
        Assert.Contains(bot.SentMessages, m => m.Contains("first"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(5);
        }
    }
}
