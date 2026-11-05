// Links the host mints into tool results embed whichever host:port was running at the time:
// share_file (ShareFileTool.cs) emits "http://127.0.0.1:{port}/files/{token}", where token is
// RandomNumberGenerator.GetHexString(16, lowercase: true) — a stable file on disk under
// ~/.litos/shared-files, valid 24h regardless of which process minted it — and the artifact tool
// (Litos.Artifacts' ArtifactTool.cs) emits "http://127.0.0.1:{port}/artifacts/{id}" (optionally
// "/v/{n}"), id also 16 lowercase hex, stored under ~/.litos/artifacts with no expiry. But the port
// is only valid for the Litos.VsCodeHost.exe process that was running when the link was made.
// Closing/reloading the VS Code window kills that process (deactivate()) and the next panel-open
// spawns a new one on a new OS-assigned port (Program.cs binds port 0), so a link persisted into an
// old session's transcript embeds a port nothing is listening on anymore even though the
// token/file are still good. Since the path itself carries everything needed to resolve the file,
// any link matching one of these shapes is rewritten to whichever host:port the *currently
// running* shared host actually reports, rather than trusting whatever host:port happened to be
// embedded when the link was generated.
const HOST_LINK_PATH_PATTERNS: readonly RegExp[] = [
    /^\/files\/[0-9a-f]{16,}$/,
    /^\/artifacts\/[0-9a-f]{16}(\/v\/[0-9]+)?$/,
];

/**
 * `url` re-pointed at `currentBaseUrl` when its path is a host-minted share or artifact link;
 * any other URL (or an unparseable one, or no running host) is returned unchanged.
 */
export function rewriteStaleHostLink(url: string, currentBaseUrl: string | undefined): string {
    if (!currentBaseUrl) return url;
    try {
        const parsed = new URL(url);
        if (!isLoopback(parsed.hostname)) return url;
        if (!HOST_LINK_PATH_PATTERNS.some(pattern => pattern.test(parsed.pathname))) return url;
        return `${currentBaseUrl.replace(/\/+$/, "")}${parsed.pathname}`;
    } catch {
        return url;
    }
}

// Only links this machine's own host minted are rewritten — a model-written link to some other
// site that happens to have a /files/<hex> path must keep pointing where it points.
function isLoopback(hostname: string): boolean {
    return hostname === "127.0.0.1" || hostname === "localhost" || hostname === "[::1]";
}
