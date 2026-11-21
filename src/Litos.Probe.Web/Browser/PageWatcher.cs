using Litos.Probe.Web.Core.Profiles;
using Microsoft.Playwright;

namespace Litos.Probe.Web.Browser;

/// <summary>
/// The universal signals on one page (README §6.3): an uncaught page error, HTTP ≥ 500 on an app
/// document, fetch or XHR, a failed app request, and <c>console.error</c>. They need no change to
/// the app. A check reads what arrived since its <see cref="Mark"/>.
/// </summary>
internal sealed class PageWatcher
{
    private static readonly HashSet<string> WatchedTypes = ["document", "fetch", "xhr"];

    private readonly List<(string Text, bool IsConsoleError)> _signals = [];
    private readonly Lock _lock = new();
    private readonly Func<string, bool> _isApp;
    private readonly bool _consoleErrorsFail;

    /// <param name="ignored">The stack profile's known framework noise; <paramref name="onIgnored"/> hears each line it drops.</param>
    public PageWatcher(IPage page, Func<string, bool> isAppUrl, bool consoleErrorsFail,
        IReadOnlyList<IgnoredConsole>? ignored = null, Action<IgnoredConsole>? onIgnored = null)
    {
        _isApp = isAppUrl;
        _consoleErrorsFail = consoleErrorsFail;

        page.PageError += (_, error) => Add($"Uncaught error: {FirstLine(error)}");
        page.Console += (_, message) =>
        {
            // The browser's own "Failed to load resource" lines repeat what the network signals
            // already report, and a missing favicon would otherwise fail every page.
            if (message.Type != "error" || message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                return;
            if (IgnoredBy(message.Location, ignored) is { } rule)
                onIgnored?.Invoke(rule);
            else
                Add($"console.error: {FirstLine(message.Text)}", isConsoleError: true);
        };
        page.Response += (_, response) =>
        {
            if (response.Status >= 500 && _isApp(response.Url) && WatchedTypes.Contains(response.Request.ResourceType))
                Add($"HTTP {response.Status} on {response.Request.Method} {PathOf(response.Url)}");
        };
        page.RequestFailed += (_, request) =>
        {
            var failure = request.Failure ?? "";
            // ERR_ABORTED is a navigation leaving the page before a request finished, not a fault.
            if (_isApp(request.Url) && !failure.Contains("ERR_ABORTED", StringComparison.Ordinal))
                Add($"Request failed: {request.Method} {PathOf(request.Url)} ({failure})");
        };
    }

    public int Mark()
    {
        lock (_lock)
            return _signals.Count;
    }

    /// <summary>The signals that fail the page since <paramref name="mark"/>, and how many console errors there were.</summary>
    public (IReadOnlyList<string> Failing, int ConsoleErrors) Since(int mark)
    {
        lock (_lock)
        {
            var since = _signals.Skip(mark).ToList();
            return ([.. since.Where(s => !s.IsConsoleError || _consoleErrorsFail).Select(s => s.Text)], since.Count(s => s.IsConsoleError));
        }
    }

    private void Add(string text, bool isConsoleError = false)
    {
        lock (_lock)
            _signals.Add((text, isConsoleError));
    }

    /// <summary>The rule that ignores a console message from <paramref name="location"/> (<c>URL:line:column</c>), if one does.</summary>
    internal static IgnoredConsole? IgnoredBy(string? location, IReadOnlyList<IgnoredConsole>? ignored)
    {
        if (string.IsNullOrEmpty(location) || ignored is not { Count: > 0 })
            return null;
        // Strip ":line:column", then any query string, and compare the end of the path.
        var url = location;
        for (var i = 0; i < 2; i++)
        {
            var colon = url.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(url[(colon + 1)..], out _))
                break;
            url = url[..colon];
        }

        var path = PathOf(url).Split('?', 2)[0];
        return ignored.FirstOrDefault(r => SourcePattern(r.Source).IsMatch(path));
    }

    /// <summary>
    /// The end of a script's path, where <c>*</c> stands for any characters but <c>/</c>: a
    /// bundler's fingerprint sits inside the name (.NET serves <c>PasskeySubmit.pr21mg9rrw.razor.js</c>
    /// for <c>PasskeySubmit.razor.js</c>), so a profile writes <c>PasskeySubmit*.razor.js</c>.
    /// </summary>
    internal static System.Text.RegularExpressions.Regex SourcePattern(string source) =>
        new("(^|/)" + string.Join("[^/]*", source.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string PathOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : url;

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
