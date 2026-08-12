using Litos.Agent.Messages;
using Litos.Api.Turns;
using Litos.Tools.Attachments;
using Microsoft.AspNetCore.Http;

namespace Litos.Api.Tests.Turns;

public class AttachmentContentBuilderTests
{
    private sealed class FakeAttachmentConverter : IAttachmentConverter
    {
        public AttachmentSource? LastSource { get; private set; }

        public Task<DocumentMarkdown> ConvertAsync(AttachmentSource source, CancellationToken ct)
        {
            LastSource = source;
            return Task.FromResult(new DocumentMarkdown("converted-title", "converted markdown body", []));
        }
    }

    private sealed class ThrowingAttachmentConverter : IAttachmentConverter
    {
        public Task<DocumentMarkdown> ConvertAsync(AttachmentSource source, CancellationToken ct) =>
            throw new InvalidOperationException("ConvertAsync should not have been called for a binary file.");
    }

    private static IFormFile CreateFormFile(string fileName, byte[] content, string contentType = "application/octet-stream")
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName) { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    [Fact]
    public async Task BuildContentAsync_NoFiles_ReturnsSingleTextBlockFromInput()
    {
        var builder = new AttachmentContentBuilder(new FakeAttachmentConverter());

        var content = await builder.BuildContentAsync("hello there", [], CancellationToken.None);

        var block = Assert.Single(content);
        var text = Assert.IsType<TextBlock>(block);
        Assert.Equal("hello there", text.Text);
    }

    [Fact]
    public async Task BuildContentAsync_NullOrEmptyInput_ProducesNoTextBlockForInput()
    {
        var builder = new AttachmentContentBuilder(new FakeAttachmentConverter());

        var content = await builder.BuildContentAsync(null, [], CancellationToken.None);

        Assert.Empty(content);
    }

    [Theory]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("photo.gif", "image/gif")]
    [InlineData("photo.webp", "image/webp")]
    public async Task BuildContentAsync_ImageFile_ReturnsImageBlockWithCorrectMediaTypeAndBytes(string fileName, string expectedMediaType)
    {
        var converter = new ThrowingAttachmentConverter();
        var builder = new AttachmentContentBuilder(converter);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var file = CreateFormFile(fileName, bytes);

        var content = await builder.BuildContentAsync(null, [file], CancellationToken.None);

        var block = Assert.Single(content);
        var image = Assert.IsType<ImageBlock>(block);
        Assert.Equal(expectedMediaType, image.MediaType);
        Assert.Equal(bytes, image.Data);
    }

    [Fact]
    public async Task BuildContentAsync_DocumentFile_CallsConverterAndWrapsResultInUntrustedContentMarkers()
    {
        var converter = new FakeAttachmentConverter();
        var builder = new AttachmentContentBuilder(converter);
        var file = CreateFormFile("report.pdf", [1, 2, 3]);

        var content = await builder.BuildContentAsync(null, [file], CancellationToken.None);

        var block = Assert.Single(content);
        var text = Assert.IsType<TextBlock>(block);
        Assert.Contains("### Attachment: converted-title", text.Text);
        Assert.Contains("<<<EXTERNAL_UNTRUSTED_CONTENT source=\"http_attachment:report.pdf\">>>", text.Text);
        Assert.Contains("converted markdown body", text.Text);
        Assert.Contains("<<<END_EXTERNAL_UNTRUSTED_CONTENT>>>", text.Text);

        var streamSource = Assert.IsType<StreamSource>(converter.LastSource);
        Assert.Equal(".pdf", streamSource.Extension);
    }

    [Fact]
    public async Task BuildContentAsync_UnknownBinaryFile_IsSkippedWithoutCallingConverter()
    {
        // ThrowingAttachmentConverter proves the binary guard short-circuits before conversion —
        // if the guard were missing or broken, this test would fail with the converter's exception
        // rather than a clean assertion failure.
        var builder = new AttachmentContentBuilder(new ThrowingAttachmentConverter());
        var file = CreateFormFile("mystery.bin", [0x01, 0x00, 0x02, 0x03]);

        var content = await builder.BuildContentAsync(null, [file], CancellationToken.None);

        var block = Assert.Single(content);
        var text = Assert.IsType<TextBlock>(block);
        Assert.Contains("mystery.bin", text.Text);
        Assert.Contains("skipped", text.Text);
    }

    [Fact]
    public async Task BuildContentAsync_KnownDocumentFormatThatHappensToBeBinary_StillGoesToConverter()
    {
        // PDFs etc. are binary but recognized by PlainTextMedia.IsKnownDocumentFormat, so the
        // binary guard must not skip them — mirrors MarkItDownAttachmentConverter's own contract.
        var converter = new FakeAttachmentConverter();
        var builder = new AttachmentContentBuilder(converter);
        var file = CreateFormFile("report.pdf", [0x25, 0x50, 0x00, 0x44, 0x46]); // NUL byte present

        var content = await builder.BuildContentAsync(null, [file], CancellationToken.None);

        var block = Assert.Single(content);
        Assert.IsType<TextBlock>(block);
        Assert.NotNull(converter.LastSource);
    }

    [Fact]
    public async Task BuildContentAsync_TextInputPlusMultipleFiles_PreservesOrderTextFirstThenFilesInOrder()
    {
        var builder = new AttachmentContentBuilder(new FakeAttachmentConverter());
        var imageFile = CreateFormFile("a.png", [9, 9, 9]);
        var docFile = CreateFormFile("b.pdf", [1, 2, 3]);

        var content = await builder.BuildContentAsync("please review", [imageFile, docFile], CancellationToken.None);

        Assert.Equal(3, content.Count);
        Assert.IsType<TextBlock>(content[0]);
        Assert.Equal("please review", ((TextBlock)content[0]).Text);
        Assert.IsType<ImageBlock>(content[1]);
        Assert.IsType<TextBlock>(content[2]);
    }
}
