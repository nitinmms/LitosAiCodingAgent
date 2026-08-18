using Litos.Tools.Shell;

namespace Litos.VsCodeHost.Tests;

public class AutoApprovalGateTests
{
    [Fact]
    public async Task RequestAsync_AlwaysApproves()
    {
        var gate = new AutoApprovalGate();
        var preview = new ToolInvocationPreview("shell", "run a command", "echo hi");

        var decision = await gate.RequestAsync(preview, CancellationToken.None);

        Assert.Equal(ApprovalDecision.Approve, decision);
    }

    [Fact]
    public async Task RequestAsync_ApprovesMcpToolNamesTheSameAsBuiltIns()
    {
        var gate = new AutoApprovalGate();
        var preview = new ToolInvocationPreview("mcp__filesystem__read_file", "read a file", null);

        var decision = await gate.RequestAsync(preview, CancellationToken.None);

        Assert.Equal(ApprovalDecision.Approve, decision);
    }
}
