using System.Text.RegularExpressions;

namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>The names the factory keeps secrets under (m3-architecture.md §3.1).</summary>
public static partial class SecretNames
{
    /// <summary>The factory-wide GitHub token (m3-architecture.md §2).</summary>
    public const string GitHub = "github";

    /// <summary>The web search provider's key (m3-architecture.md §6).</summary>
    public const string WebSearch = "websearch:tavily";

    /// <summary>A model provider's API key, by the engine's provider name.</summary>
    public static string Provider(string provider) => $"provider:{provider}";

    /// <summary>One environment variable of an MCP server.</summary>
    public static string Mcp(string server, string variable) => $"mcp:{server}:{variable}";

    public static bool IsValid(string name) => name is GitHub or WebSearch || Pattern().IsMatch(name);

    [GeneratedRegex(@"^(provider:[a-z][a-z0-9_]{0,39}|mcp:[A-Za-z0-9][A-Za-z0-9_.-]{0,63}:[A-Za-z_][A-Za-z0-9_]{0,99})$")]
    private static partial Regex Pattern();
}
