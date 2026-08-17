using Litos.Agent.Session;
using Litos.Api.Channels;

namespace Litos.Api.Tests.Channels;

public class ChannelContextTests
{
    [Fact]
    public async Task RunAsAsync_ChannelOnly_SetsChannelAndChannelId_LeavesOwnerSessionIdDefaulted()
    {
        await ChannelContext.RunAsAsync("telegram", "12345", () =>
        {
            Assert.Equal("telegram", ChannelContext.Current);
            Assert.Equal("12345", ChannelContext.ChannelId);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RunAsAsync_OwnerSessionOnly_SetsOwnerAndSessionId_LeavesChannelNull()
    {
        await ChannelContext.RunAsAsync(SessionOwner.Local, "session-1", () =>
        {
            Assert.Equal(SessionOwner.Local, ChannelContext.Owner);
            Assert.Equal("session-1", ChannelContext.SessionId);
            Assert.Null(ChannelContext.Current);
            return Task.CompletedTask;
        });
    }

    // Mirrors the real call pattern: TelegramSessionDriver wraps the synchronous kickoff of
    // AgentWorker.RunTurnAsync's Task in ChannelContext.RunAsAsync("telegram", chatId, ...), and
    // RunTurnAsync itself (running inside that Task) wraps its own body in
    // ChannelContext.RunAsAsync(owner, sessionId, ...) — the inner call must not clobber the
    // outer channel/chatId, and the outer scope must still see the owner/sessionId the inner call set
    // is scoped to (it shouldn't leak upward, but shouldn't be lost for code running inside it either).
    [Fact]
    public async Task RunAsAsync_ChannelScope_NestingOwnerScope_PreservesChannelInsideTheNestedScope()
    {
        await ChannelContext.RunAsAsync("telegram", "chat-1", async () =>
        {
            await ChannelContext.RunAsAsync(SessionOwner.Telegram, "session-1", () =>
            {
                Assert.Equal("telegram", ChannelContext.Current);
                Assert.Equal("chat-1", ChannelContext.ChannelId);
                Assert.Equal(SessionOwner.Telegram, ChannelContext.Owner);
                Assert.Equal("session-1", ChannelContext.SessionId);
                return Task.CompletedTask;
            });

            // Back in the outer scope after the nested call returns — channel is still set, but
            // the owner/sessionId set by the inner call must not have leaked upward.
            Assert.Equal("telegram", ChannelContext.Current);
            Assert.Null(ChannelContext.Owner);
        });
    }

    [Fact]
    public async Task RunAsAsync_OwnerScope_RestoresPreviousValueAfterCompletion()
    {
        await ChannelContext.RunAsAsync(SessionOwner.Local, "session-1", () => Task.CompletedTask);

        Assert.Null(ChannelContext.Owner);
        Assert.Null(ChannelContext.SessionId);
    }

    [Fact]
    public async Task RunAsAsync_ConcurrentTurns_DoNotLeakOwnerAcrossEachOther()
    {
        var barrier = new TaskCompletionSource();

        var taskA = ChannelContext.RunAsAsync(SessionOwner.Local, "session-a", async () =>
        {
            await barrier.Task;
            Assert.Equal("session-a", ChannelContext.SessionId);
        });
        var taskB = ChannelContext.RunAsAsync(SessionOwner.Telegram, "session-b", async () =>
        {
            await barrier.Task;
            Assert.Equal("session-b", ChannelContext.SessionId);
        });

        barrier.SetResult();
        await Task.WhenAll(taskA, taskB);
    }
}
