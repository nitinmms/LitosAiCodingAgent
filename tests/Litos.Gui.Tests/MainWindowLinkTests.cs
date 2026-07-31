namespace Litos.Gui.Tests;

/// <summary>
/// Covers MainWindow.IsOpenableHttpUrl, the pure validation behind OpenUrl — the click handler
/// wired to every MarkdownViewer.LinkClicked event so links rendered from model output (e.g.
/// web_search results) actually open in the system browser instead of being visually clickable
/// but inert. Only http/https is allowed through, since the URL is model-authored text handed
/// straight to Process.Start(UseShellExecute: true) — a hallucinated or malicious non-http
/// scheme must be rejected rather than launched.
/// </summary>
public class MainWindowLinkTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?query=1")]
    public void IsOpenableHttpUrl_AcceptsHttpAndHttps(string url)
    {
        var result = MainWindow.IsOpenableHttpUrl(url, out var uri);

        Assert.True(result);
        Assert.Equal(url, uri.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("")]
    public void IsOpenableHttpUrl_RejectsNonHttpSchemes(string url)
    {
        var result = MainWindow.IsOpenableHttpUrl(url, out _);

        Assert.False(result);
    }
}
