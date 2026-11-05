using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.Artifacts.Tests;

/// <summary>
/// Runs the routes on a real Kestrel listener on loopback port 0 — the same way Litos.VsCodeHost
/// hosts them — because what matters here is exactly what goes over the wire: the headers are
/// the security boundary.
/// </summary>
public sealed class ArtifactEndpointsTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private ArtifactStore _store = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _store = new ArtifactStore(_temp.Combine("artifacts"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_store);
        _app = builder.Build();
        _app.MapArtifactEndpoints();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _temp.Dispose();
    }

    private async Task<string> PublishAsync(string html, string source = "page.html") =>
        (await _store.PublishAsync(html, "T", _temp.Combine(source), null, CancellationToken.None))!.Meta.Id;

    [Fact]
    public async Task Get_ServesTheLatestVersionInlineAsHtml_WithEverySecurityHeader()
    {
        await PublishAsync("<p>one</p>");
        var id = await PublishAsync("<p>two</p>");

        using var response = await _client.GetAsync($"/artifacts/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<p>two</p>", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal(ArtifactContentPolicy.ContentSecurityPolicy, string.Join(", ", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Get_SpecificVersion_ServesThatVersion()
    {
        var id = await PublishAsync("<p>one</p>");
        await PublishAsync("<p>two</p>");

        using var response = await _client.GetAsync($"/artifacts/{id}/v/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<p>one</p>", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("/artifacts/0123456789abcdef")] // well-formed, unknown
    [InlineData("/artifacts/not-an-id")]
    [InlineData("/artifacts/%2e%2e%2fmeta.json")]
    [InlineData("/artifacts/{id}/v/9")] // version never published
    [InlineData("/artifacts/{id}/v/0")]
    [InlineData("/artifacts/{id}/v/abc")] // route constraint rejects
    [InlineData("/artifacts/{id}/meta.json")] // nothing else in the folder is reachable
    public async Task Get_UnknownOrMalformed_Is404(string pathTemplate)
    {
        var id = await PublishAsync("<p>one</p>");

        using var response = await _client.GetAsync(pathTemplate.Replace("{id}", id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_PrunedVersion_Is404()
    {
        string id = "";
        for (var i = 0; i <= ArtifactStore.MaxRetainedVersions; i++)
            id = await PublishAsync($"<p>{i}</p>");

        using var response = await _client.GetAsync($"/artifacts/{id}/v/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_AfterRepublish_ServesTheNewContentAtTheSameUrl()
    {
        var id = await PublishAsync("<p>one</p>");
        using (await _client.GetAsync($"/artifacts/{id}")) { }

        await PublishAsync("<p>two</p>");
        using var response = await _client.GetAsync($"/artifacts/{id}");

        Assert.Equal("<p>two</p>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_IsNotAllowed()
    {
        var id = await PublishAsync("<p>one</p>");

        using var response = await _client.PostAsync($"/artifacts/{id}", new StringContent("x"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
