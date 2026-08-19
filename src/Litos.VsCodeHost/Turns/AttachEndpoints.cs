using Litos.Agent.Messages;
using Litos.Tools.Attachments;

namespace Litos.VsCodeHost.Turns;

/// <summary>
/// Backs /attach and clipboard paste-to-attach — the two were scoped together (see
/// ReadMe_VsCodeExtension.md) since both need the identical missing capability: turning bytes into
/// a content block. Two request shapes rather than one, matching how the two sources actually
/// differ: /attach's file picker (extension.ts, vscode.window.showOpenDialog) already has a real
/// filesystem path — reading it here mirrors AttachHandler.AttachPathAsync exactly, no base64
/// round-trip needed. Clipboard paste has no path (it's an in-memory blob from the browser
/// Clipboard API) — that one genuinely needs base64-in-JSON, the natural fit for a local
/// single-user host (simpler than multipart, matching TurnsEndpoints' own existing JSON-only
/// design) that Litos.Api's IFormFile-based AttachmentContentBuilder isn't shaped for.
///
/// Returns a ContentBlock (ImageBlock or TextBlock), serialized generically like AgentEvent
/// (System.Text.Json's default: PascalCase, runtime-type-based, no discriminator) — the webview
/// doesn't need to inspect this shape itself, it's held opaquely and sent back verbatim as part of
/// the next turn's content array (see /sessions/{id}/turns's own attachments field, added
/// alongside this).
/// </summary>
public static class AttachEndpoints
{
    private const long MaxAttachmentBytes = 20 * 1024 * 1024; // Same cap Litos.Api's TurnsEndpoints/ShareFileTool use.

    public static IEndpointRouteBuilder MapAttachEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/attachments/from-path", async (AttachPathRequest request, IAttachmentConverter converter, CancellationToken ct) =>
        {
            if (!File.Exists(request.Path))
                return Results.BadRequest($"File not found: {request.Path}");

            var fileInfo = new FileInfo(request.Path);
            if (fileInfo.Length > MaxAttachmentBytes)
                return Results.BadRequest($"{request.Path} is {fileInfo.Length / (1024 * 1024)}MB, exceeding the {MaxAttachmentBytes / (1024 * 1024)}MB attachment limit.");

            if (ImageMedia.TryGetMimeType(request.Path, out var mimeType))
            {
                var bytes = await File.ReadAllBytesAsync(request.Path, ct);
                return Results.Ok(new AttachedContent("image", Path.GetFileName(request.Path), mimeType, Convert.ToBase64String(bytes), null));
            }

            if (!PlainTextMedia.IsKnownDocumentFormat(request.Path) && PlainTextMedia.IsBinary(request.Path))
                return Results.BadRequest($"'{Path.GetFileName(request.Path)}' is a binary file with no known document format — not attachable.");

            var converted = await converter.ConvertAsync(new FilePathSource(request.Path), ct);
            var wrapped = UntrustedContent.Wrap($"vscode_attachment:{Path.GetFileName(request.Path)}", converted.Markdown);
            return Results.Ok(new AttachedContent("document", Path.GetFileName(request.Path), null, null, $"### Attachment: {converted.Title}\n\n{wrapped}"));
        });

        app.MapPost("/attachments/from-bytes", (AttachBytesRequest request) =>
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(request.Base64Data);
            }
            catch (FormatException)
            {
                return Results.BadRequest("Base64Data is not valid base64.");
            }

            if (bytes.Length > MaxAttachmentBytes)
                return Results.BadRequest($"Pasted image is {bytes.Length / (1024 * 1024)}MB, exceeding the {MaxAttachmentBytes / (1024 * 1024)}MB attachment limit.");

            // Some clipboard sources report an image DataTransferItem with an empty or missing
            // MIME type (observed via certain OS screenshot tools' clipboard output) — the webview
            // extension now defaults this itself before calling here (see extension.ts's
            // pasteAttach handler), but this endpoint is the last real checkpoint before an
            // ImageBlock no provider can process gets written into a session's transcript forever
            // (JSONL is append-only — a bad attachment isn't just a failed turn, it silently
            // re-attaches to every later turn on that same session too, see TurnsEndpoints.cs).
            if (string.IsNullOrWhiteSpace(request.MimeType) || !request.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("MimeType must be a valid image/* MIME type.");

            return Results.Ok(new AttachedContent("image", request.FileName ?? "pasted-image.png", request.MimeType, Convert.ToBase64String(bytes), null));
        });

        return app;
    }

    /// <summary>Converts an AttachedContent (as returned above, and as sent back on the next
    /// turn's Attachments list) into the ContentBlock AgentLoop actually needs.</summary>
    public static ContentBlock ToContentBlock(this AttachedContent content) => content.Kind switch
    {
        "image" => new ImageBlock(content.MimeType!, Convert.FromBase64String(content.Base64Data!)),
        "document" => new TextBlock(content.DocumentText!),
        _ => throw new ArgumentOutOfRangeException(nameof(content), content.Kind, "Unknown attachment kind."),
    };
}

public sealed record AttachPathRequest(string Path);

public sealed record AttachBytesRequest(string Base64Data, string? MimeType, string? FileName);

public sealed record AttachedContent(string Kind, string FileName, string? MimeType, string? Base64Data, string? DocumentText);
