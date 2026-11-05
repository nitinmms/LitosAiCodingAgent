using System.Text.Json;
using System.Text.RegularExpressions;

namespace Litos.Artifacts.Tests;

public sealed class ArtifactToolTests : IDisposable
{
    private const string BaseUrl = "http://127.0.0.1:54321";

    private readonly TempDirectory _temp = new();
    private readonly ArtifactStore _store;
    private readonly ArtifactTool _tool;
    private string _currentBaseUrl = BaseUrl;

    public ArtifactToolTests()
    {
        _store = new ArtifactStore(_temp.Combine("artifacts"));
        _tool = new ArtifactTool(_store, () => _currentBaseUrl);
    }

    public void Dispose() => _temp.Dispose();

    private Task<Litos.Agent.Tools.ToolResult> Invoke(object args) =>
        _tool.InvokeAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    private static string IdIn(string resultText) => Regex.Match(resultText, @"id ([0-9a-f]{16})").Groups[1].Value;

    private string Page(string title = "Benchmark Dashboard", string name = "page.html", string body = "<p>hi</p>") =>
        _temp.WriteFile(name, $"<!doctype html><html><head><title>{title}</title></head><body>{body}</body></html>");

    [Fact]
    public void Schema_DeclaresEveryActionAndArgument()
    {
        Assert.Equal("artifact", _tool.Name);
        var properties = _tool.ParameterSchema.GetProperty("properties");
        Assert.Equal(
            ["publish", "read", "list", "delete"],
            properties.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        foreach (var name in new[] { "file_path", "title", "id", "version" })
            Assert.True(properties.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void Description_TellsTheModelToLoadTheDesignSkill() =>
        Assert.Contains("artifact-design", _tool.Description);

    [Fact]
    public async Task Publish_New_ReturnsTheUrlOnItsOwnLine()
    {
        var result = await Invoke(new { action = "publish", file_path = Page() });

        Assert.False(result.IsError, result.Text);
        var lines = result.Text.Split('\n');
        Assert.StartsWith("Published artifact \"Benchmark Dashboard\" (id ", lines[0]);
        Assert.EndsWith(", version 1, new).", lines[0]);
        Assert.Equal($"Open: {BaseUrl}/artifacts/{IdIn(result.Text)}", lines[1]);
    }

    [Fact]
    public async Task Publish_ActionDefaultsToPublish()
    {
        var result = await Invoke(new { file_path = Page() });

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Published artifact", result.Text);
    }

    [Fact]
    public async Task Publish_SameFileAgain_IsAnUpdateAtTheSameUrl()
    {
        var path = Page();
        var first = await Invoke(new { action = "publish", file_path = path });
        File.WriteAllText(path, "<title>Benchmark Dashboard</title><p>v2</p>");
        var second = await Invoke(new { action = "publish", file_path = path });

        Assert.Equal(IdIn(first.Text), IdIn(second.Text));
        Assert.Contains("version 2, updated", second.Text);
        Assert.Contains("refreshes their browser tab", second.Text);
    }

    [Fact]
    public async Task Publish_WithId_UpdatesThatArtifactFromAnotherFile()
    {
        var first = await Invoke(new { action = "publish", file_path = Page(name: "a.html") });
        var second = await Invoke(new { action = "publish", file_path = Page(name: "b.html"), id = IdIn(first.Text) });

        Assert.Equal(IdIn(first.Text), IdIn(second.Text));
        Assert.Contains("version 2", second.Text);
    }

    [Fact]
    public async Task Publish_UnknownId_IsAnError()
    {
        var result = await Invoke(new { action = "publish", file_path = Page(), id = "0123456789abcdef" });

        Assert.True(result.IsError);
        Assert.Contains("No artifact with id", result.Text);
        Assert.Empty(await _store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Publish_ReadsTheBaseUrlAtCallTime()
    {
        _currentBaseUrl = "http://127.0.0.1:60000/";

        var result = await Invoke(new { file_path = Page() });

        Assert.Contains("Open: http://127.0.0.1:60000/artifacts/", result.Text);
    }

    [Fact]
    public async Task Publish_TitlePrecedence_ArgumentThenTitleTagThenFileName()
    {
        var withArgument = await Invoke(new { file_path = Page(name: "a.html"), title = "  Chosen\n  Name " });
        var fromTag = await Invoke(new { file_path = Page(title: "Fish &amp; Chips", name: "b.html") });
        var noTag = await Invoke(new { file_path = _temp.WriteFile("plain-report.html", "<p>no title</p>") });

        Assert.Contains("\"Chosen Name\"", withArgument.Text);
        Assert.Contains("\"Fish & Chips\"", fromTag.Text);
        Assert.Contains("\"plain-report\"", noTag.Text);
    }

    [Fact]
    public async Task Publish_LongTitle_IsTruncated()
    {
        var result = await Invoke(new { file_path = Page(), title = new string('x', 500) });

        var meta = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal(200, meta.Title.Length);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Publish_MissingFilePath_IsAnError()
    {
        var result = await Invoke(new { action = "publish" });

        Assert.True(result.IsError);
        Assert.Contains("file_path", result.Text);
    }

    [Fact]
    public async Task Publish_RelativePath_IsAnError()
    {
        var result = await Invoke(new { file_path = "page.html" });

        Assert.True(result.IsError);
        Assert.Contains("absolute", result.Text);
    }

    [Fact]
    public async Task Publish_NonHtmlFile_IsAnError()
    {
        var result = await Invoke(new { file_path = _temp.WriteFile("notes.md", "# hi") });

        Assert.True(result.IsError);
        Assert.Contains(".html", result.Text);
    }

    [Fact]
    public async Task Publish_MissingFile_IsAnError()
    {
        var result = await Invoke(new { file_path = _temp.Combine("missing.html") });

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Text);
    }

    [Fact]
    public async Task Publish_OversizedFile_IsAnError()
    {
        var path = _temp.Combine("huge.html");
        using (var stream = new FileStream(path, FileMode.Create))
            stream.SetLength(ArtifactTool.MaxHtmlBytes + 1);

        var result = await Invoke(new { file_path = path });

        Assert.True(result.IsError);
        Assert.Contains("10MB", result.Text);
    }

    [Fact]
    public async Task Publish_HtmExtensionIsAccepted()
    {
        var result = await Invoke(new { file_path = _temp.WriteFile("page.HTM", "<title>Old School</title>") });

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task Read_ReturnsTheLatestOrARequestedVersion()
    {
        var path = Page(body: "<p>first</p>");
        var id = IdIn((await Invoke(new { file_path = path })).Text);
        File.WriteAllText(path, "<p>second</p>");
        await Invoke(new { file_path = path });

        var latest = await Invoke(new { action = "read", id });
        var first = await Invoke(new { action = "read", id, version = 1 });

        Assert.Contains("version 2 of 2", latest.Text);
        Assert.Contains("<p>second</p>", latest.Text);
        Assert.Contains("version 1 of 2", first.Text);
        Assert.Contains("<p>first</p>", first.Text);
    }

    [Fact]
    public async Task Read_LargePage_IsTruncated()
    {
        var id = IdIn((await Invoke(new { file_path = Page(body: new string('x', ArtifactTool.MaxReadChars + 500)) })).Text);

        var result = await Invoke(new { action = "read", id });

        Assert.False(result.IsError);
        Assert.Contains("[truncated: showing 100,000 of", result.Text);
    }

    [Fact]
    public async Task Read_Errors_ForMissingIdUnknownIdAndMissingVersion()
    {
        var id = IdIn((await Invoke(new { file_path = Page() })).Text);

        Assert.True((await Invoke(new { action = "read" })).IsError);
        Assert.True((await Invoke(new { action = "read", id = "0123456789abcdef" })).IsError);
        var missingVersion = await Invoke(new { action = "read", id, version = 9 });
        Assert.True(missingVersion.IsError);
        Assert.Contains("no stored version 9", missingVersion.Text);
    }

    [Fact]
    public async Task List_Empty_SaysSo() =>
        Assert.Equal("No artifacts published yet.", (await Invoke(new { action = "list" })).Text);

    [Fact]
    public async Task List_ShowsEachArtifactWithItsUrlAtTheEndOfTheLine()
    {
        var a = IdIn((await Invoke(new { file_path = Page(title: "Alpha", name: "a.html") })).Text);
        var b = IdIn((await Invoke(new { file_path = Page(title: "Beta", name: "b.html") })).Text);

        var lines = (await Invoke(new { action = "list" })).Text.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.Contains("\"Alpha\"") && l.EndsWith($"{BaseUrl}/artifacts/{a}"));
        Assert.Contains(lines, l => l.Contains("\"Beta\"") && l.EndsWith($"{BaseUrl}/artifacts/{b}"));
    }

    [Fact]
    public async Task Delete_RemovesTheArtifactButNotTheSourceFile()
    {
        var path = Page();
        var id = IdIn((await Invoke(new { file_path = path })).Text);

        var result = await Invoke(new { action = "delete", id });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Deleted artifact \"Benchmark Dashboard\"", result.Text);
        Assert.True(File.Exists(path));
        Assert.Empty(await _store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Delete_Errors_ForMissingAndUnknownIds()
    {
        Assert.True((await Invoke(new { action = "delete" })).IsError);
        Assert.True((await Invoke(new { action = "delete", id = "0123456789abcdef" })).IsError);
    }

    [Fact]
    public async Task UnknownAction_IsAnError()
    {
        var result = await Invoke(new { action = "open" });

        Assert.True(result.IsError);
        Assert.Contains("Unknown action 'open'", result.Text);
    }
}
