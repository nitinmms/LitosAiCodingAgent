using Litos.Agent.Tools;
using Litos.Hosting;
using Litos.SoftwareFactory.Contracts;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The run's MCP servers (m3-architecture.md §7.2), read from the mcp.json the host wrote for it.
/// They connect once, when the worker starts, and the worker reports ready only after each has
/// connected or failed. A run gets a server's tools only where its permission is Full: a denied
/// tool is never offered, and the approval gate refuses it as well.
/// </summary>
public sealed class WorkerMcp
{
    /// <summary>The most one server's handshake may take (blueprint §8.1).</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly McpConfigStore _store;
    private readonly McpToolProvider _provider;

    public WorkerMcp(string configPath, ILoggerFactory loggerFactory)
    {
        _store = new McpConfigStore(configPath);
        var gate = new McpAwareApprovalGate(new AutoApprovalGate(), _store, new PendingApprovalStore());
        _provider = new McpToolProvider(_store, loggerFactory, gate);
    }

    /// <summary>Connects every server, and says how each went.</summary>
    public async Task<IReadOnlyList<McpServerReport>> ConnectAsync(CancellationToken ct)
    {
        await _provider.InitializeAsync(HandshakeTimeout, ct);
        return Reports();
    }

    /// <summary>The tools the run may call: those of connected servers whose permission is Full.</summary>
    public IEnumerable<ITool> PermittedTools() => _provider.Tools.Where(tool => IsPermitted(tool.Name));

    public IReadOnlyList<McpServerReport> Reports() =>
    [
        .. _provider.Connections.Select(c => new McpServerReport(
            c.ServerName,
            c.Status == McpConnectionStatus.Connected,
            [.. c.Tools.Select(t => t.Name).Where(name => IsPermitted($"mcp__{c.ServerName}__{name}"))],
            c.Error)),
    ];

    private bool IsPermitted(string fullToolName) =>
        _store.Current.Servers.FirstOrDefault(s => fullToolName.StartsWith($"mcp__{s.Name}__", StringComparison.Ordinal))
            ?.PermissionFor(fullToolName) == ToolPermission.Full;
}
