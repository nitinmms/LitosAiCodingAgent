namespace Litos.Tools.Attachments;

/// <summary>
/// Decides how MarkItDownAttachmentConverter routes a local file, for extensions MarkItDown
/// isn't meant to receive raw. Two problems motivated this: an unrecognized extension (e.g.
/// .cs) falls back to a generic application/octet-stream mime type with no registered
/// converter and throws UnsupportedFormatException; a recognized-but-unintended one can get
/// misrouted into a structured-format sniffer (e.g. tabular/CSV parsing) that fails on ordinary
/// source code. Rather than maintain an allowlist of every source/config extension that should
/// be read as plain text, content is sniffed the same way GrepTool.IsBinaryFile already decides
/// whether a file belongs in a text search: a NUL byte in the first 8 KB means binary, anything
/// else is read directly as UTF-8 text and never reaches MarkItDown at all. Files MarkItDown
/// actually has a real converter for (PDF, Office formats, etc.) are the one exception —
/// KnownDocumentExtensions routes those to MarkItDown even though most are binary, so real
/// document-conversion capability isn't lost by treating "binary" as "skip."
/// </summary>
public static class PlainTextMedia
{
    private static readonly HashSet<string> KnownDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".epub", ".msg", ".eml",
    };

    public static bool IsKnownDocumentFormat(string path) => KnownDocumentExtensions.Contains(Path.GetExtension(path));

    /// <summary>NUL byte in the first 8 KB — the same heuristic GrepTool.IsBinaryFile and git itself use.</summary>
    public static bool IsBinary(string path)
    {
        const int sampleSize = 8192;
        Span<byte> buffer = stackalloc byte[sampleSize];

        try
        {
            using var stream = File.OpenRead(path);
            var read = stream.Read(buffer);
            return buffer[..read].Contains((byte)0);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
