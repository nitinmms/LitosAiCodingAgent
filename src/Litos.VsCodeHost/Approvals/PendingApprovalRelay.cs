using System.Collections.Concurrent;
using Litos.Agent.Session;
using Litos.Tools.Shell;

namespace Litos.VsCodeHost.Approvals;

/// <summary>
/// Bridges PendingApprovalStore's process-wide Added/Resolved events onto the one SSE turn stream
/// that actually triggered each approval — PendingApprovalStore itself has no per-session concept
/// (Litos.Api surfaces every pending approval on one shared web admin panel instead), but this host
/// can have multiple concurrent turns (AgentWorker._activeTurns, one per SessionOwner/sessionId
/// key), so a subscriber here filters by ChannelContext.SessionId captured at the moment Added
/// fires — synchronously, from inside McpAwareApprovalGate's call stack, itself inside the turn's
/// own ChannelContext.RunAsAsync scope (see AgentWorker.RunTurnAsync).
/// </summary>
public sealed class PendingApprovalRelay(PendingApprovalStore store)
{
    private readonly ConcurrentDictionary<string, Action<PendingApprovalRequestedWireEvent>> _requestedSubscribers = [];
    private readonly ConcurrentDictionary<string, Action<PendingApprovalResolvedWireEvent>> _resolvedSubscribers = [];
    // ApprovalId -> the sessionId it was raised for, so a later Resolved (which carries only the
    // Guid, no session context of its own) can still be routed to the right subscriber.
    private readonly ConcurrentDictionary<Guid, string> _approvalSessionIds = [];

    public PendingApprovalRelay Start()
    {
        store.Added += OnAdded;
        store.Resolved += OnResolved;
        return this;
    }

    /// <summary>Subscribes for the duration of one turn's SSE stream — matches the calling
    /// convention every other per-turn channel in this project uses (register at turn start,
    /// unregister in RunTurnAsync's finally).</summary>
    public IDisposable Subscribe(string sessionId, Action<PendingApprovalRequestedWireEvent> onRequested, Action<PendingApprovalResolvedWireEvent> onResolved)
    {
        _requestedSubscribers[sessionId] = onRequested;
        _resolvedSubscribers[sessionId] = onResolved;
        return new Unsubscriber(this, sessionId);
    }

    private void Unsubscribe(string sessionId)
    {
        _requestedSubscribers.TryRemove(sessionId, out _);
        _resolvedSubscribers.TryRemove(sessionId, out _);
    }

    private void OnAdded(PendingApproval approval)
    {
        var sessionId = ChannelContext.SessionId;
        if (sessionId is null)
            return; // Raised outside any turn's ChannelContext scope — nothing to route to.

        _approvalSessionIds[approval.Id] = sessionId;
        if (_requestedSubscribers.TryGetValue(sessionId, out var onRequested))
            onRequested(PendingApprovalRequestedWireEvent.From(approval));
    }

    private void OnResolved(Guid approvalId)
    {
        if (!_approvalSessionIds.TryRemove(approvalId, out var sessionId))
            return;

        if (_resolvedSubscribers.TryGetValue(sessionId, out var onResolved))
            onResolved(new PendingApprovalResolvedWireEvent(approvalId));
    }

    private sealed class Unsubscriber(PendingApprovalRelay relay, string sessionId) : IDisposable
    {
        public void Dispose() => relay.Unsubscribe(sessionId);
    }
}
