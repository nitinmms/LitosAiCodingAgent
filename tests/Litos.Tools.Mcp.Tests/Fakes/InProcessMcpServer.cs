using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Litos.Tools.Mcp.Tests.Fakes;

/// <summary>
/// A real, in-process MCP server wired to a real McpClient over an in-memory duplex pipe pair
/// (StreamServerTransport/StreamClientTransport, both from the SDK) — no subprocess, no external
/// interpreter, but the exact same client/server protocol code every production stdio connection
/// runs. Used to exercise McpServerConnection.CallToolAsync's hard timeout against a tool that
/// genuinely never returns, the same "real hang, not a mock" approach ShellToolTests takes with a
/// real `ping` process for its own hard-timeout tests.
/// </summary>
public sealed class InProcessMcpServer : IAsyncDisposable
{
    private readonly McpServer _server;
    private readonly Task _serverRun;
    private readonly CancellationTokenSource _serverCts;

    private InProcessMcpServer(McpServer server, Task serverRun, CancellationTokenSource serverCts, IClientTransport clientTransport)
    {
        _server = server;
        _serverRun = serverRun;
        _serverCts = serverCts;
        ClientTransport = clientTransport;
    }

    public IClientTransport ClientTransport { get; }

    /// <summary>
    /// toolHandler backs a single tool named toolName — the tests only ever need one at a time
    /// (a fast-completing tool, or one that hangs forever until cancelled).
    /// </summary>
    public static InProcessMcpServer Start(
        string toolName, Func<CancellationToken, ValueTask<CallToolResult>> toolHandler)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var tool = McpServerTool.Create(
            async (CancellationToken ct) => await toolHandler(ct),
            new McpServerToolCreateOptions { Name = toolName });

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "litos-test-fake-server", Version = "1.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            ToolCollection = [tool],
        };

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "fake");
        var server = McpServer.Create(serverTransport, options, loggerFactory: null, serviceProvider: null);

        var serverCts = new CancellationTokenSource();
        var serverRun = server.RunAsync(serverCts.Token);

        // StreamClientTransport's ctor order is (serverInput, serverOutput) — the stream the
        // client WRITES to (server's stdin) first, then the stream the client READS from
        // (server's stdout) second. clientToServer is what the server reads from (its "input"),
        // serverToClient is what the server writes to (its "output").
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());

        return new InProcessMcpServer(server, serverRun, serverCts, clientTransport);
    }

    public async ValueTask DisposeAsync()
    {
        _serverCts.Cancel();
        try { await _serverRun; } catch { /* expected once cancelled */ }
        _serverCts.Dispose();
        await _server.DisposeAsync();
    }
}
