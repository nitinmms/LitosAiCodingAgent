using Litos.Tools.Mcp.Tests.Fakes;
using Litos.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.Tools.Mcp.Tests;

public class McpServerAdminTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}");

    private McpConfigStore NewStore() => new(Path.Combine(_tempDir, "mcp.json"));

    private static McpServerDefinition Server(string name, bool enabled = true, ToolPermission permission = ToolPermission.Full) => new(
        Name: name, Transport: McpTransportKind.Stdio, Command: "npx", Args: [], Env: null, Url: null,
        Enabled: enabled, DefaultPermission: permission, ToolOverrides: null);

    [Fact]
    public void AddServer_AppendsInOrder_AndPersists()
    {
        var store = NewStore();

        store.AddServer(Server("a"));
        store.AddServer(Server("b"));

        Assert.Equal(["a", "b"], NewStore().Current.Servers.Select(s => s.Name));
    }

    [Theory]
    [InlineData("a")] // duplicate
    [InlineData("bad__name")]
    public void AddServer_InvalidName_ThrowsValidationException_AndLeavesConfigUnchanged(string name)
    {
        var store = NewStore();
        store.AddServer(Server("a"));

        Assert.Throws<McpConfigValidationException>(() => store.AddServer(Server(name)));
        Assert.Equal(["a"], store.Current.Servers.Select(s => s.Name));
    }

    [Fact]
    public void SetServerEnabled_ChangesOnlyTheNamedServer()
    {
        var store = NewStore();
        store.AddServer(Server("a"));
        store.AddServer(Server("b"));

        store.SetServerEnabled("a", false);

        Assert.False(store.Current.Servers.Single(s => s.Name == "a").Enabled);
        Assert.True(store.Current.Servers.Single(s => s.Name == "b").Enabled);
    }

    [Fact]
    public void SetServerPermission_ChangesOnlyTheNamedServer_AndPersists()
    {
        var store = NewStore();
        store.AddServer(Server("a", permission: ToolPermission.Full));
        store.AddServer(Server("b", permission: ToolPermission.Deny));

        store.SetServerPermission("a", ToolPermission.Ask);

        var reloaded = NewStore().Current.Servers;
        Assert.Equal(ToolPermission.Ask, reloaded.Single(s => s.Name == "a").DefaultPermission);
        Assert.Equal(ToolPermission.Deny, reloaded.Single(s => s.Name == "b").DefaultPermission);
    }

    [Fact]
    public void Mutations_NamingAnUnknownServer_AreNoOps()
    {
        var store = NewStore();
        store.AddServer(Server("a"));

        store.SetServerEnabled("missing", false);
        store.SetServerPermission("missing", ToolPermission.Deny);
        store.RemoveServer("missing");

        Assert.Equal(Server("a"), store.Current.Servers.Single());
    }

    [Fact]
    public void RemoveServer_RemovesOnlyTheNamedServer()
    {
        var store = NewStore();
        store.AddServer(Server("a"));
        store.AddServer(Server("b"));

        store.RemoveServer("a");

        Assert.Equal(["b"], store.Current.Servers.Select(s => s.Name));
    }

    [Fact]
    public void ListServers_ServerWithNoConnectionYet_HasNullConnection_InConfigOrder()
    {
        var store = NewStore();
        store.AddServer(Server("b", enabled: false));
        store.AddServer(Server("a", enabled: false));
        var provider = new McpToolProvider(store, NullLoggerFactory.Instance, new FakeApprovalGate());

        var servers = store.ListServers(provider);

        Assert.Equal(["b", "a"], servers.Select(s => s.Definition.Name));
        Assert.All(servers, s => Assert.Null(s.Connection));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
