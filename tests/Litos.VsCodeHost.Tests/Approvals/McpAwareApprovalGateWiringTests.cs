using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Litos.VsCodeHost;

namespace Litos.VsCodeHost.Tests.Approvals;

/// <summary>
/// Confirms the exact gate composition Program.cs wires: McpAwareApprovalGate(AutoApprovalGate, ...).
/// Built-in tools stay blanket-approved (Litos.Gui's model); MCP tools are gated per-server via
/// McpConfigStore (Litos.Api's model), defaulting to Deny when unconfigured — same safety default
/// McpAwareApprovalGate itself falls back to when a tool's server isn't found at all.
/// </summary>
public class McpAwareApprovalGateWiringTests
{
    private static McpConfigStore NewConfigStore(string tempDir) =>
        new(Path.Combine(tempDir, "mcp.json"));

    [Fact]
    public async Task BuiltInTool_IsBlanketApproved_MatchingLitosGui()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var gate = new McpAwareApprovalGate(new AutoApprovalGate(), NewConfigStore(tempDir), new PendingApprovalStore(TimeSpan.FromMinutes(10)));

            var decision = await gate.RequestAsync(new ToolInvocationPreview("shell", "run a command", "echo hi"), CancellationToken.None);

            Assert.Equal(ApprovalDecision.Approve, decision);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task McpTool_WithNoConfiguredServer_DeniesByDefault()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var gate = new McpAwareApprovalGate(new AutoApprovalGate(), NewConfigStore(tempDir), new PendingApprovalStore(TimeSpan.FromMinutes(10)));

            var decision = await gate.RequestAsync(new ToolInvocationPreview("mcp__unknownserver__read", "read a file", null), CancellationToken.None);

            Assert.Equal(ApprovalDecision.Deny, decision);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task McpTool_ServerConfiguredFull_Approves()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configStore = NewConfigStore(tempDir);
            configStore.Update(cfg => cfg with
            {
                Servers = [.. cfg.Servers, new McpServerDefinition(
                    Name: "myserver", Transport: McpTransportKind.Stdio, Command: "npx", Args: [], Env: null, Url: null,
                    Enabled: true, DefaultPermission: ToolPermission.Full, ToolOverrides: null)],
            });
            var gate = new McpAwareApprovalGate(new AutoApprovalGate(), configStore, new PendingApprovalStore(TimeSpan.FromMinutes(10)));

            var decision = await gate.RequestAsync(new ToolInvocationPreview("mcp__myserver__read", "read a file", null), CancellationToken.None);

            Assert.Equal(ApprovalDecision.Approve, decision);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task McpTool_ServerConfiguredDeny_Denies()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configStore = NewConfigStore(tempDir);
            configStore.Update(cfg => cfg with
            {
                Servers = [.. cfg.Servers, new McpServerDefinition(
                    Name: "myserver", Transport: McpTransportKind.Stdio, Command: "npx", Args: [], Env: null, Url: null,
                    Enabled: true, DefaultPermission: ToolPermission.Deny, ToolOverrides: null)],
            });
            var gate = new McpAwareApprovalGate(new AutoApprovalGate(), configStore, new PendingApprovalStore(TimeSpan.FromMinutes(10)));

            var decision = await gate.RequestAsync(new ToolInvocationPreview("mcp__myserver__read", "read a file", null), CancellationToken.None);

            Assert.Equal(ApprovalDecision.Deny, decision);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task McpTool_ServerConfiguredAsk_SuspendsUntilResolved()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configStore = NewConfigStore(tempDir);
            configStore.Update(cfg => cfg with
            {
                Servers = [.. cfg.Servers, new McpServerDefinition(
                    Name: "myserver", Transport: McpTransportKind.Stdio, Command: "npx", Args: [], Env: null, Url: null,
                    Enabled: true, DefaultPermission: ToolPermission.Ask, ToolOverrides: null)],
            });
            var approvals = new PendingApprovalStore(TimeSpan.FromMinutes(10));
            var gate = new McpAwareApprovalGate(new AutoApprovalGate(), configStore, approvals);

            var decisionTask = gate.RequestAsync(new ToolInvocationPreview("mcp__myserver__read", "read a file", null), CancellationToken.None);
            await Task.Delay(20); // Let RequestAsync reach approvals.Add and suspend.

            var pending = Assert.Single(approvals.List());
            approvals.Resolve(pending.Id, ApprovalDecision.Approve);

            Assert.Equal(ApprovalDecision.Approve, await decisionTask);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
