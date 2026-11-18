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

    /// <summary>
    /// A value one project's web-app contract names as <c>{secret:NAME}</c>, such as a test role's
    /// password (testability-architecture.md §5). It reaches only that project's app processes and
    /// the browser's sign-in, never a brief, a worker or a verification command.
    /// </summary>
    public static string Project(Guid projectId, string name) => $"{ProjectPrefix(projectId)}{name}";

    /// <summary>What every secret of one project starts with.</summary>
    public static string ProjectPrefix(Guid projectId) => $"project:{projectId:N}:";

    /// <summary>Whether <paramref name="name"/> can follow <c>{secret:</c>: a variable-style name.</summary>
    public static bool IsValidProjectSecretName(string name) => ProjectNamePattern().IsMatch(name);

    public static bool IsValid(string name) => name is GitHub or WebSearch || Pattern().IsMatch(name);

    /// <summary>Whether the name is one of a project's own secrets, and whose.</summary>
    public static bool IsProjectSecret(string name, out Guid projectId, out string secretName)
    {
        projectId = Guid.Empty;
        secretName = "";
        var match = ProjectSecretPattern().Match(name);
        if (!match.Success)
            return false;
        projectId = Guid.ParseExact(match.Groups[1].Value, "N");
        secretName = match.Groups[2].Value;
        return true;
    }

    [GeneratedRegex(@"^(provider:[a-z][a-z0-9_]{0,39}|mcp:[A-Za-z0-9][A-Za-z0-9_.-]{0,63}:[A-Za-z_][A-Za-z0-9_]{0,99}|project:[0-9a-f]{32}:[A-Za-z_][A-Za-z0-9_]{0,99})$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^project:([0-9a-f]{32}):([A-Za-z_][A-Za-z0-9_]{0,99})$")]
    private static partial Regex ProjectSecretPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    private static partial Regex ProjectNamePattern();
}
