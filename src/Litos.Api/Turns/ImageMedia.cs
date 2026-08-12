namespace Litos.Api.Turns;

/// <summary>
/// Recognizes image files that should bypass MarkItDown (text/EXIF extraction) and instead be
/// attached as native vision content — see ReadMe_AgentDesign.md §6.2. Mirrors the format list
/// every active provider (Anthropic/OpenAI/Gemini) accepts as inline image data. Ported from
/// Litos.Gui/ImageMedia.cs (face-local, not shared — same convention that file itself follows
/// when porting from Litos.Console).
/// </summary>
public static class ImageMedia
{
    private static readonly Dictionary<string, string> ExtensionToMimeType = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public static bool TryGetMimeType(string path, out string mimeType) =>
        ExtensionToMimeType.TryGetValue(Path.GetExtension(path), out mimeType!);
}
