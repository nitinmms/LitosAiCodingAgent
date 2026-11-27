using System.Net;
using System.Net.Sockets;
using System.Text;
using Litos.Mail;

namespace Litos.Mail.Tests;

/// <summary>
/// The loopback redirect that receives Google's callback. The first live sign-in (2026-11-27) met
/// "connection refused": the listener took one connection, it wasn't the callback, and it stopped
/// listening. These pin that nothing but the genuine callback, with this sign-in's state, can end
/// the wait — and that the genuine one still does.
/// </summary>
public class LoopbackRedirectTests
{
    private const string State = "expected-state";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Callback_ReturnsTheCode_AndShowsTheConnectedPage()
    {
        var (listener, uri) = LoopbackRedirect.Start();
        try
        {
            var wait = LoopbackRedirect.WaitForCodeAsync(listener, State, Timeout());

            var page = await GetAsync(uri, $"?state={State}&code=4%2F0AUn6Hd_abc&scope=x");

            Assert.Equal("4/0AUn6Hd_abc", await wait.WaitAsync(Limit));
            Assert.Contains("200 OK", page);
            Assert.Contains("Litos is connected to Gmail", page);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task EmptyPreConnection_ThatCloses_DoesNotEndTheWait()
    {
        var (listener, uri) = LoopbackRedirect.Start();
        try
        {
            var wait = LoopbackRedirect.WaitForCodeAsync(listener, State, Timeout());

            using (var stray = new TcpClient())
                await stray.ConnectAsync(IPAddress.Loopback, new Uri(uri).Port);

            await GetAsync(uri, $"?state={State}&code=real");
            Assert.Equal("real", await wait.WaitAsync(Limit));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task IdlePreConnection_LeftOpen_DoesNotHoldUpTheRealCallback()
    {
        var (listener, uri) = LoopbackRedirect.Start();
        try
        {
            var wait = LoopbackRedirect.WaitForCodeAsync(listener, State, Timeout());

            using var idle = new TcpClient();
            await idle.ConnectAsync(IPAddress.Loopback, new Uri(uri).Port);

            await GetAsync(uri, $"?state={State}&code=real");
            Assert.Equal("real", await wait.WaitAsync(Limit));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TlsHandshakeProbe_IsIgnored()
    {
        var (listener, uri) = LoopbackRedirect.Start();
        try
        {
            var wait = LoopbackRedirect.WaitForCodeAsync(listener, State, Timeout());

            // A browser trying https:// first sends a TLS ClientHello, which starts 0x16 0x03.
            await SendRawAsync(uri, [0x16, 0x03, 0x01, 0x02, 0x00, 0x01, 0x00, 0x01, 0xFC, 0x03, 0x03]);

            await GetAsync(uri, $"?state={State}&code=real");
            Assert.Equal("real", await wait.WaitAsync(Limit));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FaviconRequest_Gets404_AndIsIgnored()
    {
        var (listener, uri) = LoopbackRedirect.Start();
        try
        {
            var wait = LoopbackRedirect.WaitForCodeAsync(listener, State, Timeout());

            var favicon = await GetAsync(new Uri(new Uri(uri), "/favicon.ico").ToString(), "");
            Assert.Contains("404", favicon);

            await GetAsync(uri, $"?state={State}&code=real");
            Assert.Equal("real", await wait.WaitAsync(Limit));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WrongState_IsRefusedWithAnOutOfDatePage_AndTheRightOneStillWorks()
    {
        var (listener, uri) = LoopbackRedirect.Start();
        try
        {
            var wait = LoopbackRedirect.WaitForCodeAsync(listener, State, Timeout());

            var stale = await GetAsync(uri, "?state=from-an-old-tab&code=forged");
            Assert.Contains("400", stale);
            Assert.Contains("out of date", stale);
            Assert.False(wait.IsCompleted);

            await GetAsync(uri, $"?state={State}&code=real");
            Assert.Equal("real", await wait.WaitAsync(Limit));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AccessDenied_WithTheRightState_EndsTheWaitAsCancelled()
    {
        var (listener, uri) = LoopbackRedirect.Start();
        try
        {
            var wait = LoopbackRedirect.WaitForCodeAsync(listener, State, Timeout());

            await GetAsync(uri, $"?state={State}&error=access_denied");

            var ex = await Assert.ThrowsAsync<MailAccessException>(() => wait.WaitAsync(Limit));
            Assert.Contains("cancelled", ex.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Cancellation_EndsTheWait_WithASentenceSayingWhatToDo()
    {
        var (listener, _) = LoopbackRedirect.Start();
        try
        {
            using var cts = new CancellationTokenSource();
            var wait = LoopbackRedirect.WaitForCodeAsync(listener, State, cts.Token);

            await cts.CancelAsync();

            var ex = await Assert.ThrowsAsync<MailAccessException>(() => wait.WaitAsync(Limit));
            Assert.Contains("Connect Gmail again", ex.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>A plain HTTP/1.1 GET over a raw socket, the way a browser sends the redirect.</summary>
    private static async Task<string> GetAsync(string uri, string query)
    {
        var target = new Uri(uri + query);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, target.Port);
        var stream = client.GetStream();
        var request = $"GET {target.PathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1:{target.Port}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync().WaitAsync(Limit);
    }

    private static async Task SendRawAsync(string uri, byte[] bytes)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, new Uri(uri).Port);
        var stream = client.GetStream();
        await stream.WriteAsync(bytes);
        client.Client.Shutdown(SocketShutdown.Send);
        // Wait for the listener to close its side, so the probe has really been handled first.
        var buffer = new byte[64];
        await stream.ReadAsync(buffer).AsTask().WaitAsync(Limit);
    }
}
