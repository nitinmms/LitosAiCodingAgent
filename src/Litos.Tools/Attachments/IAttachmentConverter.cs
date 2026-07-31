namespace Litos.Tools.Attachments;

public interface IAttachmentConverter
{
    Task<DocumentMarkdown> ConvertAsync(AttachmentSource source, CancellationToken ct);
}

public abstract record AttachmentSource;

public sealed record FilePathSource(string Path) : AttachmentSource;

public sealed record StreamSource(Stream Stream, string? Extension, string? MimeType) : AttachmentSource;

public sealed record UrlSource(Uri Url) : AttachmentSource;

public sealed record DocumentMarkdown(string Title, string Markdown, IReadOnlyList<string> Warnings);
