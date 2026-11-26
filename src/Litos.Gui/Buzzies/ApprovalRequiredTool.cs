using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Tools.Shell;

namespace Litos.Gui.Buzzies;

/// <summary>
/// Asks the user before every call of the wrapped tool, through the same PendingApprovalStore the
/// Gui already renders as an in-transcript Approve/Deny card. Used for Builder's shell: built-in
/// tools are otherwise blanket-approved in the Gui (GuiApprovalGate), and a Buzzy's shell must not
/// be. "Approve always" is treated as approving this one call, since Phase 0 keeps no standing grants.
/// </summary>
public sealed class ApprovalRequiredTool(ITool inner, PendingApprovalStore approvals, string buzzyName, string note) : ITool
{
    public string Name => inner.Name;
    public string Description => inner.Description;
    public JsonElement ParameterSchema => inner.ParameterSchema;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var detail = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("command", out var command)
                     && command.ValueKind == JsonValueKind.String
            ? command.GetString()!
            : arguments.GetRawText();

        var (_, decision) = approvals.AddPending(new ToolInvocationPreview(
            Name, $"{buzzyName} wants to run this. {note}", detail));

        if (await decision.WaitAsync(ct) == ApprovalDecision.Deny)
            return ToolResult.Error($"The user denied this {Name} call, so it did not run.");

        return await inner.InvokeAsync(arguments, ct);
    }
}
