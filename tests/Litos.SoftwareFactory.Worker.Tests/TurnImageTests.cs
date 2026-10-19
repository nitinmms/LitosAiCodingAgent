using Litos.SoftwareFactory.Contracts;
using Microsoft.AspNetCore.Http;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// The images the host asks a turn to show the model are read by the worker from its working
/// copy (attachments-architecture.md §4), and only from there.
/// </summary>
public sealed class TurnImageTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly WorkingCopyGuard _guard;

    public TurnImageTests()
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, ".factory", "attachments"));
        _guard = new WorkingCopyGuard(_root.Path);
    }

    public void Dispose() => _root.Dispose();

    private string Write(string relative, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(_root.Path, relative), bytes);
        return relative;
    }

    [Fact]
    public async Task AnImageInTheWorkingCopy_IsReadAsAnImageBlock()
    {
        var path = Write(".factory/attachments/a-shot.png", [1, 2, 3]);

        var block = await WorkerApp.ReadImageAsync(_guard, new TurnImage(path, "image/png"), default);

        Assert.Equal("image/png", block.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, block.Data);
    }

    public static TheoryData<string, TurnImage, string> Refusals => new()
    {
        { "outside the working copy", new TurnImage("../elsewhere.png", "image/png"), "" },
        { "not an image type", new TurnImage(".factory/attachments/a-shot.png", "image/svg+xml"), "not an image type" },
        { "missing", new TurnImage(".factory/attachments/none.png", "image/png"), "is not in the working copy" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task AnImageTheWorkerMayNotRead_RefusesTheTurn(string name, TurnImage image, string reason)
    {
        Write(".factory/attachments/a-shot.png", [1]);

        var refused = await Assert.ThrowsAsync<BadHttpRequestException>(() => WorkerApp.ReadImageAsync(_guard, image, default));

        Assert.True(refused.Message.Contains(reason, StringComparison.Ordinal), name);
    }

    [Fact]
    public async Task AnImageOverTheCap_RefusesTheTurn()
    {
        var path = Path.Combine(_root.Path, ".factory", "attachments", "big.png");
        await using (var file = File.Create(path))
            file.SetLength(AttachmentTypes.MaxFileBytes + 1);

        var refused = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => WorkerApp.ReadImageAsync(_guard, new TurnImage(".factory/attachments/big.png", "image/png"), default));

        Assert.Contains("over 20 MB", refused.Message);
    }

    [Theory]
    [InlineData("shot.PNG", true, "image/png")]
    [InlineData("photo.jpeg", true, "image/jpeg")]
    [InlineData("anim.gif", true, "image/gif")]
    [InlineData("pic.webp", true, "image/webp")]
    [InlineData("icon.svg", false, null)]
    [InlineData("scan.tiff", false, null)]
    public void ImageTypes_AreTheVsCodeAgents(string fileName, bool isImage, string? mediaType)
    {
        Assert.Equal(isImage, AttachmentTypes.TryGetImageMediaType(fileName, out var found));
        if (isImage)
            Assert.Equal(mediaType, found);
    }

    [Theory]
    [InlineData("spec.pdf", true)]
    [InlineData("notes.DOCX", true)]
    [InlineData("deck.pptx", true)]
    [InlineData("sheet.xlsx", true)]
    [InlineData("mail.eml", true)]
    [InlineData("data.csv", false)] // text: read as it is, not converted
    [InlineData("app.exe", false)]
    public void DocumentTypes_AreTheVsCodeAgents(string fileName, bool isDocument) =>
        Assert.Equal(isDocument, AttachmentTypes.IsDocument(fileName));
}
