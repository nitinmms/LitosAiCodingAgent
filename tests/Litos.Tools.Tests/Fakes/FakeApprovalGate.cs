using Litos.Tools.Shell;

namespace Litos.Tools.Tests.Fakes;

/// <summary>
/// Defaults to Deny so a test that forgets to configure a decision fails safe — no real
/// file write / shell command runs unless the test explicitly opts in to Approve.
/// Records the last preview received for assertion.
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
