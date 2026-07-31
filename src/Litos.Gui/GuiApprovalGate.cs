using Litos.Tools.Shell;

namespace Litos.Gui;

/// <summary>
/// Spike-only approval gate: auto-approves everything. A real implementation would
/// show an Avalonia dialog (analogous to Litos.Console's ApprovalDialog) and await
/// the user's click via a TaskCompletionSource, same pattern noted in
/// ReadMe_AgentDesign.md §7.5 for Terminal.Gui's ApprovalDialog.
/// </summary>
public sealed class GuiApprovalGate : IToolApprovalGate
{
    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Approve);
}
