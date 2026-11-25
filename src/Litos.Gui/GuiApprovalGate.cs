using Litos.Tools.Shell;

namespace Litos.Gui;

/// <summary>
/// The gate for Litos.Gui's built-in tools (read_file, shell, edit_file, ...): auto-approves
/// everything, the shared convention across faces for internal tools (ReadMe_ConsoleParityPlan.md's
/// "Revised direction on tool approval"; Litos.VsCodeHost's AutoApprovalGate is the same). MCP tools
/// are gated per server instead: Program.cs wraps this in McpAwareApprovalGate, which intercepts
/// mcp__-prefixed calls (Deny/Ask/Full) and hands everything else here unchanged.
/// </summary>
public sealed class GuiApprovalGate : IToolApprovalGate
{
    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Approve);
}
