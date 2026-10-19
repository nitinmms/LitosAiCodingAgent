namespace Litos.SoftwareFactory.Contracts;

/// <summary>
/// Body of the worker's /sessions/{id}/turns as the factory host sends it: Litos.Hosting's
/// TurnRequest plus the images the turn carries (attachments-architecture.md §4).
/// </summary>
public sealed record FactoryTurnRequest(string Input, string? TurnKind = null, IReadOnlyList<TurnImage>? Images = null);

/// <summary>
/// An image the turn shows the model, by its path relative to the working copy. The worker reads
/// the bytes itself, so no image crosses the host-to-worker call.
/// </summary>
public sealed record TurnImage(string Path, string MediaType);

/// <summary>
/// The file types a task may be given: the Litos VS Code agent's, no more and no fewer
/// (Litos.VsCodeHost's ImageMedia and AttachEndpoints, Litos.Tools' PlainTextMedia;
/// attachments-architecture.md §1). Kept here because the host checks uploads against them and
/// the worker checks the images it is asked to read.
/// </summary>
public static class AttachmentTypes
{
    /// <summary>The same cap as every other Litos face.</summary>
    public const long MaxFileBytes = 20 * 1024 * 1024;

    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    /// <summary>Documents MarkItDown converts to Markdown, binary or not.</summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".epub", ".msg", ".eml",
    };

    /// <summary>The image media types a provider accepts as inline image data.</summary>
    public static IReadOnlyCollection<string> ImageMediaTypes { get; } = ImageExtensions.Values.ToHashSet();

    public static bool TryGetImageMediaType(string fileName, out string mediaType) =>
        ImageExtensions.TryGetValue(Path.GetExtension(fileName), out mediaType!);

    public static bool IsImageMediaType(string mediaType) => ImageMediaTypes.Contains(mediaType);

    public static bool IsDocument(string fileName) => DocumentExtensions.Contains(Path.GetExtension(fileName));
}
