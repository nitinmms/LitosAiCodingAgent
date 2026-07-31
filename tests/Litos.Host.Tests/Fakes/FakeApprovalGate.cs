using Litos.Tools.Shell;

namespace Litos.Host.Tests.Fakes;

public sealed class FakeApprovalGate : IToolApprovalGate
{
    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Deny);
}
