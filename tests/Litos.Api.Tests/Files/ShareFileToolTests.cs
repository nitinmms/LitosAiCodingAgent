using System.Text.Json;
using Litos.Agent.Session;
using Litos.Api.Channels;
using Litos.Api.Files;

namespace Litos.Api.Tests.Files;

public class ShareFileToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"litos-shared-files-tool-test-{Guid.NewGuid():n}");
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}.txt");

    private static JsonElement Args(string path) => JsonSerializer.SerializeToElement(new { path });

    [Fact]
    public async Task InvokeAsync_FileDoesNotExist_ReturnsError()
    {
        var tool = new ShareFileTool(new SharedFileStore(_root), "https://litos.example.com");
        var missingPath = Path.Combine(Path.GetTempPath(), $"litos-missing-{Guid.NewGuid():n}.txt");

        var result = await tool.InvokeAsync(Args(missingPath), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_FileExceedsShareLimit_ReturnsError()
    {
        using (var fs = new FileStream(_tempFile, FileMode.Create))
            fs.SetLength(21L * 1024 * 1024);
        var tool = new ShareFileTool(new SharedFileStore(_root), "https://litos.example.com");

        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("20MB", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_Success_ReturnsAClickableUrlContainingTheHost()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var tool = new ShareFileTool(new SharedFileStore(_root), "https://litos.example.com");

        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("https://litos.example.com/files/", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_Success_UrlIsActuallyDownloadable_ViaTheSameStore()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var store = new SharedFileStore(_root);
        var tool = new ShareFileTool(store, "https://litos.example.com");

        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);
        var token = result.Text.Split("/files/")[1].Split(' ')[0];

        var resolved = await store.TryGetAsync(token, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal("hello", await File.ReadAllTextAsync(resolved!.Value.FilePath));
    }

    [Fact]
    public async Task InvokeAsync_NoPublicBaseUrlConfigured_StillSharesTheFile_ButReturnsTokenInsteadOfUrl()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var tool = new ShareFileTool(new SharedFileStore(_root), publicBaseUrl: null);

        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("PUBLIC_BASE_URL", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_UsesOwnerFromChannelContext()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var store = new SharedFileStore(_root);
        var tool = new ShareFileTool(store, "https://litos.example.com");

        await ChannelContext.RunAsAsync(SessionOwner.Telegram, "session-1", async () =>
        {
            var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);
            Assert.False(result.IsError);
        });

        var ownerDir = Directory.EnumerateDirectories(_root).Single();
        Assert.EndsWith(SessionOwner.Telegram.Value, ownerDir);
    }

    [Fact]
    public async Task InvokeAsync_MissingPathArgument_ReturnsError()
    {
        // A local model's tool-call JSON omitting a required argument is a real, model-driven
        // failure mode — must degrade to a clean ToolResult.Error, not throw.
        var tool = new ShareFileTool(new SharedFileStore(_root), "https://litos.example.com");
        var emptyArgs = JsonSerializer.SerializeToElement(new { });

        var result = await tool.InvokeAsync(emptyArgs, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("The 'path' argument is required.", result.Text);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
