using global::MarkItDown;

namespace Litos.Tools.Attachments;

public sealed class MarkItDownAttachmentConverter(MarkItDownClient client) : IAttachmentConverter
{
    public async Task<DocumentMarkdown> ConvertAsync(AttachmentSource source, CancellationToken ct)
    {
        // Plain-text local files bypass MarkItDown entirely — see PlainTextMedia's remarks:
        // an unrecognized extension like .cs falls back to a generic mime type MarkItDown has
        // no converter for and throws, and some recognized-but-unintended ones get misrouted
        // into a structured-format sniffer that fails on ordinary source code. AttachHandler is
        // responsible for deciding *whether* a path reaches this converter at all (it skips
        // binary files it doesn't recognize before ever calling ConvertAsync) — this check only
        // covers the FilePathSource/StreamSource-shaped plain-text case, so a caller that has
        // already read a text file into a StreamSource still benefits from the same bypass.
        if (source is FilePathSource { Path: var textPath } && !PlainTextMedia.IsKnownDocumentFormat(textPath) && !PlainTextMedia.IsBinary(textPath))
        {
            var text = await File.ReadAllTextAsync(textPath, ct);
            return new DocumentMarkdown(Path.GetFileName(textPath), text, []);
        }

        await using var result = source switch
        {
            FilePathSource f => await client.ConvertAsync(f.Path, ct),
            UrlSource u => await client.ConvertFromUrlAsync(u.Url, streamInfoOverride: null, ct),
            StreamSource s => await client.ConvertAsync(s.Stream, new StreamInfo(mimeType: s.MimeType, extension: s.Extension), ct),
            _ => throw new NotSupportedException($"Unsupported attachment source: {source.GetType().Name}"),
        };

        return new DocumentMarkdown(result.Title ?? "attachment", result.Markdown, []);
    }
}
