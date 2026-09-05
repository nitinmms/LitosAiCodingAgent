using System.Text.Json;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Api.Channels;
using Litos.Api.Turns;

namespace Litos.Api.Files;

/// <summary>
/// Lets the model share a file from the workspace with the user as a downloadable hyperlink —
/// works on every channel (unlike Telegram's SendFileTool, which uploads directly to Telegram and
/// has no URL concept). Not approval-gated: treated like a read-only tool, same as ReadFileTool.
/// </summary>
public sealed class ShareFileTool(SharedFileStore store, string? publicBaseUrl) : ITool
{
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
        if (fileInfo.Length > TurnsEndpoints.MaxAttachmentBytes)
            return ToolResult.Error($"{path} is {fileInfo.Length / (1024 * 1024)}MB, which exceeds the {TurnsEndpoints.MaxAttachmentBytes / (1024 * 1024)}MB share limit.");

        var owner = ChannelContext.Owner ?? SessionOwner.Local;
        var shared = await store.ShareAsync(owner, path, ct);

        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            return ToolResult.Ok($"File shared, but PUBLIC_BASE_URL isn't configured so no clickable link could be built. Token: {shared.Token}. Ask the operator to set PUBLIC_BASE_URL and share_file again to get a real link.");

        var url = $"{publicBaseUrl.TrimEnd('/')}/files/{shared.Token}";
        return ToolResult.Ok($"Shared {Path.GetFileName(path)}: {url} (expires {shared.ExpiresAt:u}).");
    }
}
