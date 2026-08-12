using Litos.Api.Turns;
using Microsoft.AspNetCore.Http;

namespace Litos.Api.Tests.Turns;

public class TurnsEndpointsTests
{
    private static IFormFile CreateFormFile(string fileName, long length) =>
        new FormFile(new MemoryStream(new byte[length]), 0, length, "file", fileName);

    [Fact]
    public void ValidateAttachments_NoFiles_ReturnsNull()
    {
        Assert.Null(TurnsEndpoints.ValidateAttachments([]));
    }

    [Fact]
    public void ValidateAttachments_FilesWithinLimits_ReturnsNull()
    {
        var files = new[] { CreateFormFile("a.txt", 1024), CreateFormFile("b.txt", 2048) };

        Assert.Null(TurnsEndpoints.ValidateAttachments(files));
    }

    [Fact]
    public void ValidateAttachments_FileExceedsMaxBytes_ReturnsError()
    {
        var files = new[] { CreateFormFile("huge.bin", TurnsEndpoints.MaxAttachmentBytes + 1) };

        var error = TurnsEndpoints.ValidateAttachments(files);

        Assert.NotNull(error);
        Assert.Contains("huge.bin", error);
    }

    [Fact]
    public void ValidateAttachments_FileExactlyAtMaxBytes_ReturnsNull()
    {
        var files = new[] { CreateFormFile("exact.bin", TurnsEndpoints.MaxAttachmentBytes) };

        Assert.Null(TurnsEndpoints.ValidateAttachments(files));
    }

    [Fact]
    public void ValidateAttachments_TooManyFiles_ReturnsError()
    {
        var files = Enumerable.Range(0, TurnsEndpoints.MaxAttachmentCount + 1)
            .Select(i => CreateFormFile($"f{i}.txt", 10))
            .ToArray();

        var error = TurnsEndpoints.ValidateAttachments(files);

        Assert.NotNull(error);
        Assert.Contains((TurnsEndpoints.MaxAttachmentCount + 1).ToString(), error);
    }

    [Fact]
    public void ValidateAttachments_ExactlyAtMaxCount_ReturnsNull()
    {
        var files = Enumerable.Range(0, TurnsEndpoints.MaxAttachmentCount)
            .Select(i => CreateFormFile($"f{i}.txt", 10))
            .ToArray();

        Assert.Null(TurnsEndpoints.ValidateAttachments(files));
    }

    [Fact]
    public void ValidateAttachments_CountCheckedBeforeSize_ReturnsCountErrorFirst()
    {
        // Both limits are violated at once; the count check should short-circuit before iterating
        // files, since a too-large collection is the cheaper/cleaner rejection to report first.
        var files = Enumerable.Range(0, TurnsEndpoints.MaxAttachmentCount + 1)
            .Select(i => CreateFormFile($"f{i}.bin", TurnsEndpoints.MaxAttachmentBytes + 1))
            .ToArray();

        var error = TurnsEndpoints.ValidateAttachments(files);

        Assert.NotNull(error);
        Assert.Contains("Too many attachments", error);
    }
}
