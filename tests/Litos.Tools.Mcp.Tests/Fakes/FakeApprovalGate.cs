using Litos.Tools.Shell;

namespace Litos.Tools.Mcp.Tests.Fakes;

/// <summary>
/// Defaults to Deny so a test that forgets to configure a decision fails safe. Records the last
/// preview received for assertion. Mirrors Litos.Tools.Tests.Fakes.FakeApprovalGate.
/// </summary>
public sealed class FakeApprovalGate : IToolApprovalGate
{
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Deny;

    public ToolInvocationPreview? LastPreview { get; private set; }

    public int CallCount { get; private set; }

    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        LastPreview = preview;
        CallCount++;
        return Task.FromResult(Decision);
    }
}
