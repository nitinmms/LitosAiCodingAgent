using Litos.Tools.Shell;

namespace Litos.Tools.Mcp;

/// <summary>
/// Decorator that gates mcp__{server}__{tool}-prefixed tool calls against McpConfigStore,
/// delegating everything else unchanged to the wrapped gate. Kept as a decorator rather than
/// folded into TelegramGatingApprovalGate so MCP gating applies regardless of which channel
/// originated the turn — an HTTP-API-only deployment with no Telegram bridge configured must
/// still gate MCP tools if the user configured Ask/Deny for a server, and Litos.Gui's own gate
/// would get MCP gating for free by wrapping it the same way with no Telegram dependency pulled
/// in.
///
/// Only ever instantiated in Litos.Api/Program.cs today (wrapping whichever of
/// TelegramGatingApprovalGate/AutoApprovalGate that branch already builds) — this type itself
/// stays face-agnostic apart from the Ask-mode branch below, which looks for the optional
/// IChannelApprovalGate capability (implemented by TelegramGatingApprovalGate, defined in
/// Litos.Tools alongside IToolApprovalGate so this project never needs a reference to Litos.Api)
/// so MCP tools get the same in-chat Approve/Deny buttons native tools already get, rather than
/// being resolvable only via the web Pending Approvals panel. That panel remains the resolution
/// surface only when inner has no such channel (AutoApprovalGate — no Telegram bridge configured
/// for this deployment at all).
/// </summary>
public sealed class McpAwareApprovalGate(IToolApprovalGate inner, McpConfigStore configStore, PendingApprovalStore approvals)
    : IToolApprovalGate
{
    private const string Prefix = "mcp__";

    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        if (!preview.ToolName.StartsWith(Prefix, StringComparison.Ordinal))
            return inner.RequestAsync(preview, ct);

        // mcp__{server}__{tool} — server names are validated to never contain "__"
        // (McpConfigStore.Validate), so splitting on the first two "__" occurrences is
        // unambiguous regardless of what the tool name itself contains.
        var rest = preview.ToolName[Prefix.Length..];
        var separatorIndex = rest.IndexOf("__", StringComparison.Ordinal);
        var serverName = separatorIndex >= 0 ? rest[..separatorIndex] : rest;

        var server = configStore.Current.Servers.FirstOrDefault(s => s.Name == serverName);
        var permission = server?.PermissionFor(preview.ToolName) ?? ToolPermission.Deny;

        return permission switch
        {
            ToolPermission.Deny => Task.FromResult(ApprovalDecision.Deny),
            ToolPermission.Full => Task.FromResult(ApprovalDecision.Approve),
            // Ask: a Telegram bridge exists — reuse its chat-button flow (mirrors native tools:
            // gated via buttons on a Telegram turn, auto-approved on any other turn) instead of
            // the web panel. No Telegram bridge at all (inner has no IChannelApprovalGate
            // capability — AutoApprovalGate) still has nowhere else to resolve, so the panel
            // remains that deployment shape's only gate.
            _ => inner is IChannelApprovalGate channelGate
                ? channelGate.RequestViaChannelAsync(preview, ct)
                : approvals.Add(preview),
        };
    }
}
