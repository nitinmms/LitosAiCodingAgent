using System.Collections.Concurrent;

namespace Litos.Tools.Shell;

public sealed record PendingApproval(Guid Id, ToolInvocationPreview Preview, DateTimeOffset RequestedAt);

/// <summary>
/// The in-memory bridge a suspended approval-gate `await` needs to be resolved from outside
/// itself — a tool call blocks on RequestAsync until something else (Telegram's inline-keyboard
/// callback, or the web Ask-mode approval panel) resolves it, which arrives as a separate event,
/// not as a return value the gate could just produce directly. Raises <see cref="Added"/>/
/// <see cref="Resolved"/> for observability (e.g. Logs.razor, PendingApprovalsPanel.razor).
///
/// Known limitation, carried over unchanged from the design doc: any approval still pending at
/// process restart is lost — the suspended tool call's Task never resolves, and that one turn
/// stays blocked forever. Acceptable for v1; not fixed here.
///
/// Bounded by <see cref="Timeout"/> (default 10 minutes): an approval nobody resolves in time
/// auto-denies rather than leaving the suspended tool call — and the turn showing
/// AgentWorker.IsTurnActive == true — hanging forever with no feedback to anyone. Root-caused a
/// real incident where a Telegram-originated write_file sat waiting 25+ minutes with no way for
/// the Telegram user to even know it was stuck.
///
/// Shared by TelegramGatingApprovalGate and McpAwareApprovalGate — both Ask-mode flows resolve
/// through this same store. When a Telegram bridge is configured, both native and MCP Ask-mode
/// calls resolve via Telegram's in-chat buttons (McpAwareApprovalGate delegates to
/// TelegramGatingApprovalGate's IChannelApprovalGate.RequestViaChannelAsync); PendingApprovalsPanel
/// is then only reached by the timeout path. Only a deployment with no Telegram bridge at all
/// (McpAwareApprovalGate wrapping AutoApprovalGate) routes Ask-mode MCP approvals into this store
/// with no other resolution surface than the panel.
/// </summary>
public sealed class PendingApprovalStore(TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly ConcurrentDictionary<Guid, (PendingApproval Approval, TaskCompletionSource<ApprovalDecision> Tcs)> _pending = [];

    public event Action<PendingApproval>? Added;
    public event Action<Guid>? Resolved;

    /// <summary>Fires when an approval times out specifically (vs. a human Resolve call) — lets a
    /// caller like TelegramGatingApprovalGate distinguish "nobody answered in time" from an
    /// ordinary Approve/Deny, e.g. to edit its chat message with a clearer "⏱ timed out" label
    /// instead of the generic Approved/Denied text.</summary>
    public event Action<Guid>? TimedOut;

    public Task<ApprovalDecision> Add(ToolInvocationPreview preview) => AddPending(preview).Task;

    /// <summary>
    /// Same as <see cref="Add"/>, but also returns the generated <see cref="PendingApproval"/>
    /// (its Id in particular) to the caller synchronously — needed by any caller that must
    /// reference this specific approval before it resolves (e.g. to embed its Id in an inline
    /// keyboard's callback data). Re-deriving the Id via List().Last(...) after the fact would
    /// race a decision that resolves between Add and List (Resolve removes the entry from
    /// _pending immediately), so this is the only race-free way to get it.
    /// </summary>
    public (PendingApproval Approval, Task<ApprovalDecision> Task) AddPending(ToolInvocationPreview preview)
    {
        var id = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new PendingApproval(id, preview, DateTimeOffset.UtcNow);
        _pending[id] = (approval, tcs);
        Added?.Invoke(approval);
        _ = TimeoutAfterAsync(id);
        return (approval, tcs.Task);
    }

    private async Task TimeoutAfterAsync(Guid id)
    {
        await Task.Delay(_timeout);
        if (!_pending.TryRemove(id, out var entry))
            return; // Already resolved by a human before the timeout fired.

        entry.Tcs.SetResult(ApprovalDecision.Deny);
        TimedOut?.Invoke(id);
        Resolved?.Invoke(id);
    }

    public bool Resolve(Guid id, ApprovalDecision decision)
    {
        if (!_pending.TryRemove(id, out var entry))
            return false;

        entry.Tcs.SetResult(decision);
        Resolved?.Invoke(id);
        return true;
    }

    public IReadOnlyList<PendingApproval> List() =>
        _pending.Values.Select(e => e.Approval).OrderBy(a => a.RequestedAt).ToList();
}
