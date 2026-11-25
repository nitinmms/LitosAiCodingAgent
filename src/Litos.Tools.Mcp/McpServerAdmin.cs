using Litos.Tools.Shell;

namespace Litos.Tools.Mcp;

/// <summary>A configured server joined with its live connection (null when no connection has
/// been attempted yet — disabled, or added since the last refresh).</summary>
public sealed record McpServerStatus(McpServerDefinition Definition, McpServerConnection? Connection);

/// <summary>
/// The server-list mutations every MCP admin surface performs — Litos.Gui's McpServersWindow and
/// Litos.VsCodeHost's /mcp endpoints — kept in one place so both faces add, enable, re-permission
/// and remove servers identically. Validation stays in <see cref="McpConfigStore.Update"/>; these
/// only shape the mutation. Mutations naming an unknown server are no-ops, not errors, matching
/// how both surfaces always behaved.
/// </summary>
public static class McpServerAdmin
{
    /// <summary>Appends <paramref name="definition"/>.</summary>
    /// <exception cref="McpConfigValidationException">The name is a duplicate or contains "__".</exception>
    public static void AddServer(this McpConfigStore store, McpServerDefinition definition) =>
        store.Update(cfg => cfg with { Servers = [.. cfg.Servers, definition] });

    public static void SetServerEnabled(this McpConfigStore store, string name, bool enabled) =>
        UpdateServer(store, name, s => s with { Enabled = enabled });

    /// <summary>Takes effect on the next tool call with no reconnect: McpAwareApprovalGate reads
    /// the store fresh per call, and McpToolProvider excludes permissions from the comparison that
    /// decides whether a server needs reconnecting.</summary>
    public static void SetServerPermission(this McpConfigStore store, string name, ToolPermission permission) =>
        UpdateServer(store, name, s => s with { DefaultPermission = permission });

    public static void RemoveServer(this McpConfigStore store, string name) =>
        store.Update(cfg => cfg with { Servers = [.. cfg.Servers.Where(s => s.Name != name)] });

    /// <summary>Every configured server, in config order, with its live connection if any.</summary>
    public static IReadOnlyList<McpServerStatus> ListServers(this McpConfigStore store, McpToolProvider provider)
    {
        var connectionsByName = provider.Connections.ToDictionary(c => c.ServerName);
        return store.Current.Servers
            .Select(server => new McpServerStatus(server, connectionsByName.GetValueOrDefault(server.Name)))
            .ToList();
    }

    private static void UpdateServer(McpConfigStore store, string name, Func<McpServerDefinition, McpServerDefinition> change) =>
        store.Update(cfg => cfg with { Servers = [.. cfg.Servers.Select(s => s.Name == name ? change(s) : s)] });
}
