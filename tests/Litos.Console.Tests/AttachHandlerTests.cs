using Litos.Tools.Attachments;

namespace Litos.Console.Tests;

/// <summary>
/// Tests for AttachHandler, the logic shared by /attach and @mention handling (Program.cs):
/// routing image files to native vision content vs. everything else through
/// IAttachmentConverter, and /attach's URL-vs-path dispatch. Uses a fake IAttachmentConverter
/// (recording the AttachmentSource it was called with) rather than the real MarkItDown-backed
/// one — MarkItDownAttachmentConverterTests already covers actual conversion behavior; this
/// only needs to prove AttachHandler routes to the right source and shape.
/// </summary>
public class AttachHandlerTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-attachhandler-").FullName;
    private readonly FakeAttachmentConverter _converter = new();

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private AttachHandler CreateHandler() => new(_converter);

    [Fact]
    public async Task AttachPathAsync_ImageFile_ProducesImageNotDocument()
    {
        var path = Path.Combine(_tempDir, "photo.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var handler = CreateHandler();

        var result = await handler.AttachPathAsync(path, CancellationToken.None);

        Assert.Null(result.Document);
        Assert.NotNull(result.Image);
        Assert.Equal("image/png", result.Image!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Image.Data);
        Assert.Contains("Attached image 'photo.png'", result.Lines.Single());
        Assert.Empty(_converter.Calls);
    }

    [Fact]
    public async Task AttachPathAsync_NonImageFile_GoesThroughConverter()
    {
        var path = Path.Combine(_tempDir, "note.txt");
        await File.WriteAllTextAsync(path, "hello");
        var handler = CreateHandler();

        var result = await handler.AttachPathAsync(path, CancellationToken.None);

        Assert.Null(result.Image);
        Assert.NotNull(result.Document);
        Assert.Equal("hello-converted", result.Document!.Markdown);
        var source = Assert.IsType<FilePathSource>(Assert.Single(_converter.Calls));
        Assert.Equal(path, source.Path);
    }

    [Fact]
    public async Task AttachPathAsync_UnrecognizedBinaryFile_IsSkippedWithoutReachingConverter()
    {
        var path = Path.Combine(_tempDir, "app.exe");
        await File.WriteAllBytesAsync(path, [0x4D, 0x5A, 0x00, 0x01]); // "MZ" header + a NUL byte
        var handler = CreateHandler();

        var result = await handler.AttachPathAsync(path, CancellationToken.None);

        Assert.Null(result.Document);
        Assert.Null(result.Image);
        Assert.Contains("skipped 'app.exe'", result.Lines.Single());
        Assert.Empty(_converter.Calls);
    }

    [Fact]
    public async Task AttachPathAsync_KnownDocumentExtension_StillGoesThroughConverter_EvenThoughBinary()
    {
        var path = Path.Combine(_tempDir, "report.pdf");
        await File.WriteAllBytesAsync(path, [0x25, 0x50, 0x44, 0x46, 0x00]); // "%PDF" + a NUL byte
        var handler = CreateHandler();

        var result = await handler.AttachPathAsync(path, CancellationToken.None);

        Assert.NotNull(result.Document);
        var source = Assert.IsType<FilePathSource>(Assert.Single(_converter.Calls));
        Assert.Equal(path, source.Path);
    }

    [Fact]
    public async Task AttachPathAsync_ConverterWarnings_BecomePrintedLines()
    {
        _converter.NextWarnings = ["heads up"];
        var path = Path.Combine(_tempDir, "note.txt");
        await File.WriteAllTextAsync(path, "hello");
        var handler = CreateHandler();

        var result = await handler.AttachPathAsync(path, CancellationToken.None);

        Assert.Equal(["Warning: heads up"], result.Lines);
    }

    [Fact]
    public async Task AttachArgumentAsync_LocalPath_AppendsConfirmationLineAfterAnyWarnings()
    {
        var path = Path.Combine(_tempDir, "note.txt");
        await File.WriteAllTextAsync(path, "hello");
        var handler = CreateHandler();

        var result = await handler.AttachArgumentAsync(path, CancellationToken.None);

        Assert.Equal([$"Attached '{path}' — will be included in your next message."], result.Lines);
    }

    [Fact]
    public async Task AttachArgumentAsync_ImagePath_StillReportsImageConfirmation()
    {
        var path = Path.Combine(_tempDir, "photo.png");
        await File.WriteAllBytesAsync(path, [9]);
        var handler = CreateHandler();

        var result = await handler.AttachArgumentAsync(path, CancellationToken.None);

        Assert.NotNull(result.Image);
        Assert.Equal(2, result.Lines.Count);
        Assert.Contains("Attached image 'photo.png'", result.Lines[0]);
        Assert.Equal($"Attached '{path}' — will be included in your next message.", result.Lines[1]);
    }

    [Fact]
    public async Task AttachArgumentAsync_HttpUrl_GoesThroughConverterAsUrlSource()
    {
        var handler = CreateHandler();

        var result = await handler.AttachArgumentAsync("https://example.com/doc", CancellationToken.None);

        var source = Assert.IsType<UrlSource>(Assert.Single(_converter.Calls));
        Assert.Equal("https://example.com/doc", source.Url.ToString());
        Assert.NotNull(result.Document);
        Assert.Null(result.Image);
    }

    [Fact]
    public async Task AttachArgumentAsync_HttpUrl_ReportsTitleAndLength()
    {
        _converter.NextTitle = "Example Doc";
        _converter.NextMarkdown = "0123456789";
        var handler = CreateHandler();

        var result = await handler.AttachArgumentAsync("https://example.com/doc", CancellationToken.None);

        Assert.Equal(["Attached 'Example Doc' (10 chars) — will be included in your next message."], result.Lines);
    }

    [Fact]
    public async Task AttachArgumentAsync_NonHttpArgument_IsTreatedAsFilePath()
    {
        // "ftp://" is an absolute URI but not http/https, so it should be treated like a
        // plain (non-existent) path and surface the converter's own failure, not silently
        // route to UrlSource.
        var handler = CreateHandler();

        var result = await handler.AttachArgumentAsync("ftp://example.com/doc", CancellationToken.None);

        var source = Assert.IsType<FilePathSource>(Assert.Single(_converter.Calls));
        Assert.Equal("ftp://example.com/doc", source.Path);
    }

    private sealed class FakeAttachmentConverter : IAttachmentConverter
    {
        public List<AttachmentSource> Calls { get; } = [];
        public string NextTitle { get; set; } = "title";
        public string NextMarkdown { get; set; } = "hello-converted";
        public IReadOnlyList<string> NextWarnings { get; set; } = [];

        public Task<DocumentMarkdown> ConvertAsync(AttachmentSource source, CancellationToken ct)
        {
            Calls.Add(source);
            return Task.FromResult(new DocumentMarkdown(NextTitle, NextMarkdown, NextWarnings));
        }
    }
}
