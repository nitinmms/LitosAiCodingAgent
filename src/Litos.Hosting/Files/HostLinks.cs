using System.Text.RegularExpressions;

namespace Litos.Hosting.Files;

/// <summary>
/// C# port of Litos.VsCode's shareLinks.ts rewriteStaleHostLink, for faces that open these links
/// themselves (Litos.Gui). share_file links ("http://127.0.0.1:{port}/files/{token}") and artifact
/// links ("http://127.0.0.1:{port}/artifacts/{id}[/v/{n}]") embed whichever loopback port the
/// minting process happened to bind (port 0, OS-assigned), but the file/page behind them lives
/// under ~/.litos and outlives that process. A link persisted into an old session's transcript
/// therefore points at a port nothing listens on any more even though the token is still good, so
/// any link matching one of these shapes is re-pointed at the currently running host.
/// </summary>
public static partial class HostLinks
{
    [GeneratedRegex("^/files/[0-9a-f]{16,}$")]
    private static partial Regex SharedFilePath();

    [GeneratedRegex("^/artifacts/[0-9a-f]{16}(/v/[0-9]+)?$")]
    private static partial Regex ArtifactPath();

    // A URL in running text ends at whitespace or a closing delimiter; trailing sentence punctuation
    // is trimmed separately below.
    [GeneratedRegex(@"https?://[^\s<>""'()\[\]]+")]
    private static partial Regex UrlInText();

    /// <summary>Every host-minted share or artifact link in <paramref name="text"/>, in order and
    /// without duplicates — e.g. the "Open: http://127.0.0.1:.../artifacts/..." line the artifact
    /// tool returns, so a UI can render it as something clickable.</summary>
    public static IReadOnlyList<string> FindAll(string text) =>
        UrlInText().Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?'))
            .Where(IsHostLink)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <paramref name="url"/> re-pointed at <paramref name="currentBaseUrl"/> when its path is a
    /// host-minted share or artifact link on a loopback host; any other URL (or an unparseable one,
    /// or no running host) is returned unchanged.
    /// </summary>
    public static string RewriteStale(string url, string? currentBaseUrl)
    {
        if (string.IsNullOrEmpty(currentBaseUrl) || !TryGetHostLinkPath(url, out var path))
            return url;

        return $"{currentBaseUrl.TrimEnd('/')}{path}";
    }

    /// <summary>True when <paramref name="url"/> is a loopback share or artifact link — the shape
    /// <see cref="RewriteStale"/> rewrites. Lets a UI single these links out (e.g. to render an
    /// "Open" affordance on a tool row) using the same rule.</summary>
    public static bool IsHostLink(string url) => TryGetHostLinkPath(url, out _);

    private static bool TryGetHostLinkPath(string url, out string path)
    {
        path = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            return false;

        // Only links this machine's own host minted are rewritten — a model-written link to some
        // other site that happens to have a /files/<hex> path must keep pointing where it points.
        if (!parsed.IsLoopback)
            return false;

        if (!SharedFilePath().IsMatch(parsed.AbsolutePath) && !ArtifactPath().IsMatch(parsed.AbsolutePath))
            return false;

        path = parsed.AbsolutePath;
        return true;
    }
}
