using Litos.Tools.Shell;

namespace Litos.VsCodeHost.Approvals;

/// <summary>
/// Pushed inline on the same SSE turn stream as AgentEvent, for an MCP tool call gated Ask by
/// McpAwareApprovalGate (see Program.cs's approval-gate wiring). Deliberately NOT an AgentEvent
/// subtype — AgentEvent is defined in Litos.Agent (the brain), which stays UI/host-neutral and
/// knows nothing about approval gating; these are serialized onto the same "event: agent-event" SSE
/// stream as a sibling shape instead (see TurnsEndpoints.ToSseData), distinguished from every real
/// AgentEvent variant by carrying an "ApprovalId" property none of them have.
/// </summary>
public sealed record PendingApprovalRequestedWireEvent(Guid ApprovalId, string ToolName, string Summary, string? DiffOrCommand)
{
    public static PendingApprovalRequestedWireEvent From(PendingApproval approval) => new(
        approval.Id, approval.Preview.ToolName, approval.Preview.Summary, approval.Preview.DiffOrCommand);
}

/// <summary>Pushed when a pending approval resolves for any reason (a human's decision, or the
/// store's own 10-minute timeout) — lets the webview remove/replace its inline prompt even if the
/// resolution came from the timeout rather than a click in this same webview.</summary>
public sealed record PendingApprovalResolvedWireEvent(Guid ApprovalId);
