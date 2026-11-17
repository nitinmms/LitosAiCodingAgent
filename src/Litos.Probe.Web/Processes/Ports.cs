using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Litos.Probe.Web.Processes;

internal static class Ports
{
    /// <summary>
    /// Free loopback ports, one per name. Every listener is held until all are chosen, so two
    /// processes never get the same one.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Allocate(IEnumerable<string> names)
    {
        var listeners = new List<TcpListener>();
        try
        {
            var ports = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
                ports[name] = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            return ports;
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();
        }
    }

    /// <summary>Every TCP port something on this machine listens on now.</summary>
    public static HashSet<int> Listening()
    {
        try
        {
            return [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    public static async Task<bool> AcceptsAsync(int port, CancellationToken ct)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var client = new TcpClient(address.AddressFamily);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await client.ConnectAsync(address, port, attempt.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Try the other loopback address.
            }
        }

        return false;
    }

    /// <summary>Waits until nothing accepts on the port, so the next run can have it. False if it stays busy.</summary>
    public static async Task<bool> WaitFreeAsync(int port, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!await AcceptsAsync(port, CancellationToken.None))
                return true;
            await Task.Delay(200);
        }

        return false;
    }
}
