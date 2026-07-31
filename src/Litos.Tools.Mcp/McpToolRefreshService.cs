using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Litos.Tools.Mcp;

/// <summary>
/// Periodically reconciles McpToolProvider's live connections against McpConfigStore — picks up
/// admin-page add/enable/disable/edit/remove without a container restart, and retries any
/// Unreachable server once its own backoff window elapses (McpServerConnection.NextRetryAt).
/// The poll tick itself stays fixed and cheap (most ticks no-op against unchanged config and
/// non-due backoffs); only whether a given Unreachable server is actually retried on a given
/// tick depends on that server's own backoff state, computed in McpToolProvider.RefreshAsync.
/// </summary>
public sealed class McpToolRefreshService(
    McpToolProvider provider, TimeSpan pollInterval, TimeSpan perServerTimeout, ILogger<McpToolRefreshService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(pollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await provider.RefreshAsync(perServerTimeout, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutting down — let the loop exit via WaitForNextTickAsync returning false.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP reconciliation tick failed.");
            }
        }
    }
}
