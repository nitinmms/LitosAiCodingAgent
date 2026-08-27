using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Litos.VsCodeHost.Mcp;

namespace Litos.VsCodeHost.Tests.Mcp;

/// <summary>
/// Covers the mutation POST /mcp/servers/{name}/permission performs — the same seam
/// McpAwareApprovalGateWiringTests.cs exercises behavior through, since this project has no
/// WebApplicationFactory/HTTP test harness and the route handlers in McpEndpoints.cs are thin
/// wrappers around McpConfigStore.Update. Added because before this endpoint existed, a server's
/// DefaultPermission (Deny/Ask/Full) could only be set at add-time — see McpEndpoints.cs's
/// "/permission" route for the mirror of the pre-existing "/enabled" route this follows.
/// </summary>
public sealed class McpEndpointsPermissionTests
{
    private static McpConfigStore NewConfigStore(string tempDir) =>
        new(Path.Combine(tempDir, "mcp.json"));

    private static McpServerDefinition StdioServer(string name, ToolPermission defaultPermission) => new(
        Name: name, Transport: McpTransportKind.Stdio, Command: "npx", Args: [], Env: null, Url: null,
        Enabled: true, DefaultPermission: defaultPermission, ToolOverrides: null);

    // Mirrors the exact mutation McpEndpoints.cs's "/mcp/servers/{name}/permission" handler runs.
    private static void SetDefaultPermission(McpConfigStore configStore, string name, SetDefaultPermissionRequest request) =>
        configStore.Update(cfg => cfg with
        {
            Servers = [.. cfg.Servers.Select(s => s.Name == name ? s with { DefaultPermission = request.DefaultPermission } : s)],
        });

    [Fact]
    public void ChangesAnExistingServersDefaultPermission()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configStore = NewConfigStore(tempDir);
            configStore.Update(cfg => cfg with { Servers = [StdioServer("myserver", ToolPermission.Deny)] });

            SetDefaultPermission(configStore, "myserver", new SetDefaultPermissionRequest(ToolPermission.Full));

            Assert.Equal(ToolPermission.Full, configStore.Current.Servers.Single(s => s.Name == "myserver").DefaultPermission);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void OnlyTheNamedServerIsAffected_OthersKeepTheirOwnPermission()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configStore = NewConfigStore(tempDir);
            configStore.Update(cfg => cfg with
            {
                Servers = [StdioServer("a", ToolPermission.Deny), StdioServer("b", ToolPermission.Ask)],
            });

            SetDefaultPermission(configStore, "a", new SetDefaultPermissionRequest(ToolPermission.Full));

            Assert.Equal(ToolPermission.Full, configStore.Current.Servers.Single(s => s.Name == "a").DefaultPermission);
            Assert.Equal(ToolPermission.Ask, configStore.Current.Servers.Single(s => s.Name == "b").DefaultPermission);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void UnknownServerName_IsANoOp_RatherThanThrowing()
    {
        // Matches the existing "/enabled" route's behavior (Select() with no match just leaves
        // the list unchanged) — the permission route follows the same convention rather than
        // inventing a 404 case the sibling route doesn't have.
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configStore = NewConfigStore(tempDir);
            configStore.Update(cfg => cfg with { Servers = [StdioServer("myserver", ToolPermission.Deny)] });

            SetDefaultPermission(configStore, "doesnotexist", new SetDefaultPermissionRequest(ToolPermission.Full));

            Assert.Equal(ToolPermission.Deny, configStore.Current.Servers.Single(s => s.Name == "myserver").DefaultPermission);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChangeIsVisibleToANewlyConstructedGate_WithNoReconnectNeeded()
    {
        // McpAwareApprovalGate reads McpConfigStore.Current fresh on every RequestAsync call (see
        // McpToolProvider.DefinitionsMatch's remarks on why DefaultPermission is excluded from the
        // reconnect-triggering comparison) — this proves the permission endpoint's mutation is
        // actually the live value that gate would see, matching the "no refreshMcpServers() call"
        // decision made in extension.ts's setDefaultPermission case.
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configStore = NewConfigStore(tempDir);
            configStore.Update(cfg => cfg with { Servers = [StdioServer("myserver", ToolPermission.Deny)] });
            var gate = new McpAwareApprovalGate(new AutoApprovalGate(), configStore, new PendingApprovalStore(TimeSpan.FromMinutes(10)));

            SetDefaultPermission(configStore, "myserver", new SetDefaultPermissionRequest(ToolPermission.Full));

            var decision = await gate.RequestAsync(new ToolInvocationPreview("mcp__myserver__read", "read a file", null), CancellationToken.None);
            Assert.Equal(ApprovalDecision.Approve, decision);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PersistsAcrossAConfigStoreReload()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var configStore = NewConfigStore(tempDir);
            configStore.Update(cfg => cfg with { Servers = [StdioServer("myserver", ToolPermission.Deny)] });

            SetDefaultPermission(configStore, "myserver", new SetDefaultPermissionRequest(ToolPermission.Ask));

            var reloaded = NewConfigStore(tempDir);
            Assert.Equal(ToolPermission.Ask, reloaded.Current.Servers.Single(s => s.Name == "myserver").DefaultPermission);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
