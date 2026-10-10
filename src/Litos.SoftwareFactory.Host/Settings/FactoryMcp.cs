using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Infrastructure.Processes;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;

namespace Litos.SoftwareFactory.Host.Settings;

/// <summary>
/// The factory's MCP servers in the engine's terms (m3-architecture.md §7): a definition with its
/// secrets filled in, for a run's mcp.json or for Test connection.
/// </summary>
public static class FactoryMcp
{
    /// <summary>The engine's name for one of a server's tools.</summary>
    public static string ToolName(string server, string tool) => $"mcp__{server}__{tool}";

    /// <summary>The server's environment variables that are set, with their values.</summary>
    public static Dictionary<string, string> Secrets(FactorySettings settings, McpServerSettings server)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in server.SecretVariables)
        {
            if (settings.Secret(SecretNames.Mcp(server.Name, variable)) is { } value)
                values[variable] = value;
        }

        return values;
    }

    /// <summary>The server as the engine starts it: Full or Deny, never Ask, with tool overrides under their full names.</summary>
    public static McpServerDefinition Definition(McpServerSettings server, IReadOnlyDictionary<string, string> environment, bool inheritEnvironment) =>
        new(
            server.Name,
            server.Transport == McpTransport.Http ? McpTransportKind.Http : McpTransportKind.Stdio,
            server.Command,
            server.Args,
            environment,
            server.Url,
            Enabled: true,
            Permission(server.Permission),
            server.ToolOverrides.ToDictionary(o => ToolName(server.Name, o.Key), o => Permission(o.Value)))
        {
            InheritEnvironment = inheritEnvironment,
        };

    private static ToolPermission Permission(McpAccess access) => access == McpAccess.Full ? ToolPermission.Full : ToolPermission.Deny;

    /// <summary>
    /// Writes a run's servers, secrets included, where its worker reads them (§7.2). The worker's
    /// own environment is already only the toolchain allowlist, so a server it starts inherits that.
    /// Anyone who can read the run's folder can read this file, the agent's shell included; under
    /// the trusted-user rule (blueprint §17) that is noted, not hidden.
    /// </summary>
    public static void WriteRunConfig(string path, IEnumerable<McpServerSettings> servers, FactorySettings settings)
    {
        var config = new McpConfig([.. servers.Select(s => Definition(s, Secrets(settings, s), inheritEnvironment: true))]);
        config.Save(path);
    }

    /// <summary>
    /// What a server is started with when the host tests it: the toolchain allowlist a worker gets
    /// and the server's own variables, nothing else of the host's, whose environment may hold its
    /// database connection or keys.
    /// </summary>
    public static Dictionary<string, string> TestEnvironment(IReadOnlyDictionary<string, string> hostEnvironment, IReadOnlyDictionary<string, string> secrets)
    {
        var environment = ToolchainEnvironment.Scrub(hostEnvironment);
        foreach (var (name, value) in secrets)
            environment[name] = value;
        return environment;
    }
}

/// <summary>A server's Test connection result: the tools it offers, or why it could not be reached.</summary>
public sealed record McpTestResult(bool Connected, IReadOnlyList<McpTestedTool> Tools, string? Error);

public sealed record McpTestedTool(string Name, string? Description);

/// <summary>Starts a server once and lists its tools (blueprint §8.1). Replaced in tests.</summary>
public interface IMcpConnectionTester
{
    Task<McpTestResult> TestAsync(McpServerSettings server, CancellationToken ct);
}

public sealed class McpConnectionTester(FactorySettings settings, ILoggerFactory loggerFactory) : IMcpConnectionTester
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<McpTestResult> TestAsync(McpServerSettings server, CancellationToken ct)
    {
        var environment = FactoryMcp.TestEnvironment(ToolchainEnvironment.CurrentHostEnvironment(), FactoryMcp.Secrets(settings, server));
        var connection = new McpServerConnection(FactoryMcp.Definition(server, environment, inheritEnvironment: false), loggerFactory);
        try
        {
            await connection.ConnectAsync(Timeout, ct);
            return connection.Status == McpConnectionStatus.Connected
                ? new McpTestResult(true, [.. connection.Tools.Select(t => new McpTestedTool(t.Name, t.Description))], null)
                : new McpTestResult(false, [], connection.Error ?? "It could not be reached.");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
