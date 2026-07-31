using Litos.Tools.Mcp.Tests.Fakes;
using Litos.Tools.Shell;

namespace Litos.Tools.Mcp.Tests;

public class McpAwareApprovalGateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}");
    private string StateFilePath => Path.Combine(_tempDir, "mcp.json");

    private static McpServerDefinition Server(string name, ToolPermission defaultPermission, IReadOnlyDictionary<string, ToolPermission>? overrides = null) => new(
        Name: name,
        Transport: McpTransportKind.Stdio,
        Command: "npx",
        Args: null,
        Env: null,
        Url: null,
        Enabled: true,
        DefaultPermission: defaultPermission,
        ToolOverrides: overrides);

    [Fact]
    public async Task RequestAsync_NonMcpToolName_DelegatesToInner()
    {
        var inner = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var gate = new McpAwareApprovalGate(inner, new McpConfigStore(StateFilePath), new PendingApprovalStore());

        var decision = await gate.RequestAsync(new ToolInvocationPreview("shell", "Run shell command", "echo hi"), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Approve, decision);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task RequestAsync_McpTool_ServerDefaultDeny_DeniesWithoutCallingInner()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [Server("filesystem", ToolPermission.Deny)] });
        var inner = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var gate = new McpAwareApprovalGate(inner, configStore, new PendingApprovalStore());

        var decision = await gate.RequestAsync(
            new ToolInvocationPreview("mcp__filesystem__read", "Call MCP tool", "{}"), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Deny, decision);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task RequestAsync_McpTool_ServerDefaultFull_ApprovesWithoutCallingInner()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [Server("filesystem", ToolPermission.Full)] });
        var inner = new FakeApprovalGate { Decision = ApprovalDecision.Deny };
        var gate = new McpAwareApprovalGate(inner, configStore, new PendingApprovalStore());

        var decision = await gate.RequestAsync(
            new ToolInvocationPreview("mcp__filesystem__read", "Call MCP tool", "{}"), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Approve, decision);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task RequestAsync_McpTool_ServerNotFoundInConfig_DeniesSafeByDefault()
    {
        var configStore = new McpConfigStore(StateFilePath);
        var gate = new McpAwareApprovalGate(new FakeApprovalGate(), configStore, new PendingApprovalStore());

        var decision = await gate.RequestAsync(
            new ToolInvocationPreview("mcp__unknown__read", "Call MCP tool", "{}"), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Deny, decision);
    }

    [Fact]
    public async Task RequestAsync_McpTool_PerToolOverride_TakesPrecedenceOverServerDefault()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with
        {
            Servers = [Server("filesystem", ToolPermission.Full,
                new Dictionary<string, ToolPermission> { ["mcp__filesystem__delete"] = ToolPermission.Deny })],
        });
        var gate = new McpAwareApprovalGate(new FakeApprovalGate(), configStore, new PendingApprovalStore());

        var deleteDecision = await gate.RequestAsync(
            new ToolInvocationPreview("mcp__filesystem__delete", "Call MCP tool", "{}"), CancellationToken.None);
        var readDecision = await gate.RequestAsync(
            new ToolInvocationPreview("mcp__filesystem__read", "Call MCP tool", "{}"), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Deny, deleteDecision);
        Assert.Equal(ApprovalDecision.Approve, readDecision);
    }

    [Fact]
    public async Task RequestAsync_McpTool_AskMode_NoChannelGate_BridgesThroughPendingApprovalStore()
    {
        // inner has no IChannelApprovalGate capability (e.g. AutoApprovalGate on a deployment with
        // no Telegram bridge configured at all) — the web panel remains the only resolution path.
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [Server("filesystem", ToolPermission.Ask)] });
        var approvals = new PendingApprovalStore();
        var gate = new McpAwareApprovalGate(new FakeApprovalGate(), configStore, approvals);

        var decisionTask = gate.RequestAsync(
            new ToolInvocationPreview("mcp__filesystem__read", "Call MCP tool", "{}"), CancellationToken.None);

        Assert.False(decisionTask.IsCompleted);
        var pending = Assert.Single(approvals.List());
        Assert.True(approvals.Resolve(pending.Id, ApprovalDecision.Approve));
        Assert.Equal(ApprovalDecision.Approve, await decisionTask);
    }

    [Fact]
    public async Task RequestAsync_McpTool_AskMode_InnerIsChannelGate_DelegatesToChannelInsteadOfPanel()
    {
        // inner exposes IChannelApprovalGate (TelegramGatingApprovalGate in production) — Ask-mode
        // MCP calls should reuse its in-chat button flow rather than the web panel.
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [Server("filesystem", ToolPermission.Ask)] });
        var approvals = new PendingApprovalStore();
        var channelGate = new FakeChannelApprovalGate { Decision = ApprovalDecision.Approve };
        var gate = new McpAwareApprovalGate(channelGate, configStore, approvals);

        var decision = await gate.RequestAsync(
            new ToolInvocationPreview("mcp__filesystem__read", "Call MCP tool", "{}"), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Approve, decision);
        Assert.Equal(1, channelGate.RequestViaChannelAsyncCallCount);
        Assert.Equal(0, channelGate.RequestAsyncCallCount);
        Assert.Empty(approvals.List());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
