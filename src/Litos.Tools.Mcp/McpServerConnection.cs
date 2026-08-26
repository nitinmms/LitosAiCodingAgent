using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Litos.Tools.Mcp;

public enum McpConnectionStatus
{
    Connecting,
    Connected,
    Unreachable,
    Failed,
}

/// <summary>
/// One configured server's connect/handshake/state lifecycle — a concrete class, not behind an
/// interface, matching this codebase's existing style (ShellTool itself isn't tested against a
/// fake process either). Crash and timeout both collapse to Unreachable (or, once
/// MaxConsecutiveFailures is reached, the terminal Failed state) with the distinguishing detail
/// preserved in Error, rather than a separate status per failure mode.
/// </summary>
public sealed class McpServerConnection(
    McpServerDefinition definition, ILoggerFactory loggerFactory, int consecutiveFailures = 0, TimeSpan? callTimeout = null)
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hard wall-clock cap on a single tool call, independent of the caller's CancellationToken —
    /// same reasoning and same default as ShellTool's own _hardTimeout (see its doc comment). Unlike
    /// ConnectAsync just below, which already wraps its handshake in a timeout, CallToolAsync used to
    /// await the underlying MCP client with no bound at all: a stdio server process that goes
    /// unresponsive mid-call (wedged, deadlocked, blocked on stdin) after a successful handshake left
    /// the caller's await parked forever, since the caller's own ct only fires on a genuine user
    /// cancel — which, for a face with no cancel UI wired up, may never come. That stalled AgentLoop
    /// inside InvokeToolSafelyAsync before it could ever reach the next provider request, matching
    /// "working indicator stuck on, zero requests reach the LLM, no amount of steering helps" (steering
    /// is only polled after a tool call returns). Overridable purely so tests can exercise the
    /// timeout path without a real 5-minute wait — production callers always get the default.
    /// </summary>
    private readonly TimeSpan _callTimeout = callTimeout ?? TimeSpan.FromMinutes(5);

    /// <summary>
    /// Once a server has failed this many consecutive attempts, it stops being retried
    /// automatically (Status becomes Failed instead of Unreachable) rather than being polled
    /// forever — an admin must edit or toggle the server via /mcp to try again. At MinBackoff
    /// doubling to MaxBackoff, 8 attempts spans ~22 minutes of real time before giving up, enough
    /// to ride out a brief restart/deploy without retrying indefinitely.
    /// </summary>
    private const int MaxConsecutiveFailures = 8;

    private readonly ILogger _logger = loggerFactory.CreateLogger($"Litos.Tools.Mcp.{definition.Name}");
    private McpClient? _client;
    private int _consecutiveFailures = consecutiveFailures;

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
    /// null once Connected (nothing to retry) or once Failed (given up, no more automatic retries).
    /// Backoff doubles per consecutive failure, capped at MaxBackoff, so a consistently-down
    /// server is retried less and less often instead of hammered every poll tick.
    /// </summary>
    public DateTimeOffset? NextRetryAt { get; private set; }

    /// <summary>
    /// Read by McpToolProvider.RefreshAsync so a retry's replacement McpServerConnection can be
    /// constructed with this count carried forward — otherwise every retry would start a fresh
    /// instance at zero and the backoff below would never actually grow past MinBackoff.
    /// </summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    public Task ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
        ConnectAsync(definition.Transport == McpTransportKind.Stdio ? BuildStdioTransport() : BuildHttpTransport(), timeout, ct);

    /// <summary>
    /// Internal seam so tests can connect over an in-process transport (e.g. StreamClientTransport
    /// over an in-memory pipe pair driving a real in-process McpServer) instead of a real external
    /// process/URL — exercises the exact same handshake/CallToolAsync code every production path
    /// runs, just without needing a real subprocess to spawn.
    /// </summary>
    internal async Task ConnectAsync(IClientTransport transport, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
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
        _consecutiveFailures++;

        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            Status = McpConnectionStatus.Failed;
            NextRetryAt = null;
            return;
        }

        Status = McpConnectionStatus.Unreachable;
        var backoff = TimeSpan.FromSeconds(Math.Min(
            MinBackoff.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1), MaxBackoff.TotalSeconds));
        NextRetryAt = DateTimeOffset.UtcNow + backoff;
    }

    public async ValueTask<ModelContextProtocol.Protocol.CallToolResult> CallToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException($"MCP server '{definition.Name}' is not connected.");

        using var timeoutCts = new CancellationTokenSource(_callTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await _client.CallToolAsync(toolName, arguments, cancellationToken: linkedCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MCP tool '{toolName}' on server '{definition.Name}' did not respond within {_callTimeout.TotalMinutes:0}m.");
        }
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
