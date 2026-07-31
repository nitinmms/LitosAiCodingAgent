using Litos.Tools.Shell;

namespace Litos.Api.Approvals;

/// <summary>
/// IToolApprovalGate for deployments with no Telegram token configured — approves everything,
/// since there's no channel that could ever set ChannelContext.Current to "telegram" and no other
/// approval UI left to gate against. Mirrors Litos.Gui's own spike-only GuiApprovalGate.
/// </summary>
public sealed class AutoApprovalGate : IToolApprovalGate
{
    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Approve);
}
