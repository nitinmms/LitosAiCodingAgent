using Litos.Tools.Attachments;

namespace Litos.Tools.Tests.Attachments;

public class PlainTextMediaTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-plaintextmedia-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("notes.docx")]
    [InlineData("slides.pptx")]
    [InlineData("budget.xlsx")]
    public void IsKnownDocumentFormat_ReturnsTrue_ForFormatsMarkItDownHandles(string fileName)
    {
        Assert.True(PlainTextMedia.IsKnownDocumentFormat(Path.Combine("C:\\repo", fileName)));
    }

    [Theory]
    [InlineData("HelpForm.cs")]
    [InlineData("photo.png")]
    [InlineData("archive.zip")]
    public void IsKnownDocumentFormat_ReturnsFalse_ForEverythingElse(string fileName)
    {
        Assert.False(PlainTextMedia.IsKnownDocumentFormat(Path.Combine("C:\\repo", fileName)));
    }

    [Fact]
    public void IsKnownDocumentFormat_IsCaseInsensitive()
    {
        Assert.True(PlainTextMedia.IsKnownDocumentFormat("Report.PDF"));
    }

    [Fact]
    public async Task IsBinary_ReturnsFalse_ForTextContent()
    {
        var path = Path.Combine(_tempDir, "HelpForm.cs");
        await File.WriteAllTextAsync(path, "namespace SankeAndLadders;\n");

        Assert.False(PlainTextMedia.IsBinary(path));
    }

    [Fact]
    public async Task IsBinary_ReturnsTrue_WhenContentHasANulByte()
    {
        var path = Path.Combine(_tempDir, "data.bin");
        await File.WriteAllBytesAsync(path, [0x01, 0x00, 0x02, 0x03]);

        Assert.True(PlainTextMedia.IsBinary(path));
    }

    [Fact]
    public async Task IsBinary_ReturnsFalse_ForEmptyFile()
    {
        var path = Path.Combine(_tempDir, "empty.txt");
        await File.WriteAllTextAsync(path, "");

        Assert.False(PlainTextMedia.IsBinary(path));
    }
}
