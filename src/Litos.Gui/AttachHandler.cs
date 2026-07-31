using Litos.Agent.Messages;
using Litos.Tools.Attachments;

namespace Litos.Gui;

/// <summary>
/// Result of attaching a single path or URL: the lines to print, plus at most one of
/// Document/Image to add to the caller's pending-attachment state (never both — a given
/// source is either an image or goes through MarkItDown). Ported from
/// Litos.Console/AttachHandler.cs (Console-local, not shared).
/// </summary>
public sealed record AttachResult(IReadOnlyList<string> Lines, DocumentMarkdown? Document, ImageBlock? Image);

/// <summary>
/// Backs /attach: images bypass MarkItDown entirely and go to the model as native vision
/// content (ReadMe_AgentDesign.md §6.2) — MarkItDown's own image handling only extracts EXIF
/// metadata, which isn't visual content. A binary file MarkItDown has no real converter for
/// (anything not a recognized document format like PDF/DOCX — see PlainTextMedia) is skipped
/// entirely rather than sent to MarkItDown, which would otherwise throw or, for some
/// recognized-but-unintended extensions, misroute the file into a structured-format sniffer
/// that fails on it. Every other file type (plain text, source code, or a real document format)
/// goes through IAttachmentConverter.
/// </summary>
public sealed class AttachHandler(IAttachmentConverter converter)
{
    public async Task<AttachResult> AttachPathAsync(string path, CancellationToken ct)
    {
        if (ImageMedia.TryGetMimeType(path, out var mimeType))
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return new AttachResult(
                [$"Attached image '{Path.GetFileName(path)}' ({bytes.Length} bytes) — will be sent to the model directly."],
                Document: null,
                Image: new ImageBlock(mimeType, bytes));
        }

        if (File.Exists(path) && !PlainTextMedia.IsKnownDocumentFormat(path) && PlainTextMedia.IsBinary(path))
        {
            return new AttachResult(
                [$"Warning: skipped '{Path.GetFileName(path)}' — binary file, not attached."],
                Document: null,
                Image: null);
        }

        var converted = await converter.ConvertAsync(new FilePathSource(path), ct);
        return new AttachResult(converted.Warnings.Select(w => $"Warning: {w}").ToList(), converted, Image: null);
    }

    /// <summary>
    /// Handles the /attach &lt;path-or-url&gt; argument: dispatches to a URL or file-path
    /// attachment and appends the confirmation line.
    /// </summary>
    public async Task<AttachResult> AttachArgumentAsync(string argument, CancellationToken ct)
    {
        var isUrl = Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        if (!isUrl)
        {
            var result = await AttachPathAsync(argument, ct);
            return result with { Lines = [.. result.Lines, $"Attached '{argument}' — will be included in your next message."] };
        }

        var converted = await converter.ConvertAsync(new UrlSource(uri!), ct);
        List<string> lines = [.. converted.Warnings.Select(w => $"Warning: {w}"),
            $"Attached '{converted.Title}' ({converted.Markdown.Length} chars) — will be included in your next message."];
        return new AttachResult(lines, converted, Image: null);
    }
}
