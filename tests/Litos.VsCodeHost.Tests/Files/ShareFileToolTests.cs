using System.Text.Json;
using Litos.Agent.Session;
using Litos.VsCodeHost;
using Litos.VsCodeHost.Files;

namespace Litos.VsCodeHost.Tests.Files;

/// <summary>
/// Adapted from Litos.Api.Tests/Files/ShareFileToolTests.cs. No "PUBLIC_BASE_URL not configured"
/// case here — unlike Litos.Api, this host is always loopback-only, so LoopbackBaseUrl.Value is
/// always a real, known URL by the time any tool actually runs (see Program.cs's own remarks on
/// why the DI-registration-before-StartAsync ordering is still safe).
/// </summary>
public class ShareFileToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"litos-vscodehost-shared-files-tool-test-{Guid.NewGuid():n}");
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"litos-vscodehost-test-{Guid.NewGuid():n}.txt");

    private static JsonElement Args(string path) => JsonSerializer.SerializeToElement(new { path });

    private static LoopbackBaseUrl BaseUrl(string value = "http://127.0.0.1:54321") => new() { Value = value };

    [Fact]
    public async Task InvokeAsync_FileDoesNotExist_ReturnsError()
    {
        var tool = new ShareFileTool(new SharedFileStore(_root), BaseUrl());
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
        var tool = new ShareFileTool(new SharedFileStore(_root), BaseUrl());

        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("20MB", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_Success_ReturnsAClickableUrlContainingTheHost()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var tool = new ShareFileTool(new SharedFileStore(_root), BaseUrl("http://127.0.0.1:54321"));

        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("http://127.0.0.1:54321/files/", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_Success_UrlIsActuallyDownloadable_ViaTheSameStore()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var store = new SharedFileStore(_root);
        var tool = new ShareFileTool(store, BaseUrl());

        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);
        var token = result.Text.Split("/files/")[1].Split(' ')[0];

        var resolved = await store.TryGetAsync(token, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal("hello", await File.ReadAllTextAsync(resolved!.Value.FilePath));
    }

    // Confirms the lazy-read-at-invocation design actually works: a ShareFileTool built with an
    // as-yet-unpopulated LoopbackBaseUrl (mirroring DI registration happening before
    // app.StartAsync() resolves the real port) still produces the right URL once .Value is set
    // before InvokeAsync runs — exactly the sequencing Program.cs relies on.
    [Fact]
    public async Task InvokeAsync_BaseUrlSetAfterConstruction_StillUsesTheUpdatedValue()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var baseUrl = new LoopbackBaseUrl(); // empty at construction, like before StartAsync() resolves the port
        var tool = new ShareFileTool(new SharedFileStore(_root), baseUrl);

        baseUrl.Value = "http://127.0.0.1:9999"; // populated later, like Program.cs does after StartAsync()
        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);

        Assert.Contains("http://127.0.0.1:9999/files/", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_UsesOwnerFromChannelContext()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var store = new SharedFileStore(_root);
        var tool = new ShareFileTool(store, BaseUrl());

        await ChannelContext.RunAsAsync(SessionOwner.Local, "session-1", async () =>
        {
            var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);
            Assert.False(result.IsError);
        });

        var ownerDir = Directory.EnumerateDirectories(_root).Single();
        Assert.EndsWith(SessionOwner.Local.Value, ownerDir);
    }

    [Fact]
    public async Task InvokeAsync_MissingPathArgument_ReturnsError()
    {
        // A local model's tool-call JSON omitting a required argument is a real, model-driven
        // failure mode — must degrade to a clean ToolResult.Error, not throw.
        var tool = new ShareFileTool(new SharedFileStore(_root), BaseUrl());
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
