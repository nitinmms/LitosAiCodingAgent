using Litos.Agent.Session;
using Litos.Api.Files;

namespace Litos.Api.Tests.Files;

public class SharedFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"litos-shared-files-test-{Guid.NewGuid():n}");
    private readonly string _sourceFile;

    public SharedFileStoreTests()
    {
        _sourceFile = Path.Combine(Path.GetTempPath(), $"litos-source-{Guid.NewGuid():n}.txt");
        File.WriteAllText(_sourceFile, "hello world");
    }

    [Fact]
    public async Task ShareAsync_ThenTryGetAsync_ReturnsTheCopiedFileWithOriginalName()
    {
        var store = new SharedFileStore(_root);

        var token = await store.ShareAsync(SessionOwner.Local, _sourceFile, CancellationToken.None);
        var result = await store.TryGetAsync(token.Token, CancellationToken.None);

        Assert.NotNull(result);
        var (meta, filePath) = result!.Value;
        Assert.Equal(Path.GetFileName(_sourceFile), meta.FileName);
        Assert.True(File.Exists(filePath));
        Assert.Equal("hello world", await File.ReadAllTextAsync(filePath));
        // The copy, not the original — source file staying in place shouldn't be required for the link to work.
        Assert.NotEqual(_sourceFile, filePath);
    }

    [Fact]
    public async Task ShareAsync_DoesNotDeleteOrModifyTheSourceFile()
    {
        var store = new SharedFileStore(_root);

        await store.ShareAsync(SessionOwner.Local, _sourceFile, CancellationToken.None);

        Assert.True(File.Exists(_sourceFile));
        Assert.Equal("hello world", await File.ReadAllTextAsync(_sourceFile));
    }

    [Fact]
    public async Task TryGetAsync_UnknownToken_ReturnsNull()
    {
        var store = new SharedFileStore(_root);

        var result = await store.TryGetAsync("0000000000000000", CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task TryGetAsync_MalformedToken_ReturnsNull_WithoutThrowing(string malformedToken)
    {
        var store = new SharedFileStore(_root);

        var result = await store.TryGetAsync(malformedToken, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetAsync_ExpiredToken_ReturnsNull_ButLeavesTheFileOnDisk()
    {
        var store = new SharedFileStore(_root);
        var token = await store.ShareAsync(SessionOwner.Local, _sourceFile, CancellationToken.None);

        // Backdate meta.json's ExpiresAt to simulate the 24h window having passed, without
        // waiting 24h in a test — same "manual expiry test" note the design blueprint called out.
        var metaPath = Directory.EnumerateFiles(_root, "meta.json", SearchOption.AllDirectories).Single();
        var expired = new SharedFileMeta(SessionOwner.Local.Value, Path.GetFileName(_sourceFile), "text/plain", DateTimeOffset.UtcNow.AddSeconds(-1));
        await File.WriteAllTextAsync(metaPath, System.Text.Json.JsonSerializer.Serialize(expired));

        var result = await store.TryGetAsync(token.Token, CancellationToken.None);

        Assert.Null(result);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(metaPath)!, Path.GetFileName(_sourceFile))));
    }

    [Fact]
    public async Task ShareAsync_TwoDifferentOwners_TokensDoNotCollideOrLeakBetweenOwners()
    {
        var store = new SharedFileStore(_root);

        var localToken = await store.ShareAsync(SessionOwner.Local, _sourceFile, CancellationToken.None);
        var telegramToken = await store.ShareAsync(SessionOwner.Telegram, _sourceFile, CancellationToken.None);

        Assert.NotEqual(localToken.Token, telegramToken.Token);
        Assert.NotNull(await store.TryGetAsync(localToken.Token, CancellationToken.None));
        Assert.NotNull(await store.TryGetAsync(telegramToken.Token, CancellationToken.None));
    }

    public void Dispose()
    {
        if (File.Exists(_sourceFile))
            File.Delete(_sourceFile);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
