using System.Net;
using Litos.Artifacts;
using Litos.Gui;
using Litos.Hosting.Files;
using Litos.Agent.Session;

namespace Litos.Gui.Tests;

/// <summary>
/// The in-process server behind the artifact and share_file links: proves both routes are mapped
/// and serve from the same store instances the tools write to, over a real loopback socket.
/// </summary>
public sealed class GuiLoopbackServerTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("litos-gui-loopback-").FullName;
    private readonly GuiLoopbackServer _server = new();
    private readonly HttpClient _http = new();
    private ArtifactStore _artifacts = null!;
    private SharedFileStore _sharedFiles = null!;

    public Task InitializeAsync()
    {
        _artifacts = new ArtifactStore(Path.Combine(_root, "artifacts"));
        _sharedFiles = new SharedFileStore(Path.Combine(_root, "shared"));
        _server.Start(_artifacts, _sharedFiles);
        return Task.CompletedTask;
    }

    [Fact]
    public void Start_BindsALoopbackPort()
    {
        Assert.Null(_server.StartError);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+$", _server.BaseUrl);
    }

    [Fact]
    public async Task ServesAPublishedArtifact_WithTheSandboxPolicy()
    {
        var published = await _artifacts.PublishAsync("<html><body>hi</body></html>", "Hi", sourcePath: null, id: null, CancellationToken.None);

        using var response = await _http.GetAsync($"{_server.BaseUrl}/artifacts/{published!.Meta.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hi", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task ServesASharedFile()
    {
        var source = Path.Combine(_root, "report.txt");
        await File.WriteAllTextAsync(source, "shared!");
        var token = await _sharedFiles.ShareAsync(SessionOwner.Local, source, CancellationToken.None);

        using var response = await _http.GetAsync($"{_server.BaseUrl}/files/{token.Token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("shared!", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownArtifact_Is404()
    {
        using var response = await _http.GetAsync($"{_server.BaseUrl}/artifacts/0123456789abcdef");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        Directory.Delete(_root, recursive: true);
    }
}
