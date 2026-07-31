using Litos.Tools.Shell;

namespace Litos.Tools.Mcp.Tests.Fakes;

/// <summary>
/// Stands in for TelegramGatingApprovalGate to verify McpAwareApprovalGate's Ask-mode branch
/// delegates to IChannelApprovalGate.RequestViaChannelAsync (the in-chat button flow) rather than
/// falling back to PendingApprovalStore, whenever inner exposes that capability.
/// </summary>
public sealed class FakeChannelApprovalGate : IChannelApprovalGate
{
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Deny;

    public int RequestAsyncCallCount { get; private set; }

    public int RequestViaChannelAsyncCallCount { get; private set; }

    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        RequestAsyncCallCount++;
        return Task.FromResult(Decision);
    }

    public Task<ApprovalDecision> RequestViaChannelAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        RequestViaChannelAsyncCallCount++;
        return Task.FromResult(Decision);
    }
}
