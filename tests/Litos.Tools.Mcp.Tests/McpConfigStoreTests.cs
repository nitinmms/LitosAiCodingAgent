using Litos.Tools.Shell;

namespace Litos.Tools.Mcp.Tests;

public class McpConfigStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}");
    private string StateFilePath => Path.Combine(_tempDir, "mcp.json");

    private static McpServerDefinition StdioServer(string name) => new(
        Name: name,
        Transport: McpTransportKind.Stdio,
        Command: "npx",
        Args: ["-y", "@modelcontextprotocol/server-everything"],
        Env: null,
        Url: null,
        Enabled: true,
        DefaultPermission: ToolPermission.Ask,
        ToolOverrides: null);

    [Fact]
    public void Constructor_NoFileOnDisk_StartsEmpty()
    {
        var store = new McpConfigStore(StateFilePath);

        Assert.Empty(store.Current.Servers);
    }

    [Fact]
    public void Update_PersistsToDisk_AndIsReloadedByANewStore()
    {
        var store = new McpConfigStore(StateFilePath);

        store.Update(c => c with { Servers = [StdioServer("filesystem")] });

        Assert.True(File.Exists(StateFilePath));

        var reloaded = new McpConfigStore(StateFilePath);
        Assert.Single(reloaded.Current.Servers);
        Assert.Equal("filesystem", reloaded.Current.Servers[0].Name);
        Assert.Equal(ToolPermission.Ask, reloaded.Current.Servers[0].DefaultPermission);
    }

    [Fact]
    public void Update_ServerNameContainingDoubleUnderscore_Throws()
    {
        var store = new McpConfigStore(StateFilePath);

        Assert.Throws<McpConfigValidationException>(() =>
            store.Update(c => c with { Servers = [StdioServer("bad__name")] }));
    }

    [Fact]
    public void Update_DuplicateServerNames_Throws()
    {
        var store = new McpConfigStore(StateFilePath);

        Assert.Throws<McpConfigValidationException>(() =>
            store.Update(c => c with { Servers = [StdioServer("dup"), StdioServer("dup")] }));
    }

    [Fact]
    public void Update_InvalidMutation_DoesNotChangeCurrent()
    {
        var store = new McpConfigStore(StateFilePath);
        store.Update(c => c with { Servers = [StdioServer("good")] });

        Assert.ThrowsAny<McpConfigValidationException>(() =>
            store.Update(c => c with { Servers = [.. c.Servers, StdioServer("bad__name")] }));

        Assert.Single(store.Current.Servers);
        Assert.Equal("good", store.Current.Servers[0].Name);
    }

    [Fact]
    public void Update_MalformedFileOnDisk_FallsBackToEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(StateFilePath, "{ not valid json");

        var store = new McpConfigStore(StateFilePath);

        Assert.Empty(store.Current.Servers);
    }

    [Fact]
    public void PermissionFor_ToolOverridePresent_TakesPrecedenceOverDefault()
    {
        var server = StdioServer("filesystem") with
        {
            DefaultPermission = ToolPermission.Ask,
            ToolOverrides = new Dictionary<string, ToolPermission> { ["mcp__filesystem__delete"] = ToolPermission.Deny },
        };

        Assert.Equal(ToolPermission.Deny, server.PermissionFor("mcp__filesystem__delete"));
        Assert.Equal(ToolPermission.Ask, server.PermissionFor("mcp__filesystem__read"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
