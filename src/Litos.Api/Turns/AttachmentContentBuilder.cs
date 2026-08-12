using Litos.Agent.Messages;
using Litos.Api.Channels.Telegram;
using Litos.Tools.Attachments;
using Microsoft.AspNetCore.Http;

namespace Litos.Api.Turns;

/// <summary>
/// Builds a turn's ContentBlock list from an HTTP multipart request — the direct HTTP-endpoint
/// counterpart to TelegramSessionDriver.BuildTurnContentAsync, sharing the same
/// IAttachmentConverter/UntrustedContent pipeline. Differs from Telegram's version in two
/// deliberate ways: it recognizes images by extension (Gui's ImageMedia approach, more reliable
/// than Telegram's hardcoded "image/jpeg"), and it guards against binary/unsupported files
/// before calling the converter (ported from Gui's AttachHandler — Telegram's own document path
/// has no equivalent guard and can throw on an unrecognized binary upload; scoped here to the
/// new HTTP endpoint only, per the confirmed decision not to touch Telegram's path).
/// </summary>
public sealed class AttachmentContentBuilder(IAttachmentConverter converter)
{
    public async Task<IReadOnlyList<ContentBlock>> BuildContentAsync(
        string? input, IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        List<ContentBlock> blocks = [];
        if (!string.IsNullOrEmpty(input))
            blocks.Add(new TextBlock(input));

        foreach (var file in files)
            blocks.Add(await BuildBlockAsync(file, ct));

        return blocks;
    }

    private async Task<ContentBlock> BuildBlockAsync(IFormFile file, CancellationToken ct)
    {
        if (ImageMedia.TryGetMimeType(file.FileName, out var mimeType))
        {
            using var imageStream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await imageStream.CopyToAsync(buffer, ct);
            return new ImageBlock(mimeType, buffer.ToArray());
        }

        return await BuildDocumentBlockAsync(file, ct);
    }

    private async Task<ContentBlock> BuildDocumentBlockAsync(IFormFile file, CancellationToken ct)
    {
        var extension = Path.GetExtension(file.FileName);
        await using var stream = file.OpenReadStream();

        if (!PlainTextMedia.IsKnownDocumentFormat(file.FileName) && PlainTextMedia.IsBinary(stream))
        {
            return new TextBlock(
                $"### Attachment: {file.FileName}\n\n" +
                $"(skipped — binary file of a type this endpoint doesn't know how to convert)");
        }

        var source = new StreamSource(stream, extension, file.ContentType);
        var converted = await converter.ConvertAsync(source, ct);
        var wrapped = UntrustedContent.Wrap($"http_attachment:{file.FileName}", converted.Markdown);
        return new TextBlock($"### Attachment: {converted.Title}\n\n{wrapped}");
    }
}
