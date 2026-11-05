namespace Litos.Artifacts;

/// <summary>
/// The response headers every artifact is served with — the whole security boundary for v1
/// (ReadMe_ArtifactsFeature.md §7). Artifact HTML is agent-authored and the agent reads untrusted
/// repositories and web pages, so the page is treated as hostile. It is served on the same
/// loopback origin as the host's own unauthenticated endpoints (/config/keys, /sessions/{id}/turns
/// — which runs an agent with a shell tool), so it must not be able to reach them:
/// <list type="bullet">
/// <item><c>sandbox</c> without <c>allow-same-origin</c> makes the document an opaque origin, so its
/// script is not same-origin with the host even though the URL is.</item>
/// <item><c>connect-src 'none'</c> blocks fetch/XHR/WebSocket/EventSource/beacons to anywhere,
/// including blind no-cors requests to the host.</item>
/// <item><c>form-action 'none'</c> (and no <c>allow-forms</c>) blocks form posts; <c>frame-src</c>
/// blocks embedding the host's pages; <c>frame-ancestors</c> stops other pages framing the artifact.</item>
/// </list>
/// Scripts and styles may come inline or from <see cref="Cdns"/>; there is no <c>'unsafe-eval'</c>.
/// </summary>
public static class ArtifactContentPolicy
{
    /// <summary>Where an artifact may load scripts, stylesheets, fonts and images from.</summary>
    public static readonly IReadOnlyList<string> Cdns =
    [
        "https://cdnjs.cloudflare.com",
        "https://cdn.jsdelivr.net",
        "https://unpkg.com",
        "https://cdn.tailwindcss.com",
        "https://code.jquery.com",
    ];

    public const string GoogleFontsStylesheets = "https://fonts.googleapis.com";
    public const string GoogleFontsFiles = "https://fonts.gstatic.com";

    /// <summary>
    /// Scripts run and may open dialogs, popups (links in a new tab) and downloads; nothing else.
    /// Deliberately no <c>allow-popups-to-escape-sandbox</c>: a popup inherits the sandbox, so the page
    /// can't hand its own URL — this host's port — to an unsandboxed page that could then attempt DNS
    /// rebinding against the host's unauthenticated endpoints.
    /// </summary>
    public const string SandboxFlags = "allow-scripts allow-popups allow-modals allow-downloads";

    public static string ContentSecurityPolicy { get; } = Build();

    public const string ContentType = "text/html; charset=utf-8";

    public static void Apply(HttpResponse response)
    {
        var headers = response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        // A refresh must show the latest version: republishing keeps the same URL.
        headers.CacheControl = "no-store";
    }

    private static string Build()
    {
        var cdns = string.Join(' ', Cdns);
        string[] directives =
        [
            $"sandbox {SandboxFlags}",
            "default-src 'none'",
            $"script-src 'unsafe-inline' {cdns}",
            $"style-src 'unsafe-inline' {cdns} {GoogleFontsStylesheets}",
            $"font-src {GoogleFontsFiles} {cdns} data:",
            $"img-src data: blob: {cdns}",
            "media-src data: blob:",
            "connect-src 'none'",
            "frame-src 'none'",
            "object-src 'none'",
            "base-uri 'none'",
            "form-action 'none'",
            "frame-ancestors 'none'",
        ];
        return string.Join("; ", directives);
    }
}
