using System.Text.Json;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.VsCodeHost;

namespace Litos.VsCodeHost.Files;

/// <summary>
/// Lets the model share a file from the workspace with the user as a downloadable hyperlink,
/// rendered clickable in the webview transcript the same way Litos.Api's example clients already
/// linkify "[label](http://...)" markdown-style links (see AngularChat/app.js's own "linkify"
/// filter — webviewContent.ts's escapeAndLinkify does the equivalent for this face). Not
/// approval-gated, matching Litos.Api's own ShareFileTool: treated like a read-only tool, same as
/// ReadFileTool — McpAwareApprovalGate only intercepts mcp__-prefixed calls, so this flows straight
/// to the inner AutoApprovalGate regardless.
///
/// Unlike Litos.Api's version, there is no PUBLIC_BASE_URL concept here — this host is always
/// loopback-only (Program.cs binds http://127.0.0.1:0), so the base URL for a share link is always
/// known and always reachable from the webview that's rendering it (same origin the webview's own
/// fetch calls already use), never "operator hasn't configured it yet."
///
/// Takes the LoopbackBaseUrl holder, not a plain string — this tool is registered in DI before
/// Build()/StartAsync() resolve the real port (Program.cs), so baseUrl.Value is read lazily inside
/// InvokeAsync, by which point StartAsync() has always already populated it (no turn — and so no
/// tool call — can run before this process has finished starting and reported its port).
/// </summary>
public sealed class ShareFileTool(SharedFileStore store, LoopbackBaseUrl baseUrl) : ITool
{
    /// <summary>Same 20MB cap Litos.Api's TurnsEndpoints.MaxAttachmentBytes uses for attachments.</summary>
    public const long MaxShareBytes = 20 * 1024 * 1024;

    public string Name => "share_file";

    public string Description => "Share a file from the workspace with the user as a downloadable link, valid for 24 hours.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Path to the file to share." },
        },
        required = new[] { "path" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("The 'path' argument is required.");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxShareBytes)
            return ToolResult.Error($"{path} is {fileInfo.Length / (1024 * 1024)}MB, which exceeds the {MaxShareBytes / (1024 * 1024)}MB share limit.");

        var owner = ChannelContext.Owner ?? SessionOwner.Local;
        var shared = await store.ShareAsync(owner, path, ct);

        var url = $"{baseUrl.Value.TrimEnd('/')}/files/{shared.Token}";
        return ToolResult.Ok($"Shared {Path.GetFileName(path)}: {url} (expires {shared.ExpiresAt:u}).");
    }
}
