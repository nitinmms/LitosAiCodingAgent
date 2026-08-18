using Litos.Tools.Shell;

namespace Litos.VsCodeHost;

/// <summary>
/// Approves every tool call unconditionally — this process has no interactive approval UI,
/// matching Litos.Gui/Litos.Console's own auto-approve model. Copy of Litos.Api's
/// AutoApprovalGate; duplicated rather than shared since this project deliberately has no
/// reference to Litos.Api (see Program.cs remarks).
/// </summary>
public sealed class AutoApprovalGate : IToolApprovalGate
{
    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Approve);
}
