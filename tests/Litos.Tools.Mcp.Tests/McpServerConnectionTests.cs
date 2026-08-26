using Litos.Tools.Mcp.Tests.Fakes;
using Litos.Tools.Shell;
using ModelContextProtocol.Protocol;

namespace Litos.Tools.Mcp.Tests;

public class McpServerConnectionTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    // Same rationale as McpToolProviderTests.BogusServer — a command guaranteed not to exist,
    // so ConnectAsync fails fast and deterministically without a real MCP server binary.
    private static McpServerDefinition BogusServer(string name = "bad") => new(
        Name: name,
        Transport: McpTransportKind.Stdio,
        Command: "litos-test-nonexistent-command-xyz",
        Args: [],
        Env: null,
        Url: null,
        Enabled: true,
        DefaultPermission: ToolPermission.Ask,
        ToolOverrides: null);

    private static McpServerConnection Create(int consecutiveFailures = 0) =>
        new(BogusServer(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, consecutiveFailures);

    [Fact]
    public async Task ConnectAsync_FirstFailure_BacksOffByMinBackoff()
    {
        var connection = Create();
        var before = DateTimeOffset.UtcNow;

        await connection.ConnectAsync(ShortTimeout, CancellationToken.None);

        Assert.Equal(McpConnectionStatus.Unreachable, connection.Status);
        Assert.Equal(1, connection.ConsecutiveFailures);
        Assert.NotNull(connection.NextRetryAt);
        // MinBackoff is 15s (McpServerConnection) — assert within a tolerant window rather than
        // hardcoding the exact constant, so this test isn't coupled to internals it can't see.
        Assert.InRange(connection.NextRetryAt!.Value - before, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ConnectAsync_ConsecutiveFailuresCarriedForward_BackoffGrows()
    {
        // This is the scenario McpToolProvider.RefreshAsync's retry path produces in production:
        // a replacement McpServerConnection constructed with the prior instance's
        // ConsecutiveFailures, rather than starting over at zero on every retry.
        var afterOneFailure = Create();
        await afterOneFailure.ConnectAsync(ShortTimeout, CancellationToken.None);

        var retry = Create(afterOneFailure.ConsecutiveFailures);
        var before = DateTimeOffset.UtcNow;
        await retry.ConnectAsync(ShortTimeout, CancellationToken.None);

        Assert.Equal(2, retry.ConsecutiveFailures);
        Assert.Equal(McpConnectionStatus.Unreachable, retry.Status);
        // Second consecutive failure should back off ~30s (double MinBackoff), not repeat ~15s.
        Assert.InRange(retry.NextRetryAt!.Value - before, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(35));
    }

    [Fact]
    public async Task ConnectAsync_ManyConsecutiveFailures_BackoffCapsAtMaxBackoff()
    {
        var connection = Create(consecutiveFailures: 5); // well past enough doublings to hit the 5-minute cap
        var before = DateTimeOffset.UtcNow;

        await connection.ConnectAsync(ShortTimeout, CancellationToken.None);

        Assert.Equal(McpConnectionStatus.Unreachable, connection.Status);
        Assert.InRange(connection.NextRetryAt!.Value - before, TimeSpan.FromSeconds(295), TimeSpan.FromSeconds(305));
    }

    [Fact]
    public async Task ConnectAsync_ReachesMaxConsecutiveFailures_StatusBecomesFailed_StopsSchedulingRetry()
    {
        // MaxConsecutiveFailures is 8 — starting at 7 means this one failure is the 8th.
        var connection = Create(consecutiveFailures: 7);

        await connection.ConnectAsync(ShortTimeout, CancellationToken.None);

        Assert.Equal(McpConnectionStatus.Failed, connection.Status);
        Assert.Equal(8, connection.ConsecutiveFailures);
        Assert.Null(connection.NextRetryAt);
        Assert.NotNull(connection.Error);
    }

    [Fact]
    public async Task ConnectAsync_FailureCountPassedToConstructor_IsWhatIncrements()
    {
        // Proves MarkUnreachable increments from the constructor-supplied count rather than
        // always starting from zero — the core of the backoff-growth fix.
        var connection = Create(consecutiveFailures: 6);

        await connection.ConnectAsync(ShortTimeout, CancellationToken.None);

        Assert.Equal(7, connection.ConsecutiveFailures);
        Assert.Equal(McpConnectionStatus.Unreachable, connection.Status);
    }

    // --- CallToolAsync hard timeout ---
    //
    // Connects a real McpServerConnection to a real, in-process McpServer (InProcessMcpServer,
    // over an in-memory duplex pipe pair — same StreamClientTransport/StreamServerTransport
    // protocol code a real stdio server uses) so these exercise the actual handshake and
    // CallToolAsync path, not a mock. Mirrors ShellToolTests' own "real hang, short overridable
    // timeout" approach for its hardTimeout tests.

    [Fact]
    public async Task CallToolAsync_ToolNeverReturns_TimesOutAndThrows()
    {
        await using var server = InProcessMcpServer.Start("hang", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new CallToolResult(); // unreachable
        });

        var connection = new McpServerConnection(
            BogusServer(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            callTimeout: TimeSpan.FromMilliseconds(500));
        await connection.ConnectAsync(server.ClientTransport, ShortTimeout, CancellationToken.None);
        Assert.True(connection.Status == McpConnectionStatus.Connected, connection.Error);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => connection.CallToolAsync("hang", new Dictionary<string, object?>(), CancellationToken.None).AsTask());
        Assert.Contains("hang", ex.Message);
    }

    [Fact]
    public async Task CallToolAsync_ToolNeverReturns_DoesNotWaitLongerThanCallTimeout()
    {
        await using var server = InProcessMcpServer.Start("hang", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new CallToolResult(); // unreachable
        });

        var connection = new McpServerConnection(
            BogusServer(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            callTimeout: TimeSpan.FromMilliseconds(500));
        await connection.ConnectAsync(server.ClientTransport, ShortTimeout, CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => connection.CallToolAsync("hang", new Dictionary<string, object?>(), CancellationToken.None).AsTask());
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected early return, took {sw.Elapsed}");
    }

    [Fact]
    public async Task CallToolAsync_FastTool_CompletesNormally_CallTimeoutDoesNotInterfere()
    {
        await using var server = InProcessMcpServer.Start("echo", ct =>
            ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "hello" }] }));

        var connection = new McpServerConnection(
            BogusServer(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            callTimeout: TimeSpan.FromSeconds(30));
        await connection.ConnectAsync(server.ClientTransport, ShortTimeout, CancellationToken.None);

        var result = await connection.CallToolAsync("echo", new Dictionary<string, object?>(), CancellationToken.None);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("hello", text.Text);
    }

    [Fact]
    public async Task CallToolAsync_UserCancelsBeforeCallTimeout_PropagatesCancellation()
    {
        await using var server = InProcessMcpServer.Start("hang", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new CallToolResult(); // unreachable
        });

        var connection = new McpServerConnection(
            BogusServer(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            callTimeout: TimeSpan.FromMinutes(5));
        await connection.ConnectAsync(server.ClientTransport, ShortTimeout, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var callTask = connection.CallToolAsync("hang", new Dictionary<string, object?>(), cts.Token).AsTask();
        cts.Cancel();

        // A genuine user cancel (ct itself, not the call's own timeout) should propagate as
        // cancellation, not be reinterpreted as a TimeoutException — matches ShellTool's own
        // "!ct.IsCancellationRequested" distinction between the two.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callTask);
    }
}
