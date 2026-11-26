using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Persistence;

namespace Litos.Gui.Tests;

/// <summary>
/// Covers MainWindow.TryLoadSessionToRestoreAsync, the decision behind a plain start reopening
/// config.json's LastSessionId. Uses the real JsonlTranscriptStore over a scratch directory, since
/// "the file is gone" and "a line is damaged" are exactly the store behaviours that must not stop
/// the window opening.
/// </summary>
public sealed class MainWindowRestoreSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"litos-gui-restore-test-{Guid.NewGuid():n}");
    private readonly JsonlTranscriptStore _store;

    public MainWindowRestoreSessionTests() => _store = new JsonlTranscriptStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ReturnsNull_WhenNoSessionWasRemembered(string? sessionId)
    {
        Assert.Null(await MainWindow.TryLoadSessionToRestoreAsync(_store, sessionId, CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsNull_WhenRememberedSessionHasNoFile()
    {
        // An empty /new never writes a transcript file, so this is also "closed on an empty session".
        Assert.Null(await MainWindow.TryLoadSessionToRestoreAsync(_store, "never-written", CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsNull_WhenRememberedSessionHasOnlyAHeader()
    {
        await _store.AppendAsync(SessionOwner.Local, "header-only", TranscriptEntry.SessionHeader("/repo"), CancellationToken.None);

        Assert.Null(await MainWindow.TryLoadSessionToRestoreAsync(_store, "header-only", CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsTranscript_WhenRememberedSessionHasMessages()
    {
        await _store.AppendAsync(SessionOwner.Local, "real", TranscriptEntry.SessionHeader("/repo"), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "real", TranscriptEntry.FromMessage(ChatMessage.User("hello")), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "real", TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock("hi")])), CancellationToken.None);

        var transcript = await MainWindow.TryLoadSessionToRestoreAsync(_store, "real", CancellationToken.None);

        Assert.NotNull(transcript);
        Assert.Equal(2, transcript.Messages.Count);
        Assert.Equal("/repo", transcript.WorkingDirectory);
    }

    [Fact]
    public async Task ReturnsNull_WhenRememberedSessionFileIsDamaged()
    {
        await _store.AppendAsync(SessionOwner.Local, "damaged", TranscriptEntry.FromMessage(ChatMessage.User("hello")), CancellationToken.None);
        var file = Directory.GetFiles(_root, "transcript.jsonl", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(Path.GetDirectoryName(f)) == "damaged");
        await File.AppendAllTextAsync(file, "{ this is not json\n");

        Assert.Null(await MainWindow.TryLoadSessionToRestoreAsync(_store, "damaged", CancellationToken.None));
    }
}
