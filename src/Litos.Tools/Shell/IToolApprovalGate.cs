namespace Litos.Tools.Shell;

public interface IToolApprovalGate
{
    Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct);
}

/// <summary>
/// Optional capability an IToolApprovalGate can implement when it has its own channel to notify
/// for Ask-mode decisions (e.g. Telegram's in-chat Approve/Deny buttons) independent of its own
/// per-tool permission lookup. Lets a caller that has already decided a call is Ask-mode via a
/// different permission source — McpAwareApprovalGate, deciding via McpConfigStore rather than
/// whatever config the gate itself would consult — reuse that notify-and-await flow without
/// referencing the gate's concrete type (avoids Litos.Tools.Mcp needing a project reference to
/// Litos.Api, which already references Litos.Tools.Mcp).
/// </summary>
public interface IChannelApprovalGate : IToolApprovalGate
{
    /// <summary>
    /// Sends the Ask-mode prompt via this gate's own channel and awaits the decision, without
    /// re-checking this gate's own permission config first. Implementations should mirror their
    /// own RequestAsync's "no channel context for this turn" fallback (e.g. auto-approve) so
    /// behavior stays identical to a tool gated directly through RequestAsync.
    /// </summary>
    Task<ApprovalDecision> RequestViaChannelAsync(ToolInvocationPreview preview, CancellationToken ct);
}

public enum ApprovalDecision
{
    Approve,
    ApproveAlways,
    Deny,
}

public sealed record ToolInvocationPreview(string ToolName, string Summary, string? DiffOrCommand);
