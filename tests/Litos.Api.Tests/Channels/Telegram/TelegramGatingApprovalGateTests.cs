using Litos.Api.Channels;
using Litos.Api.Channels.Telegram;
using Litos.Api.Tests.Fakes;
using Litos.Tools.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.Api.Tests.Channels.Telegram;

public class TelegramGatingApprovalGateTests
{
    private const long ChatId = 555;
    private static readonly ILogger<TelegramGatingApprovalGate> Logger = NullLogger<TelegramGatingApprovalGate>.Instance;

    private static TelegramConfigStore StoreWithPermissions(params (string Tool, ToolPermission Permission)[] permissions)
    {
        var store = new TelegramConfigStore(Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}", "telegram.json"));
        store.Update(_ => new TelegramConfig(
            Enabled: true,
            ToolPermissions: permissions.ToDictionary(p => p.Tool, p => p.Permission),
            LinkedChats: []));
        return store;
    }

    private static Task RunAsTelegramAsync(Func<Task> action) => ChannelContext.RunAsAsync("telegram", ChatId.ToString(), action);

    [Fact]
    public async Task RequestAsync_NonTelegramTurn_AlwaysAutoApproves()
    {
        var gate = new TelegramGatingApprovalGate(StoreWithPermissions(), new PendingApprovalStore(), new FakeTelegramBotClient(), Logger);

        // ChannelContext.Current is null by default — not a Telegram-originated turn.
        var decision = await gate.RequestAsync(new ToolInvocationPreview("shell", "run", "ls"), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Approve, decision);
    }

    [Fact]
    public async Task RequestAsync_TelegramTurn_ToolDenied_DeniesWithoutReachingChat()
    {
        var bot = new FakeTelegramBotClient();
        var gate = new TelegramGatingApprovalGate(StoreWithPermissions(("shell", ToolPermission.Deny)), new PendingApprovalStore(), bot, Logger);

        var decision = ApprovalDecision.Approve;
        await RunAsTelegramAsync(async () =>
            decision = await gate.RequestAsync(new ToolInvocationPreview("shell", "run", "ls"), CancellationToken.None));

        Assert.Equal(ApprovalDecision.Deny, decision);
        Assert.Empty(bot.SentMessages);
    }

    [Fact]
    public async Task RequestAsync_TelegramTurn_ToolNotListed_DefaultsToDeny()
    {
        // A tool absent from ToolPermissions entirely (not just explicitly Deny) must still be
        // refused — the safe default from the old AllowedTools allowlist's implicit behavior.
        var gate = new TelegramGatingApprovalGate(StoreWithPermissions(), new PendingApprovalStore(), new FakeTelegramBotClient(), Logger);

        var decision = ApprovalDecision.Approve;
        await RunAsTelegramAsync(async () =>
            decision = await gate.RequestAsync(new ToolInvocationPreview("shell", "run", "ls"), CancellationToken.None));

        Assert.Equal(ApprovalDecision.Deny, decision);
    }

    [Fact]
    public async Task RequestAsync_TelegramTurn_ToolFull_ApprovesImmediately_WithoutReachingChat()
    {
        var bot = new FakeTelegramBotClient();
        var gate = new TelegramGatingApprovalGate(StoreWithPermissions(("shell", ToolPermission.Full)), new PendingApprovalStore(), bot, Logger);

        var decision = ApprovalDecision.Deny;
        await RunAsTelegramAsync(async () =>
            decision = await gate.RequestAsync(new ToolInvocationPreview("shell", "run", "ls"), CancellationToken.None));

        Assert.Equal(ApprovalDecision.Approve, decision);
        Assert.Empty(bot.SentMessages); // Full never prompts in chat.
    }

