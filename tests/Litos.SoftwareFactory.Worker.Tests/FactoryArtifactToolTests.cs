using System.Net;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>The chat turn's artifact tool (ReadMe_ArtifactsFeature.md §11.3), against a fake host.</summary>
public class FactoryArtifactToolTests
{
    private static readonly Guid PageId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static (FactoryArtifactTool Tool, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new FactoryArtifactTool(TestOptions.HostClient(handler)), handler);
    }

    private static Task<Litos.Agent.Tools.ToolResult> Invoke(FactoryArtifactTool tool, object args) =>
        tool.InvokeAsync(JsonSerializer.SerializeToElement(args), default);

    [Fact]
    public void Schema_DeclaresEveryAction_AndTheDescriptionSaysToFetchTheGuideFirst()
    {
        var (tool, _) = Create();

        Assert.Equal("artifact", tool.Name);
        Assert.Equal(
            ["guide", "publish", "read", "list"],
            tool.ParameterSchema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("\"guide\" once first", tool.Description);
        Assert.Contains("card under your answer", tool.Description);
    }

    [Fact]
    public async Task Guide_StatesTheFactoryWorkflow_ThenTheSharedDesignRules_WithoutTheFileWorkflow()
    {
        var (tool, handler) = Create();

        var result = await Invoke(tool, new { action = "guide" });

        Assert.False(result.IsError);
        Assert.StartsWith("# Pages in the factory", result.Text);
        Assert.Contains("there is no file to write", result.Text);
        Assert.Contains("await artifact(\"action\", \"publish\", \"html\", page, \"title\", \"Export Pipeline\")", result.Text);
        Assert.Contains("## Where the page runs (hard limits)", result.Text);
        Assert.Contains("prefers-color-scheme: dark", result.Text);
        Assert.DoesNotContain("write_file", result.Text);
        Assert.DoesNotContain("name: artifact-design", result.Text);
        Assert.Empty(handler.Requests); // the guide is the worker's own; the host isn't asked
    }

    [Fact]
    public async Task Publish_PostsThePageWhole_ToTheRunsArtifactsPath_AndSaysWhatWasStored()
    {
        var (tool, handler) = Create();
        handler.EnqueueJson(new ArtifactPublishResponse(true, null, new ArtifactSummary(PageId, "Export Pipeline", 1), Created: true));

        var result = await Invoke(tool, new { action = "publish", html = "<title>Export Pipeline</title>", title = "Pipeline" });

        Assert.False(result.IsError, result.Text);
        Assert.Equal($"Published \"Export Pipeline\" (id {PageId}, version 1, new). It appears as a card under your answer.", result.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "/internal/runs/run-1/artifacts"), (request.Method, request.Uri.AbsolutePath));
        Assert.Equal(TestOptions.Secret, request.Headers[FactoryWire.SecretHeader]);
        var sent = JsonSerializer.Deserialize<ArtifactPublishRequest>(request.Body!, FactoryWire.Json)!;
        Assert.Equal(("<title>Export Pipeline</title>", "Pipeline", (Guid?)null), (sent.Html, sent.Title, sent.ArtifactId));
    }

    [Fact]
    public async Task Publish_WithAnId_AddsAVersion()
    {
        var (tool, handler) = Create();
        handler.EnqueueJson(new ArtifactPublishResponse(true, null, new ArtifactSummary(PageId, "Export Pipeline", 2), Created: false));

        var result = await Invoke(tool, new { action = "publish", html = "<p>v2</p>", id = PageId.ToString() });

        Assert.Contains("version 2, updated", result.Text);
        Assert.Equal(PageId, JsonSerializer.Deserialize<ArtifactPublishRequest>(handler.Requests[0].Body!, FactoryWire.Json)!.ArtifactId);
    }

    [Fact]
    public async Task Publish_RefusedByTheHost_IsAnError_WithTheHostsReason()
    {
        var (tool, handler) = Create();
        handler.EnqueueJson(ArtifactPublishResponse.Refused("The page is 2048 KB, over the 1024 KB limit for one page."));

        var result = await Invoke(tool, new { action = "publish", html = "<p>x</p>" });

        Assert.True(result.IsError);
        Assert.Contains("over the 1024 KB limit", result.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Publish_WithoutHtml_IsAnError_AndAsksTheHostNothing(string? html)
    {
        var (tool, handler) = Create();

        var result = await Invoke(tool, new { action = "publish", html });

        Assert.True(result.IsError);
        Assert.Contains("'html' argument is required", result.Text);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Publish_WithAnIdThatIsNotOne_IsAnError_AndAsksTheHostNothing()
    {
        var (tool, handler) = Create();

        var result = await Invoke(tool, new { action = "publish", html = "<p>x</p>", id = "page-1" });

        Assert.True(result.IsError);
        Assert.Contains("is not a page id", result.Text);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Read_ReturnsTheVersionAsked_FromTheRunsArtifactPath()
    {
        var (tool, handler) = Create();
        handler.EnqueueJson(new ArtifactReadResponse(new ArtifactSummary(PageId, "Export Pipeline", 3), 2, "<p>v2</p>"));

        var result = await Invoke(tool, new { action = "read", id = PageId.ToString(), version = 2 });

        Assert.Equal($"\"Export Pipeline\" (id {PageId}), version 2 of 3:\n<p>v2</p>", result.Text);
        Assert.Equal($"/internal/runs/run-1/artifacts/{PageId:N}?version=2", handler.Requests[0].Uri.PathAndQuery);
    }

    [Fact]
    public async Task Read_AnUnknownPageOrVersion_IsAnError()
    {
        var (tool, handler) = Create();
        handler.EnqueueJson(new ArtifactReadResponse(null, 0, null));
        handler.EnqueueJson(new ArtifactReadResponse(new ArtifactSummary(PageId, "Export Pipeline", 3), 9, null));

        var unknown = await Invoke(tool, new { action = "read", id = PageId.ToString() });
        var noVersion = await Invoke(tool, new { action = "read", id = PageId.ToString(), version = 9 });
        var noId = await Invoke(tool, new { action = "read" });

        Assert.True(unknown.IsError);
        Assert.Contains("no page with id", unknown.Text);
        Assert.True(noVersion.IsError);
        Assert.Contains("has no version 9; its latest is 3", noVersion.Text);
        Assert.True(noId.IsError);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task List_ShowsTheThreadsPages_OrSaysThereAreNone()
    {
        var (tool, handler) = Create();
        handler.EnqueueJson(new List<ArtifactSummary>());
        handler.EnqueueJson(new List<ArtifactSummary> { new(PageId, "Export Pipeline", 2) });

        var none = await Invoke(tool, new { action = "list" });
        var one = await Invoke(tool, new { action = "list" });

        Assert.Equal("No pages have been published on this thread yet.", none.Text);
        Assert.Equal($"- \"Export Pipeline\" id {PageId}, version 2", one.Text);
        Assert.Equal((HttpMethod.Get, "/internal/runs/run-1/artifacts"), (handler.Requests[0].Method, handler.Requests[0].Uri.AbsolutePath));
    }

    [Fact]
    public async Task AHostThatCannotBeReached_IsAnError_NotAFailedTurn()
    {
        var (tool, handler) = Create();
        handler.EnqueueFailure();
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var unreachable = await Invoke(tool, new { action = "list" });
        var refused = await Invoke(tool, new { action = "publish", html = "<p>x</p>" });

        Assert.True(unreachable.IsError);
        Assert.Contains("could not be reached", unreachable.Text);
        Assert.True(refused.IsError);
    }

    [Theory]
    [InlineData(null, "'action' argument is required")]
    [InlineData("open", "Unknown action 'open'")]
    public async Task AMissingOrUnknownAction_IsAnError(string? action, string expected)
    {
        var (tool, _) = Create();

        var result = await Invoke(tool, new { action });

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
    }
}
