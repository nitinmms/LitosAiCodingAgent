namespace Litos.Artifacts.Tests;

public sealed class ArtifactStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ArtifactStore _store;

    public ArtifactStoreTests() => _store = new ArtifactStore(_temp.Combine("artifacts"));

    public void Dispose() => _temp.Dispose();

    private string Source(string name = "page.html") => _temp.Combine("src", name);

    private async Task<ArtifactPublishResult> PublishAsync(string html, string? sourcePath = null, string? id = null, string title = "Title") =>
        (await _store.PublishAsync(html, title, sourcePath, id, CancellationToken.None))!;

    [Fact]
    public async Task Publish_New_CreatesVersionOneWithAValidId()
    {
        var result = await PublishAsync("<p>one</p>", Source());

        Assert.True(result.Created);
        Assert.True(ArtifactStore.IsValidId(result.Meta.Id));
        Assert.Equal(1, result.Meta.CurrentVersion);
        Assert.Equal(1, result.Meta.OldestVersion);
        Assert.Equal(Source(), result.Meta.SourcePath);
        Assert.Equal(result.Meta.CreatedAt, result.Meta.UpdatedAt);
    }

    [Fact]
    public async Task Publish_SameSourcePath_AddsAVersionToTheSameArtifact()
    {
        var first = await PublishAsync("<p>one</p>", Source());
        var second = await PublishAsync("<p>two</p>", Source(), title: "Renamed");

        Assert.False(second.Created);
        Assert.Equal(first.Meta.Id, second.Meta.Id);
        Assert.Equal(2, second.Meta.CurrentVersion);
        Assert.Equal("Renamed", second.Meta.Title);
        Assert.Equal(first.Meta.CreatedAt, second.Meta.CreatedAt);
        Assert.Single(await _store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Publish_DifferentSourcePath_CreatesASeparateArtifact()
    {
        var first = await PublishAsync("<p>a</p>", Source("a.html"));
        var second = await PublishAsync("<p>b</p>", Source("b.html"));

        Assert.True(second.Created);
        Assert.NotEqual(first.Meta.Id, second.Meta.Id);
    }

    [Fact]
    public async Task Publish_SourcePathDifferingOnlyInCase_IsTheSameArtifactOnWindowsOnly()
    {
        var first = await PublishAsync("<p>a</p>", Source("page.html"));
        var second = await PublishAsync("<p>b</p>", Source("PAGE.html"));

        Assert.Equal(OperatingSystem.IsWindows(), first.Meta.Id == second.Meta.Id);
    }

    [Fact]
    public async Task Publish_ById_AddsAVersionAndAdoptsTheNewSourcePath()
    {
        var first = await PublishAsync("<p>a</p>", Source("a.html"));
        var second = await PublishAsync("<p>b</p>", Source("b.html"), id: first.Meta.Id);

        Assert.False(second.Created);
        Assert.Equal(first.Meta.Id, second.Meta.Id);
        Assert.Equal(2, second.Meta.CurrentVersion);
        Assert.Equal(Source("b.html"), second.Meta.SourcePath);
    }

    [Fact]
    public async Task Publish_ByIdWithoutSourcePath_KeepsTheOriginalSourcePath()
    {
        var first = await PublishAsync("<p>a</p>", Source());
        var second = await PublishAsync("<p>b</p>", sourcePath: null, id: first.Meta.Id);

        Assert.Equal(Source(), second.Meta.SourcePath);
    }

    [Theory]
    [InlineData("0123456789abcdef")] // well-formed but unknown
    [InlineData("not-an-id")]
    [InlineData("../../etc")]
    public async Task Publish_UnknownOrMalformedId_ReturnsNullAndCreatesNothing(string id)
    {
        var result = await _store.PublishAsync("<p>x</p>", "T", Source(), id, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(await _store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryGet_LatestAndSpecificVersions_ReturnTheirOwnContent()
    {
        var id = (await PublishAsync("<p>one</p>", Source())).Meta.Id;
        await PublishAsync("<p>two</p>", Source());

        var latest = await _store.TryGetAsync(id, version: null, CancellationToken.None);
        var first = await _store.TryGetAsync(id, version: 1, CancellationToken.None);

        Assert.Equal("<p>two</p>", File.ReadAllText(latest!.Value.FilePath));
        Assert.Equal("<p>one</p>", File.ReadAllText(first!.Value.FilePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public async Task TryGet_VersionOutOfRange_ReturnsNull(int version)
    {
        var id = (await PublishAsync("<p>one</p>", Source())).Meta.Id;
        await PublishAsync("<p>two</p>", Source());

        Assert.Null(await _store.TryGetAsync(id, version, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF0123456789")] // upper case is never minted
    [InlineData("0123456789abcde")] // 15 chars
    [InlineData("..")]
    [InlineData("0123456789abcdef0")] // 17 chars
    public async Task TryGet_MalformedId_ReturnsNull(string id) =>
        Assert.Null(await _store.TryGetAsync(id, null, CancellationToken.None));

    [Fact]
    public async Task Publish_PastTheRetentionLimit_PrunesTheOldestVersions()
    {
        var extra = 3;
        string id = "";
        for (var i = 1; i <= ArtifactStore.MaxRetainedVersions + extra; i++)
            id = (await PublishAsync($"<p>{i}</p>", Source())).Meta.Id;

        var meta = await _store.GetAsync(id, CancellationToken.None);
        Assert.Equal(ArtifactStore.MaxRetainedVersions + extra, meta!.CurrentVersion);
        Assert.Equal(extra + 1, meta.OldestVersion);
        Assert.Null(await _store.TryGetAsync(id, extra, CancellationToken.None));
        Assert.NotNull(await _store.TryGetAsync(id, extra + 1, CancellationToken.None));
        Assert.Equal(
            ArtifactStore.MaxRetainedVersions,
            Directory.GetFiles(_temp.Combine("artifacts", id), "v*.html").Length);
    }

    [Fact]
    public async Task List_OrdersMostRecentlyUpdatedFirst()
    {
        var a = (await PublishAsync("<p>a</p>", Source("a.html"))).Meta.Id;
        var b = (await PublishAsync("<p>b</p>", Source("b.html"))).Meta.Id;
        await Task.Delay(20);
        await PublishAsync("<p>a2</p>", Source("a.html"));

        var ids = (await _store.ListAsync(CancellationToken.None)).Select(m => m.Id).ToList();

        Assert.Equal([a, b], ids);
    }

    [Fact]
    public async Task List_SkipsCorruptOrForeignFolders()
    {
        var good = (await PublishAsync("<p>a</p>", Source())).Meta.Id;
        Directory.CreateDirectory(_temp.Combine("artifacts", "not-an-artifact"));
        var corrupt = _temp.Combine("artifacts", "aaaaaaaaaaaaaaaa");
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "meta.json"), "{ not json");

        var ids = (await _store.ListAsync(CancellationToken.None)).Select(m => m.Id);

        Assert.Equal([good], ids);
    }

    [Fact]
    public async Task Get_MetaWhoseIdDisagreesWithItsFolder_IsIgnored()
    {
        var real = (await PublishAsync("<p>a</p>", Source())).Meta.Id;
        var impostor = _temp.Combine("artifacts", "bbbbbbbbbbbbbbbb");
        Directory.CreateDirectory(impostor);
        File.Copy(_temp.Combine("artifacts", real, "meta.json"), Path.Combine(impostor, "meta.json"));

        Assert.Null(await _store.GetAsync("bbbbbbbbbbbbbbbb", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesEveryVersion_AndReportsWhetherAnythingWasDeleted()
    {
        var id = (await PublishAsync("<p>a</p>", Source())).Meta.Id;
        await PublishAsync("<p>b</p>", Source());

        Assert.Equal(id, (await _store.DeleteAsync(id, CancellationToken.None))?.Id);
        Assert.Null(await _store.DeleteAsync(id, CancellationToken.None));
        Assert.Null(await _store.TryGetAsync(id, null, CancellationToken.None));
        Assert.False(Directory.Exists(_temp.Combine("artifacts", id)));
    }

    [Fact]
    public async Task Delete_MalformedId_ReturnsFalseWithoutTouchingTheFilesystem()
    {
        Directory.CreateDirectory(_temp.Combine("artifacts"));
        var sibling = _temp.Combine("keep-me");
        Directory.CreateDirectory(sibling);

        Assert.Null(await _store.DeleteAsync("..", CancellationToken.None));
        Assert.True(Directory.Exists(sibling));
    }

    [Fact]
    public async Task Meta_SurvivesAFreshStoreInstance_SoAnotherHostProcessSeesTheSameArtifact()
    {
        var first = await PublishAsync("<p>a</p>", Source());

        var otherProcess = new ArtifactStore(_temp.Combine("artifacts"));
        var republished = await otherProcess.PublishAsync("<p>b</p>", "T", Source(), null, CancellationToken.None);

        Assert.Equal(first.Meta.Id, republished!.Meta.Id);
        Assert.Equal(2, republished.Meta.CurrentVersion);
    }

    [Fact]
    public async Task Publish_LeavesNoTempFilesBehind()
    {
        var id = (await PublishAsync("<p>a</p>", Source())).Meta.Id;

        Assert.Empty(Directory.GetFiles(_temp.Combine("artifacts", id), "*.tmp"));
    }

    [Fact]
    public async Task Publish_ConcurrentRepublishesOfOneFile_EachGetADistinctVersion()
    {
        await PublishAsync("<p>0</p>", Source());

        var results = await Task.WhenAll(Enumerable.Range(1, 10).Select(i => PublishAsync($"<p>{i}</p>", Source())));

        Assert.Equal(Enumerable.Range(2, 10), results.Select(r => r.Meta.CurrentVersion).Order());
        Assert.Single(results.Select(r => r.Meta.Id).Distinct());
    }

    [Fact]
    public async Task Publish_WhileAReaderHoldsMetaJsonOpenBriefly_StillSucceeds()
    {
        var id = (await PublishAsync("<p>a</p>", Source())).Meta.Id;
        var reader = new FileStream(_temp.Combine("artifacts", id, "meta.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Delay(100).ContinueWith(_ => reader.Dispose());

        var second = await PublishAsync("<p>b</p>", Source());
        await release;

        Assert.Equal(2, second.Meta.CurrentVersion);
        Assert.Equal(2, (await _store.GetAsync(id, CancellationToken.None))!.CurrentVersion);
    }

    [Fact]
    public async Task List_EmptyWhenTheRootDoesNotExistYet() =>
        Assert.Empty(await new ArtifactStore(_temp.Combine("never-created")).ListAsync(CancellationToken.None));
}
