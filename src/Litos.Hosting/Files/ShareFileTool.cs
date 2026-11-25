using System.Text.Json;
using Litos.Agent.Session;
using Litos.Agent.Tools;

namespace Litos.Hosting.Files;

/// <summary>
/// Lets the model share a file from the workspace with the user as a downloadable hyperlink,
/// served by the host's own loopback /files/{token} route (<see cref="SharedFileEndpoints"/>).
/// Shared by Litos.VsCodeHost and Litos.Gui, both loopback-only, so the base URL is always known
/// and reachable from the machine rendering the link. Not approval-gated, matching Litos.Api's own
/// ShareFileTool: treated like a read-only tool, same as ReadFileTool — McpAwareApprovalGate only
/// intercepts mcp__-prefixed calls, so this flows straight to the inner gate regardless.
///
/// The base URL is a delegate read lazily inside InvokeAsync (never at construction), the same
/// shape ArtifactTool uses: the tool is registered in DI before the host's listener has bound its
/// OS-assigned port, and no tool call can run before that port is known.
/// </summary>
public sealed class ShareFileTool(SharedFileStore store, Func<string> baseUrl) : ITool
{
    public const long MaxShareBytes = Litos.Tools.Attachments.AttachmentLimits.MaxFileBytes;

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

        var url = $"{baseUrl().TrimEnd('/')}/files/{shared.Token}";
        return ToolResult.Ok($"Shared {Path.GetFileName(path)}: {url} (expires {shared.ExpiresAt:u}).");
    }
}
