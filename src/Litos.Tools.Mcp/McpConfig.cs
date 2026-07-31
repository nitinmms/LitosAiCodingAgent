using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Tools.Shell;

namespace Litos.Tools.Mcp;

public enum McpTransportKind
{
    Stdio,
    Http,
}

/// <summary>
/// One configured MCP server. Name is both the unique config key and the mcp__{Name}__ prefix
/// every discovered tool from this server is registered under — McpConfigStore rejects a Name
/// containing "__" or duplicating an existing server's Name (see McpConfigStore.Validate), so
/// the prefix can never collide with another server's or be ambiguous to split back apart.
/// </summary>
public sealed record McpServerDefinition(
    string Name,
    McpTransportKind Transport,
    string? Command,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    string? Url,
    bool Enabled,
    ToolPermission DefaultPermission,
    IReadOnlyDictionary<string, ToolPermission>? ToolOverrides)
{
    /// <summary>Per-tool override for the full mcp__{server}__{tool} name, falling back to
    /// DefaultPermission, falling back to Deny — the same safe-by-default fallback
    /// TelegramConfig.PermissionFor uses.</summary>
    public ToolPermission PermissionFor(string fullToolName) =>
        ToolOverrides is not null && ToolOverrides.TryGetValue(fullToolName, out var overridden)
            ? overridden
            : DefaultPermission;
}

public sealed record McpConfig(IReadOnlyList<McpServerDefinition> Servers)
{
    public static McpConfig Empty { get; } = new([]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static McpConfig Load(string path)
    {
        if (!File.Exists(path))
            return Empty;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<McpConfig>(json, JsonOptions) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}
