using Microsoft.Playwright;

namespace Litos.Probe.Web.Browser;

public sealed class BrowserUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// One Chromium for a run. Playwright's Node driver is a child process, so a crash or hang in it
/// takes down this run's browser, not the host (README §2).
/// </summary>
internal sealed class BrowserHost : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;

    private BrowserHost(IPlaywright playwright, IBrowser browser)
    {
        _playwright = playwright;
        _browser = browser;
    }

    public static async Task<BrowserHost> LaunchAsync(bool headless)
    {
        IPlaywright? playwright = null;
        try
        {
            playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = headless });
            return new BrowserHost(playwright, browser);
        }
        catch (PlaywrightException ex)
        {
            playwright?.Dispose();
            var hint = ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
                ? "Chromium for Playwright is not installed. Install it with: pwsh bin/<config>/<tfm>/playwright.ps1 install chromium"
                : $"Chromium could not be started: {FirstLine(ex.Message)}";
            throw new BrowserUnavailableException(hint, ex);
        }
    }

    /// <param name="appOrigins">The app's own origins. Requests anywhere else are blocked unless <paramref name="allowedOrigins"/> lists them.</param>
    public async Task<IBrowserContext> NewContextAsync(string baseUrl, IReadOnlySet<string> appOrigins, IReadOnlyList<string> allowedOrigins, string? storageState)
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl,
            StorageState = storageState,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            // A service worker would fetch around the origin guard below.
            ServiceWorkers = ServiceWorkerPolicy.Block,
            Locale = "en-US",
        });

        var allowed = appOrigins.Concat(allowedOrigins.Select(o => o.TrimEnd('/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await context.RouteAsync("**/*", async route =>
        {
            if (allowed.Contains(OriginOf(route.Request.Url)))
                await route.ContinueAsync();
            else
                await route.AbortAsync("blockedbyclient");
        });
        return context;
    }

    internal static string OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "";

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _browser.CloseAsync();
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // The driver already went away; disposing it below is all that's left.
        }

        _playwright.Dispose();
    }
}
