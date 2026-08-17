using Litos.Agent.Session;

namespace Litos.Api.Channels;

/// <summary>
/// Ambient signal carrying "which channel originated the turn currently executing" and "which
/// session/owner this turn belongs to" down into tool invocation, without threading a
/// caller-identity parameter through AgentLoop/ITool (ReadMe_TelegramIntegrationTool.md's own "no
/// changes to AgentLoop... this is purely additive" principle, §6.3). AsyncLocal flows correctly
/// through the await-chain from TelegramSessionDriver's call into AgentWorker.StartOrSteerTurn
/// down through AgentLoop.RunTurnAsync into ShellTool/WriteFileTool/EditFileTool's own
/// approvalGate.RequestAsync call, and — critically — stays correctly isolated per concurrently
/// -running turn (each Task capturing its own AsyncLocal value), unlike a shared mutable field.
///
/// Owner/SessionId are set for every turn (AgentWorker.RunTurnAsync sets them unconditionally),
/// while Channel/ChannelId stay null unless a channel bridge (e.g. Telegram) explicitly wraps its
/// own call in RunAsAsync — that null still means "not a channel-originated turn" (the HTTP API's
/// own session), never gated.
/// </summary>
public static class ChannelContext
{
    private static readonly AsyncLocal<(string? Channel, string? ChannelId, SessionOwner? Owner, string? SessionId)?> _current = new();

    public static string? Current => _current.Value?.Channel;

    /// <summary>
    /// The channel's own identifier for the conversation the current turn belongs to — e.g. a
    /// Telegram chat id, as a string since callers outside Litos.Api.Channels.Telegram shouldn't
    /// need a Telegram-specific type. Null for a non-channel turn or a channel that has no such
    /// concept. Set together with the channel name so the pair can never observe one updated
    /// without the other (e.g. mid-restore during a nested RunAsAsync).
    /// </summary>
    public static string? ChannelId => _current.Value?.ChannelId;

    /// <summary>The session owner for the turn currently executing. Null only before AgentWorker.RunTurnAsync has set it.</summary>
    public static SessionOwner? Owner => _current.Value?.Owner;

    /// <summary>The session id for the turn currently executing. Null only before AgentWorker.RunTurnAsync has set it.</summary>
    public static string? SessionId => _current.Value?.SessionId;

    /// <summary>Sets Current/ChannelId for the duration of <paramref name="action"/>, preserving whatever Owner/SessionId is already set (or default/null, if called before AgentWorker.RunTurnAsync's own wrap), and restoring the previous value after.</summary>
    public static async Task RunAsAsync(string channelName, string? channelId, Func<Task> action)
    {
        var previous = _current.Value;
        _current.Value = (channelName, channelId, previous?.Owner, previous?.SessionId);
        try
        {
            await action();
        }
        finally
        {
            _current.Value = previous;
        }
    }

    /// <summary>Sets Owner/SessionId for the duration of <paramref name="action"/>, preserving whatever Channel/ChannelId is already set (null if this is the outermost scope, e.g. an HTTP-API-own-session turn), and restoring the previous value after. Called once per turn by AgentWorker.RunTurnAsync.</summary>
    public static async Task RunAsAsync(SessionOwner owner, string sessionId, Func<Task> action)
    {
        var previous = _current.Value;
        _current.Value = (previous?.Channel, previous?.ChannelId, owner, sessionId);
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
