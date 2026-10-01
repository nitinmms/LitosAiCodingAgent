using System.Text.Json;

namespace Litos.Hosting;

/// <summary>
/// Startup shared by every host that is spawned as a child process and reached over loopback:
/// bind an OS-assigned port, then report it to the parent as the first line on stdout.
/// </summary>
public static class LoopbackHost
{
    public static void ConfigureLoopback(WebApplicationBuilder builder)
    {
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // port 0 = OS-assigned free loopback port

        // TurnsEndpoints' /turns SSE response (ToSseData) only writes bytes when a real AgentEvent
        // fires — it sends no heartbeat during silence — so a quiet gap with no events (a slow tool
        // call, or a local model that hasn't produced its first token yet) can run well past Kestrel's
        // default MinResponseDataRate (240 B/s after a 5s grace period). Kestrel then aborts the
        // connection itself, which the VS Code extension's Node-side fetch (undici) surfaces as a bare
        // `TypeError: terminated` — rendered in the webview as a SYSTEM "Error: terminated" bubble with
        // no indication it was Kestrel, not the model or a tool, that gave up. That data-rate watchdog
        // exists to protect a server from slow/malicious remote clients; this process only ever listens
        // on loopback for the process that spawned it, so there's nothing to protect against — disable it.
        builder.WebHost.ConfigureKestrel(o => o.Limits.MinResponseDataRate = null);
    }

    /// <summary>
    /// Starts the app and writes the port handshake. Started (not Run) so the OS-assigned port is
    /// known before the caller blocks — app.Urls is only populated with the real, resolved address
    /// once the listener actually opens.
    /// </summary>
    /// <param name="beforeAnnounce">Runs with the bound port after the listener opens but before
    /// the handshake is written, for setup that must be complete by the time the parent can send
    /// its first request.</param>
    /// <returns>The bound port.</returns>
    public static async Task<int> StartAndAnnounceAsync(WebApplication app, Action<int>? beforeAnnounce = null)
    {
        await app.StartAsync();

        var boundPort = new Uri(app.Urls.First()).Port;
        beforeAnnounce?.Invoke(boundPort);

        // The parent's only startup signal: first stdout line is this single-line JSON handshake.
        // Nothing else should write to stdout before this.
        Console.WriteLine(FormatHandshake(boundPort));
        return boundPort;
    }

    internal static string FormatHandshake(int port) => JsonSerializer.Serialize(new { port });
}
