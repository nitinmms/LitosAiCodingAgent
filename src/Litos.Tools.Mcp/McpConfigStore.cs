namespace Litos.Tools.Mcp;

/// <summary>
/// Thrown by <see cref="McpConfigStore.Update"/> when a mutation would leave the config in an
/// invalid state — a server name containing "__" (which would make its mcp__{name}__{tool}
/// prefix ambiguous to split back apart) or two servers sharing the same name. Validation lives
/// here, not in the admin UI, so every mutation path (McpServers.razor today, any future caller)
/// is covered with no separate duplicate check needed anywhere else.
/// </summary>
public sealed class McpConfigValidationException(string message) : Exception(message);

/// <summary>
/// Thread-safe in-memory holder for the live McpConfig — mirrors TelegramConfigStore exactly
/// (single source of truth, Load/Save-backed, constructor-injectable path for tests). Lives in
/// Litos.Tools.Mcp rather than Litos.Api (unlike TelegramConfigStore) so it stays available to
/// Litos.Gui/Litos.Console if MCP support ever extends to those faces, even though only
/// Litos.Api wires it up today.
/// </summary>
public sealed class McpConfigStore
{
    private readonly Lock _lock = new();
    private readonly string _stateFilePath;
    private McpConfig _current;

    public McpConfigStore(string? stateFilePath = null)
    {
        _stateFilePath = stateFilePath ?? Path.Combine(DefaultStateDirectory(), "mcp.json");
        _current = McpConfig.Load(_stateFilePath);
    }

    private static string DefaultStateDirectory() =>
        Environment.GetEnvironmentVariable("LITOS_STATE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos");

    public McpConfig Current
    {
        get { lock (_lock) return _current; }
    }

    public void Update(Func<McpConfig, McpConfig> mutate)
    {
        lock (_lock)
        {
            var updated = mutate(_current);
            Validate(updated);
            _current = updated;
            _current.Save(_stateFilePath);
        }
    }

    private static void Validate(McpConfig config)
    {
        foreach (var server in config.Servers)
        {
            if (server.Name.Contains("__", StringComparison.Ordinal))
                throw new McpConfigValidationException(
                    $"Server name '{server.Name}' may not contain '__' — it would make the mcp__{{server}}__{{tool}} prefix ambiguous.");
        }

        var duplicate = config.Servers
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new McpConfigValidationException($"Duplicate server name '{duplicate.Key}'.");
    }
}
