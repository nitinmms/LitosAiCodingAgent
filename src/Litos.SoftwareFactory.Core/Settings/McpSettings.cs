using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Litos.SoftwareFactory.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransport
{
    /// <summary>A command the worker starts, spoken to over its standard input and output.</summary>
    Stdio,

    /// <summary>A server already running at a URL.</summary>
    Http,
}

/// <summary>What a run may do with a server's tool. No Ask: nobody watches a run (m3-architecture.md §2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpAccess
{
    Full,
    Deny,
}

/// <summary>
/// One MCP server (blueprint §8.1, m3-architecture.md §7.1), in the engine's shape. The values of
/// its environment variables are secrets (SecretNames.Mcp), never kept here: only their names.
/// </summary>
public sealed record McpServerSettings
{
    public required string Name { get; init; }
    public McpTransport Transport { get; init; }

    /// <summary>For Stdio: the command and its arguments.</summary>
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>For Http: where it is.</summary>
    public string? Url { get; init; }

    /// <summary>Given to runs. A server that is off is kept, with its secrets, but no run starts it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>What a run may do with its tools, unless one is named in <see cref="ToolOverrides"/>.</summary>
    public McpAccess Permission { get; init; } = McpAccess.Full;

    /// <summary>Exceptions by the server's own tool name: a tool denied on a server that is Full, or allowed on one that is Deny.</summary>
    public IReadOnlyDictionary<string, McpAccess> ToolOverrides { get; init; } = new Dictionary<string, McpAccess>();

    /// <summary>The names of the environment variables it is started with; each value is a secret.</summary>
    public IReadOnlyList<string> SecretVariables { get; init; } = [];

    public McpAccess AccessTo(string tool) => ToolOverrides.TryGetValue(tool, out var access) ? access : Permission;
}

/// <summary>The MCP servers tab (blueprint §8.1). The list applies to every project.</summary>
public sealed partial record McpSettings
{
    public IReadOnlyList<McpServerSettings> Servers { get; init; } = [];

    public const int MaxServers = 20;

    /// <summary>What is wrong with these settings, one sentence each; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Servers.Count > MaxServers)
            errors.Add($"At most {MaxServers} MCP servers can be set up.");

        foreach (var duplicate in Servers.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"There are two MCP servers named \"{duplicate.Key}\".");

        foreach (var server in Servers)
        {
            // The engine names each tool mcp__{server}__{tool}, so "__" would make that ambiguous.
            if (!NamePattern().IsMatch(server.Name) || server.Name.Contains("__", StringComparison.Ordinal))
            {
                errors.Add($"\"{server.Name}\" is not a usable server name: letters, digits, '.', '-' and single '_', up to 64, starting with a letter or digit.");
                continue;
            }

            var name = server.Name;
            if (server.Transport == McpTransport.Stdio && string.IsNullOrWhiteSpace(server.Command))
                errors.Add($"{name}: give the command that starts it.");
            if (server.Transport == McpTransport.Http
                && !(Uri.TryCreate(server.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"))
                errors.Add($"{name}: its address must be an http or https URL.");
            foreach (var variable in server.SecretVariables.Where(v => !VariablePattern().IsMatch(v)))
                errors.Add($"{name}: \"{variable}\" is not an environment variable name.");
            foreach (var duplicate in server.SecretVariables.GroupBy(v => v).Where(g => g.Count() > 1))
                errors.Add($"{name}: {duplicate.Key} is listed twice.");
            foreach (var tool in server.ToolOverrides.Keys.Where(t => string.IsNullOrWhiteSpace(t) || t.Length > 200))
                errors.Add($"{name}: \"{tool}\" is not a tool name.");
        }

        return errors;
    }

    public McpServerSettings? Server(string name) => Servers.FirstOrDefault(s => s.Name == name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    private static partial Regex VariablePattern();
}
