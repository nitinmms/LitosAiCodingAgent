using Litos.Tools.Shell;

namespace Litos.Console.Terminal;

/// <summary>
/// Auto-approves every tool call — built-in (shell/write_file/edit_file) and MCP alike, matching
/// Litos.Gui's GuiApprovalGate exactly (copied, not shared, per the no-Gui-changes rule). Replaces
/// ApprovalDialog/NonInteractiveApprovalGate as Console's only IToolApprovalGate; both of those
/// remain in the codebase, unused, since ApprovalDialog's DiffView is still reused by /reflect.
/// </summary>
public sealed class AutoApprovalGate : IToolApprovalGate
{
    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Approve);
}
