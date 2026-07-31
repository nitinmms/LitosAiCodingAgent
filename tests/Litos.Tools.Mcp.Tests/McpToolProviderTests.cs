using Litos.Tools.Mcp.Tests.Fakes;
using Litos.Tools.Shell;

namespace Litos.Tools.Mcp.Tests;

public class McpToolProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}");
    private string StateFilePath => Path.Combine(_tempDir, "mcp.json");

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    // A command guaranteed not to exist on any OS this runs on — ConnectAsync fails fast (process
    // spawn error, not a handshake timeout), letting these tests exercise Unreachable/backoff
    // behavior deterministically without needing a real MCP server binary or waiting out a timeout.
    private static McpServerDefinition BogusServer(string name, bool enabled = true) => new(
        Name: name,
        Transport: McpTransportKind.Stdio,
        Command: "litos-test-nonexistent-command-xyz",
        Args: [],
        Env: null,
        Url: null,
        Enabled: enabled,
        DefaultPermission: ToolPermission.Ask,
        ToolOverrides: null);

    private McpToolProvider CreateProvider(McpConfigStore configStore) =>
        new(configStore, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new FakeApprovalGate());

    [Fact]
    public async Task InitializeAsync_NoServersConfigured_YieldsNoTools()
    {
        var configStore = new McpConfigStore(StateFilePath);
        var provider = CreateProvider(configStore);

        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);

        Assert.Empty(provider.Tools);
        Assert.Empty(provider.Connections);
    }

    [Fact]
    public async Task InitializeAsync_DisabledServer_NeverAttemptsConnection()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [BogusServer("disabled", enabled: false)] });
        var provider = CreateProvider(configStore);

        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);

        Assert.Empty(provider.Connections);
    }

    [Fact]
    public async Task InitializeAsync_UnreachableServer_MarksUnreachable_ContributesNoTools()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [BogusServer("bad")] });
        var provider = CreateProvider(configStore);

        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);

        Assert.Empty(provider.Tools);
        var connection = Assert.Single(provider.Connections);
        Assert.Equal(McpConnectionStatus.Unreachable, connection.Status);
        Assert.NotNull(connection.Error);
        Assert.NotNull(connection.NextRetryAt);
    }

    [Fact]
    public async Task RefreshAsync_ServerRemovedFromConfig_DropsItsConnection()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [BogusServer("gone")] });
        var provider = CreateProvider(configStore);
        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);
        Assert.Single(provider.Connections);

        configStore.Update(c => c with { Servers = [] });
        await provider.RefreshAsync(ShortTimeout, CancellationToken.None);

        Assert.Empty(provider.Connections);
    }

    [Fact]
    public async Task RefreshAsync_ServerDisabled_DropsItsConnection()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [BogusServer("toggled")] });
        var provider = CreateProvider(configStore);
        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);
        Assert.Single(provider.Connections);

        configStore.Update(c => c with { Servers = [BogusServer("toggled", enabled: false)] });
        await provider.RefreshAsync(ShortTimeout, CancellationToken.None);

        Assert.Empty(provider.Connections);
    }

    [Fact]
    public async Task RefreshAsync_NewlyEnabledServer_AttemptsConnection()
    {
        var configStore = new McpConfigStore(StateFilePath);
        var provider = CreateProvider(configStore);
        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);
        Assert.Empty(provider.Connections);

        configStore.Update(c => c with { Servers = [BogusServer("newcomer")] });
        await provider.RefreshAsync(ShortTimeout, CancellationToken.None);

        var connection = Assert.Single(provider.Connections);
        Assert.Equal("newcomer", connection.ServerName);
        Assert.Equal(McpConnectionStatus.Unreachable, connection.Status);
    }

    [Fact]
    public async Task RefreshAsync_UnreachableServer_BeforeBackoffElapses_DoesNotRetry()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [BogusServer("bad")] });
        var provider = CreateProvider(configStore);
        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);
        var firstAttempt = Assert.Single(provider.Connections);
        var firstNextRetryAt = firstAttempt.NextRetryAt;

        // Config unchanged, backoff (>=15s per McpServerConnection.MinBackoff) hasn't elapsed —
        // RefreshAsync should leave the existing Unreachable connection alone rather than
        // replacing it with a fresh attempt every poll tick.
        await provider.RefreshAsync(ShortTimeout, CancellationToken.None);

        var stillThere = Assert.Single(provider.Connections);
        Assert.Same(firstAttempt, stillThere);
        Assert.Equal(firstNextRetryAt, stillThere.NextRetryAt);
    }

    [Fact]
    public async Task RefreshAsync_EditedServerDefinition_ReconnectsUnderSameName()
    {
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [BogusServer("edited")] });
        var provider = CreateProvider(configStore);
        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);
        var original = Assert.Single(provider.Connections);

        var edited = BogusServer("edited") with { Args = ["--different-flag"] };
        configStore.Update(c => c with { Servers = [edited] });
        await provider.RefreshAsync(ShortTimeout, CancellationToken.None);

        var reconnected = Assert.Single(provider.Connections);
        Assert.Equal("edited", reconnected.ServerName);
        Assert.NotSame(original, reconnected);
    }

    [Fact]
    public async Task RefreshAsync_PermissionOnlyChange_DoesNotReconnect()
    {
        // DefaultPermission/ToolOverrides deliberately excluded from the reconnect-triggering
        // definition comparison — McpAwareApprovalGate reads McpConfigStore.Current fresh per
        // call, so permission changes apply live without needing a connection teardown.
        var configStore = new McpConfigStore(StateFilePath);
        configStore.Update(c => c with { Servers = [BogusServer("stable")] });
        var provider = CreateProvider(configStore);
        await provider.InitializeAsync(ShortTimeout, CancellationToken.None);
        var original = Assert.Single(provider.Connections);

        configStore.Update(c => c with
        {
            Servers = [BogusServer("stable") with { DefaultPermission = ToolPermission.Full }],
        });
        await provider.RefreshAsync(ShortTimeout, CancellationToken.None);

        var stillSame = Assert.Single(provider.Connections);
        Assert.Same(original, stillSame);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
