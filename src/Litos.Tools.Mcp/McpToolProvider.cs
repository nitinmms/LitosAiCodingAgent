using Litos.Agent.Tools;
using Litos.Tools.Shell;
using Microsoft.Extensions.Logging;

namespace Litos.Tools.Mcp;

/// <summary>
/// Orchestrates every configured MCP server's connection lifecycle. Constructed directly with
/// new(...) in Litos.Api/Program.cs (not resolved via DI) before builder.Build() — InitializeAsync
/// is awaited there so every discovered tool is present in Tools by the time DI registers the
/// IToolSource wrapping this instance, since per-tool schemas (not a collapsed generic proxy) are
/// required for tool-selection quality on the very first turn.
///
/// After startup, McpToolRefreshService (a BackgroundService) calls RefreshAsync on a poll loop —
/// this is what picks up admin-page add/enable/disable/edit/remove (McpConfigStore.Update) and
/// retries Unreachable servers with backoff, without a container restart. RefreshAsync never
/// throws — a failed/timed-out server contributes zero tools and an Unreachable state, everything
/// else keeps working, mirroring the WebSearchTool-without-a-key precedent (register
/// unconditionally, report the failure at the point of discovery instead of at point-of-use since
/// there's no point-of-use for a tool that was never discovered).
/// </summary>
public sealed class McpToolProvider(McpConfigStore configStore, ILoggerFactory loggerFactory, IToolApprovalGate approvalGate)
{
    private readonly List<McpServerConnection> _connections = [];
    private readonly Lock _lock = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<McpToolProvider>();

    public IReadOnlyList<ITool> Tools { get; private set; } = [];

    /// <summary>
    /// Every connected server's prompts, aggregated the same way Tools is — rebuilt in the same
    /// RebuildToolsSnapshot pass so the two can never drift out of sync with each other or with
    /// Connections' live state (no separate refresh call/timer of its own).
    /// </summary>
    public IReadOnlyList<McpPromptEntry> Prompts { get; private set; } = [];

    public IReadOnlyList<McpServerConnection> Connections
    {
        get { lock (_lock) return [.. _connections]; }
    }

    /// <summary>First-ever connect, at startup — every enabled server, unconditionally attempted
    /// once regardless of Unreachable backoff state (nothing has failed yet).</summary>
    public Task InitializeAsync(TimeSpan perServerTimeout, CancellationToken ct) =>
        RefreshAsync(perServerTimeout, ct);

    /// <summary>
    /// Reconciles live connections against configStore.Current: starts connections for
    /// newly-added/newly-enabled/newly-edited servers, disposes and drops connections for
    /// removed/disabled/edited-under-the-same-name servers, and retries any Unreachable
    /// connection whose backoff window (McpServerConnection.NextRetryAt) has elapsed. Safe to
    /// call repeatedly — this is the same method both the one-shot startup path (InitializeAsync)
    /// and the periodic background poll (McpToolRefreshService) use.
    /// </summary>
    public async Task RefreshAsync(TimeSpan perServerTimeout, CancellationToken ct)
    {
        var desired = configStore.Current.Servers.Where(s => s.Enabled).ToDictionary(s => s.Name);

        List<McpServerConnection> toConnect;
        lock (_lock)
        {
            var stale = _connections
                .Where(c => !desired.TryGetValue(c.ServerName, out var def) || !DefinitionsMatch(c.Definition, def))
                .ToList();
            foreach (var connection in stale)
                _connections.Remove(connection);
            _ = DisposeAllAsync(stale); // fire-and-forget teardown; a slow dispose must not block reconnecting others

            var now = DateTimeOffset.UtcNow;
            var existingNames = _connections.Select(c => c.ServerName).ToHashSet();
            toConnect = desired.Values
                .Where(def => !existingNames.Contains(def.Name))
                .Select(def => new McpServerConnection(def, loggerFactory))
                .ToList();

            // Retry any still-live Unreachable connection whose backoff has elapsed — replaced
            // with a fresh McpServerConnection (status resets to Connecting) but carrying
            // ConsecutiveFailures forward, so backoff keeps growing and the MaxConsecutiveFailures
            // cap is reached across retries instead of every retry looking like the first one.
            // (Failed connections are deliberately excluded here — they've stopped retrying.)
            var dueForRetry = _connections
                .Where(c => c.Status == McpConnectionStatus.Unreachable && (c.NextRetryAt is null || c.NextRetryAt <= now))
                .ToList();
            foreach (var connection in dueForRetry)
            {
                _connections.Remove(connection);
                toConnect.Add(new McpServerConnection(connection.Definition, loggerFactory, connection.ConsecutiveFailures));
            }

            _connections.AddRange(toConnect);
        }

        await Task.WhenAll(toConnect.Select(c => ConnectOneAsync(c, perServerTimeout, ct)));

        RebuildToolsSnapshot();
        RebuildPromptsSnapshot();
    }

    /// <summary>
    /// Fields that determine whether an existing connection is still valid for a server's current
    /// definition — DefaultPermission/ToolOverrides deliberately excluded since McpAwareApprovalGate
    /// already reads McpConfigStore.Current fresh on every call, so permission changes apply live
    /// without needing a reconnect.
    /// </summary>
    private static bool DefinitionsMatch(McpServerDefinition a, McpServerDefinition b) =>
        a.Transport == b.Transport
        && a.Command == b.Command
        && (a.Args ?? []).SequenceEqual(b.Args ?? [])
        && (a.Env ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key)
            .SequenceEqual((b.Env ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key))
        && a.Url == b.Url;

    private void RebuildToolsSnapshot()
    {
        var tools = new List<ITool>();
        lock (_lock)
        {
            foreach (var connection in _connections.Where(c => c.Status == McpConnectionStatus.Connected))
            {
                var serverName = connection.ServerName;
                foreach (var clientTool in connection.Tools)
                    tools.Add(new McpToolProxy(serverName, clientTool, connection, approvalGate));
            }
        }

        Tools = tools; // atomic reference swap — IToolSource.CurrentTools readers never see a half-built list
    }

    private void RebuildPromptsSnapshot()
    {
        var prompts = new List<McpPromptEntry>();
        lock (_lock)
        {
            foreach (var connection in _connections.Where(c => c.Status == McpConnectionStatus.Connected))
            {
                var serverName = connection.ServerName;
                foreach (var prompt in connection.Prompts)
                    prompts.Add(new McpPromptEntry(serverName, prompt, connection));
            }
        }

        Prompts = prompts; // atomic reference swap, same reasoning as Tools above
    }

    private async Task ConnectOneAsync(McpServerConnection connection, TimeSpan perServerTimeout, CancellationToken ct)
    {
        try
        {
            await connection.ConnectAsync(perServerTimeout, ct);
        }
        catch (Exception ex)
        {
            // ConnectAsync itself already catches connection-level failures into Unreachable —
            // this is a last-resort guard so one server's unexpected exception can never take
            // down Task.WhenAll for every other server.
            _logger.LogWarning(ex, "Unexpected failure connecting to MCP server '{Server}'.", connection.ServerName);
        }
    }

    private async Task DisposeAllAsync(IReadOnlyList<McpServerConnection> connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP server connection '{Server}'.", connection.ServerName);
            }
        }
    }

    public async Task ShutdownAsync()
    {
        List<McpServerConnection> connections;
        lock (_lock)
            connections = [.. _connections];

        await DisposeAllAsync(connections);
    }
}
