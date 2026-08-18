using Litos.Agent.Session;

namespace Litos.VsCodeHost;

/// <summary>
/// Local copy of Litos.Api.Channels.ChannelContext.cs, trimmed to just Owner/SessionId — this
/// process has no channel-bridge concept (Telegram etc.), only ever the single local session a
/// turn is running for. Kept as an independent copy rather than a Litos.Api reference, matching
/// this codebase's established convention for cross-face code (AttachHandler.cs/ImageMedia.cs are
/// duplicated the same way between Litos.Console and Litos.Gui).
///
/// Exists so AgentWorker.RunTurnAsync can tag "which session is this turn for" as ambient state,
/// which PendingApprovalListener reads at the moment PendingApprovalStore.Added fires (synchronously,
/// from inside the gate's call stack, itself inside RunAsAsync's scope) to filter pending MCP
/// approvals down to the ones belonging to the turn currently streaming — Litos.Api's own
/// PendingApprovalStore has no per-session concept at all (it's global, surfaced on one shared web
/// admin panel), but this host multiplexes concurrent turns per SessionOwner/sessionId key
/// (AgentWorker._activeTurns) and pushes approvals inline on each turn's own SSE stream, so
/// cross-session correlation is required here in a way Litos.Api never needed.
/// </summary>
public static class ChannelContext
{
    private static readonly AsyncLocal<(SessionOwner Owner, string SessionId)?> _current = new();

    public static SessionOwner? Owner => _current.Value?.Owner;

    public static string? SessionId => _current.Value?.SessionId;

    public static async Task RunAsAsync(SessionOwner owner, string sessionId, Func<Task> action)
    {
        var previous = _current.Value;
        _current.Value = (owner, sessionId);
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
