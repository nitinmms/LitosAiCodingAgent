using System.Text;
using Litos.Tools.Attachments;

namespace Litos.Mail;

/// <summary>
/// Turns attachment bytes into text for the model (Implement_Buzzies.md, Phase 4). A document
/// attachment is not a file the Buzzy can open: its bytes never touch the workspace, they arrive in
/// memory behind an approval. So conversion happens here, through the same MarkItDown converter
/// every other attachment path in Litos uses, and anything that isn't text and isn't a document the
/// converter knows says so plainly instead of dumping binary into a prompt.
/// </summary>
internal static class AttachmentText
{
    /// <summary>Plain types are decoded directly; everything else is offered to the converter,
    /// which is null in a test or a headless run that didn't wire one up.</summary>
    public static async Task<string> OfAsync(
        byte[] bytes, string mediaType, string fileName, IAttachmentConverter? converter, CancellationToken ct)
    {
        if (IsPlainText(mediaType, fileName))
            return Encoding.UTF8.GetString(bytes);

        if (converter is null)
            return $"[{fileName} is a {mediaType} file and no document converter is available this run, so it could not be read.]";

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var converted = await converter.ConvertAsync(
                new StreamSource(stream, Path.GetExtension(fileName), mediaType), ct);
            return converted.Markdown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"[{fileName} is a {mediaType} file that could not be converted to text: {ex.Message}]";
        }
    }

    private static bool IsPlainText(string mediaType, string fileName) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || (!PlainTextMedia.IsKnownDocumentFormat(fileName) && !IsKnownBinaryMediaType(mediaType));

    private static bool IsKnownBinaryMediaType(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("zip", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase);
}
