namespace Litos.Api.Channels;

/// <summary>
/// Ambient signal carrying "which channel originated the turn currently executing" down into
/// tool invocation, without threading a caller-identity parameter through AgentLoop/ITool
/// (ReadMe_TelegramIntegrationTool.md's own "no changes to AgentLoop... this is purely additive"
/// principle, §6.3). AsyncLocal flows correctly through the await-chain from
/// TelegramSessionDriver's call into AgentWorker.StartOrSteerTurn down through
/// AgentLoop.RunTurnAsync into ShellTool/WriteFileTool/EditFileTool's own
/// approvalGate.RequestAsync call, and — critically — stays correctly isolated per concurrently
/// -running turn (each Task capturing its own AsyncLocal value), unlike a shared mutable field.
///
/// null means "not a channel-originated turn" (the HTTP API's own session) — never gated.
/// </summary>
public static class ChannelContext
{
    private static readonly AsyncLocal<(string Channel, string? ChannelId)?> _current = new();

    public static string? Current => _current.Value?.Channel;

    /// <summary>
    /// The channel's own identifier for the conversation the current turn belongs to — e.g. a
    /// Telegram chat id, as a string since callers outside Litos.Api.Channels.Telegram shouldn't
    /// need a Telegram-specific type. Null for a non-channel turn or a channel that has no such
    /// concept. Set together with the channel name so the pair can never observe one updated
    /// without the other (e.g. mid-restore during a nested RunAsAsync).
    /// </summary>
    public static string? ChannelId => _current.Value?.ChannelId;

    /// <summary>Sets Current/ChannelId for the duration of <paramref name="action"/>, restoring both after.</summary>
    public static async Task RunAsAsync(string channelName, string? channelId, Func<Task> action)
    {
        var previous = _current.Value;
        _current.Value = (channelName, channelId);
        try
        {
            await action();
        }
        finally
        {
            _current.Value = previous;
        }
    }
}