    [Fact]
    public async Task RequestAsync_TelegramTurn_ToolAsk_SendsInlineKeyboard_AndWaitsForResolution()
    {
        var bot = new FakeTelegramBotClient();
        var approvalStore = new PendingApprovalStore();
        var gate = new TelegramGatingApprovalGate(StoreWithPermissions(("write_file", ToolPermission.Ask)), approvalStore, bot, Logger);

        var requestTask = RunAsTelegramAsync(async () =>
        {
            var decision = await gate.RequestAsync(new ToolInvocationPreview("write_file", "Write to foo.txt", "+hello"), CancellationToken.None);
            Assert.Equal(ApprovalDecision.Approve, decision);
        });

        // Resolution comes from the chat's inline buttons (simulated here via
        // PendingApprovalStore.Resolve, exactly what TelegramSessionDriver.
        // HandleApprovalCallbackAsync calls on a button tap).
        await WaitUntilAsync(() => bot.SentMessages.Count > 0);
        Assert.Single(bot.SentMessages);
        Assert.Contains("Write to foo.txt", bot.SentMessages[0]);

        var pending = Assert.Single(approvalStore.List());
        Assert.True(approvalStore.Resolve(pending.Id, ApprovalDecision.Approve));

        await requestTask;
    }

    [Fact]
    public async Task RequestAsync_TelegramTurn_ToolAsk_DeniedViaChat_ReturnsDeny()
    {
        var bot = new FakeTelegramBotClient();
        var approvalStore = new PendingApprovalStore();
        var gate = new TelegramGatingApprovalGate(StoreWithPermissions(("shell", ToolPermission.Ask)), approvalStore, bot, Logger);

        var decision = ApprovalDecision.Approve;
        var requestTask = RunAsTelegramAsync(async () =>
            decision = await gate.RequestAsync(new ToolInvocationPreview("shell", "run", "rm -rf /"), CancellationToken.None));

        await WaitUntilAsync(() => approvalStore.List().Count > 0);
        var pending = Assert.Single(approvalStore.List());
        approvalStore.Resolve(pending.Id, ApprovalDecision.Deny);

        await requestTask;
        Assert.Equal(ApprovalDecision.Deny, decision);
    }

    [Fact]
    public async Task RequestViaChannelAsync_NonTelegramTurn_AutoApproves_WithoutSendingMessage()
    {
        // Used by McpAwareApprovalGate, which has already decided a call is Ask-mode via
        // McpConfigStore — this must not re-check TelegramConfig's own permission map.
        var bot = new FakeTelegramBotClient();
        var gate = new TelegramGatingApprovalGate(StoreWithPermissions(("mcp__fs__read", ToolPermission.Deny)), new PendingApprovalStore(), bot, Logger);

        var decision = await gate.RequestViaChannelAsync(new ToolInvocationPreview("mcp__fs__read", "Call MCP tool", "{}"), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Approve, decision);
        Assert.Empty(bot.SentMessages);
    }

    [Fact]
    public async Task RequestViaChannelAsync_TelegramTurn_SendsInlineKeyboard_IgnoringOwnPermissionMap()
    {
        var bot = new FakeTelegramBotClient();
        var approvalStore = new PendingApprovalStore();
        // TelegramConfig has no entry (would default-deny via RequestAsync) — RequestViaChannelAsync
        // must skip that lookup entirely, since McpAwareApprovalGate already decided this is Ask-mode.
        var gate = new TelegramGatingApprovalGate(StoreWithPermissions(), approvalStore, bot, Logger);

        var requestTask = RunAsTelegramAsync(async () =>
        {
            var decision = await gate.RequestViaChannelAsync(new ToolInvocationPreview("mcp__fs__read", "Call MCP tool", "{}"), CancellationToken.None);
            Assert.Equal(ApprovalDecision.Approve, decision);
        });

        await WaitUntilAsync(() => bot.SentMessages.Count > 0);
        var pending = Assert.Single(approvalStore.List());
        Assert.True(approvalStore.Resolve(pending.Id, ApprovalDecision.Approve));

        await requestTask;
    }

    [Fact]
    public async Task ChannelContext_IsolatedAcrossConcurrentTurns()
    {
        var telegramTask = ChannelContext.RunAsAsync("telegram", "123", async () =>
        {
            await Task.Delay(20);
            Assert.Equal("telegram", ChannelContext.Current);
            Assert.Equal("123", ChannelContext.ChannelId);
        });

        var localTask = Task.Run(async () =>
        {
            Assert.Null(ChannelContext.Current);
            Assert.Null(ChannelContext.ChannelId);
            await Task.Delay(20);
            Assert.Null(ChannelContext.Current);
        });

        await Task.WhenAll(telegramTask, localTask);
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
