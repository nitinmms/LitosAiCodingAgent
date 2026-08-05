using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Litos.Tools.Mcp;

public enum McpConnectionStatus
{
    Connecting,
    Connected,
    Unreachable,
}

/// <summary>
/// One configured server's connect/handshake/state lifecycle — a concrete class, not behind an
/// interface, matching this codebase's existing style (ShellTool itself isn't tested against a
/// fake process either). Crash and timeout both collapse to Unreachable with the distinguishing
/// detail preserved in Error, rather than a separate 4th state.
/// </summary>
public sealed class McpServerConnection(McpServerDefinition definition, ILoggerFactory loggerFactory)
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger = loggerFactory.CreateLogger($"Litos.Tools.Mcp.{definition.Name}");
    private McpClient? _client;
    private int _consecutiveFailures;

    public McpServerDefinition Definition => definition;
    public string ServerName => definition.Name;
    public McpConnectionStatus Status { get; private set; } = McpConnectionStatus.Connecting;
    public string? Error { get; private set; }
    public IReadOnlyList<McpClientTool> Tools { get; private set; } = [];

    /// <summary>
    /// Prompts this server declares, named with the same mcp__{server}__{name} convention
    /// McpToolProxy uses for tools (see McpConfig.cs remarks on "__"-rejection) even though a
    /// prompt is never an ITool/ToolRegistry entry — it's a GUI-only command-menu concept whose
    /// fetched result is injected as turn content (see MainWindow.HandleMcpPromptAsync), not
    /// invoked like a tool.
    /// </summary>
    public IReadOnlyList<McpClientPrompt> Prompts { get; private set; } = [];

    /// <summary>
    /// Earliest time McpToolRefreshService should retry this connection while it's Unreachable —
    /// null once Connected (nothing to retry) or before the first attempt (retry immediately).
    /// Backoff doubles per consecutive failure, capped at MaxBackoff, so a consistently-down
    /// server is retried less and less often instead of hammered every poll tick.
    /// </summary>
    public DateTimeOffset? NextRetryAt { get; private set; }

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            IClientTransport transport = definition.Transport == McpTransportKind.Stdio
                ? BuildStdioTransport()
                : BuildHttpTransport();

            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions(),
                loggerFactory,
                linkedCts.Token);

            var listResult = await _client.ListToolsAsync(cancellationToken: linkedCts.Token);
            Tools = [.. listResult];
            Prompts = await TryListPromptsAsync(_client, linkedCts.Token);
            Status = McpConnectionStatus.Connected;
            Error = null;
            NextRetryAt = null;
            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Error = $"Timed out connecting within {timeout.TotalSeconds:0}s.";
            _logger.LogWarning("MCP server '{Server}' did not respond within {Timeout}s.", definition.Name, timeout.TotalSeconds);
            MarkUnreachable();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _logger.LogWarning(ex, "MCP server '{Server}' failed to connect.", definition.Name);
            MarkUnreachable();
        }
    }

    /// <summary>
    /// Prompts are additive (most servers expose only tools), so — unlike ListToolsAsync just
    /// above, whose failure legitimately fails the whole connect attempt — a server that errors
    /// on ListPromptsAsync (e.g. one that never declared the prompts capability) contributes zero
    /// prompts rather than being marked Unreachable over a capability it never claimed to have.
    /// </summary>
    private async Task<IReadOnlyList<McpClientPrompt>> TryListPromptsAsync(McpClient client, CancellationToken ct)
    {
        try
        {
            var result = await client.ListPromptsAsync(cancellationToken: ct);
            return [.. result];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "MCP server '{Server}' has no usable prompts capability.", definition.Name);
            return [];
        }
    }

    private void MarkUnreachable()
    {
        Status = McpConnectionStatus.Unreachable;
        _consecutiveFailures++;
        var backoff = TimeSpan.FromSeconds(Math.Min(
            MinBackoff.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1), MaxBackoff.TotalSeconds));
        NextRetryAt = DateTimeOffset.UtcNow + backoff;
    }

    public async ValueTask<ModelContextProtocol.Protocol.CallToolResult> CallToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException($"MCP server '{definition.Name}' is not connected.");

        return await _client.CallToolAsync(toolName, arguments, cancellationToken: ct);
    }

    public async ValueTask<ModelContextProtocol.Protocol.GetPromptResult> GetPromptAsync(
        McpClientPrompt prompt, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct) =>
        await prompt.GetAsync(arguments, cancellationToken: ct);

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }

    private StdioClientTransport BuildStdioTransport()
    {
        var options = new StdioClientTransportOptions
        {
            Name = definition.Name,
            Command = definition.Command ?? throw new InvalidOperationException(
                $"Stdio server '{definition.Name}' has no Command configured."),
            Arguments = definition.Args?.ToList() ?? [],
            StandardErrorLines = line => _logger.LogInformation("[{Server} stderr] {Line}", definition.Name, line),
        };

        if (definition.Env is { Count: > 0 })
        {
            options.EnvironmentVariables ??= new Dictionary<string, string?>();
            foreach (var (key, value) in definition.Env)
                options.EnvironmentVariables[key] = value;
        }

        return new StdioClientTransport(options);
    }

    private HttpClientTransport BuildHttpTransport()
    {
        var url = definition.Url ?? throw new InvalidOperationException(
            $"HTTP server '{definition.Name}' has no Url configured.");

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            Name = definition.Name,
        });
    }
}
