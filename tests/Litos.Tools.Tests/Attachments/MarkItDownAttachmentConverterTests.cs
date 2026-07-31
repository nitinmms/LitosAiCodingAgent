using System.Text;
using global::MarkItDown;
using Litos.Tools.Attachments;

namespace Litos.Tools.Tests.Attachments;

// Real I/O via MarkItDownClient (concrete class, not behind an interface here) — kept to
// the cheapest conversion paths (plain text via stream/file) to avoid pulling in
// MarkItDown's heavier PDF/OCR dependencies. UrlSource (real network) is intentionally not
// tested here.
public class MarkItDownAttachmentConverterTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-markitdown-").FullName;
    private readonly MarkItDownClient _client = new();

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task ConvertAsync_StreamSource_PlainText_ProducesMarkdown()
    {
        var converter = new MarkItDownAttachmentConverter(_client);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello from a stream"));

        var result = await converter.ConvertAsync(new StreamSource(stream, ".txt", "text/plain"), CancellationToken.None);

        Assert.Contains("hello from a stream", result.Markdown);
    }

    [Fact]
    public async Task ConvertAsync_FilePathSource_PlainTextFile_ProducesMarkdown()
    {
        var path = Path.Combine(_tempDir, "note.txt");
        await File.WriteAllTextAsync(path, "hello from a file");
        var converter = new MarkItDownAttachmentConverter(_client);

        var result = await converter.ConvertAsync(new FilePathSource(path), CancellationToken.None);

        Assert.Contains("hello from a file", result.Markdown);
    }

    [Fact]
    public async Task ConvertAsync_WarningsIsAlwaysEmpty_RegardlessOfConversionResult()
    {
        // The converter hardcodes Warnings to [] and never surfaces anything from the
        // underlying MarkItDown result — documenting this as current behavior.
        var path = Path.Combine(_tempDir, "note.txt");
        await File.WriteAllTextAsync(path, "content");
        var converter = new MarkItDownAttachmentConverter(_client);

        var result = await converter.ConvertAsync(new FilePathSource(path), CancellationToken.None);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ConvertAsync_FilePathSource_SourceCodeFile_BypassesMarkItDown_AndReadsRawText()
    {
        // MarkItDown has no converter for .cs (falls back to application/octet-stream and
        // throws UnsupportedFormatException) — PlainTextMedia routes it around MarkItDown
        // entirely instead, so this must succeed rather than throw.
        var path = Path.Combine(_tempDir, "HelpForm.cs");
        await File.WriteAllTextAsync(path, "namespace SankeAndLadders;\n\npublic partial class HelpForm\n{\n}\n");
        var converter = new MarkItDownAttachmentConverter(_client);

        var result = await converter.ConvertAsync(new FilePathSource(path), CancellationToken.None);

        Assert.Contains("namespace SankeAndLadders;", result.Markdown);
        Assert.Equal("HelpForm.cs", result.Title);
    }

    [Fact]
    public async Task ConvertAsync_FilePathSource_KnownDocumentExtension_StillGoesThroughMarkItDown_EvenThoughItIsBinary()
    {
        // A real PDF/DOCX/etc. is binary (fails PlainTextMedia.IsBinary's text check), but must
        // still reach MarkItDown rather than being treated as unreadable — AttachHandler is what
        // skips genuinely-unrecognized binaries before they ever reach this converter; a known
        // document extension should never take the plain-text bypass here regardless of content.
        // A .pdf extension with non-PDF bytes won't convert cleanly, but the point of this test is
        // only that MarkItDown is invoked (and throws its own format error) rather than the
        // plain-text path silently "succeeding" with garbled binary content as if it were text.
        var path = Path.Combine(_tempDir, "fake.pdf");
        await File.WriteAllBytesAsync(path, [0x25, 0x50, 0x44, 0x46, 0x00, 0x01, 0x02]); // "%PDF" + NUL byte
        var converter = new MarkItDownAttachmentConverter(_client);

        await Assert.ThrowsAnyAsync<Exception>(() => converter.ConvertAsync(new FilePathSource(path), CancellationToken.None));
    }

    [Fact]
    public async Task ConvertAsync_UnsupportedAttachmentSourceType_ThrowsNotSupportedException()
    {
        var converter = new MarkItDownAttachmentConverter(_client);

        await Assert.ThrowsAsync<NotSupportedException>(() => converter.ConvertAsync(new UnknownSource(), CancellationToken.None));
    }

    private sealed record UnknownSource : AttachmentSource;
}
