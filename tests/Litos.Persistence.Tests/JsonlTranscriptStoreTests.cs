using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Tools;

namespace Litos.Persistence.Tests;

/// <summary>
/// Exercises JsonlTranscriptStore against real files in a per-test temp directory (constructor's
/// rootDirectory override exists for exactly this). Covers the round trip AppendAsync/ReadAsync
/// depends on for every other Litos face, plus BranchAsync — including the safe-cut-point
/// snapping added alongside the GUI's /branch picker, which CompactionPlannerTests already covers
/// at the pure-logic level; these tests confirm the store actually applies it when writing real
/// JSONL files, not just that the shared helper computes the right index.
/// </summary>
public sealed class JsonlTranscriptStoreTests : IDisposable
{
    private readonly string _root;
    private readonly JsonlTranscriptStore _store;

    public JsonlTranscriptStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "litos-persistence-tests-" + Guid.NewGuid().ToString("n"));
        _store = new JsonlTranscriptStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static TranscriptEntry EntryFor(ChatMessage message) =>
        TranscriptEntry.FromMessage(message);

    [Fact]
    public async Task AppendAsync_ThenReadAsync_RoundTripsEntriesInOrder()
    {
        await _store.AppendAsync(SessionOwner.Local, "s1", EntryFor(ChatMessage.User("hello")), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "s1", EntryFor(ChatMessage.Assistant([new TextBlock("hi there")])), CancellationToken.None);

        var entries = await ReadAllAsync("s1");

        Assert.Equal(2, entries.Count);
        Assert.Equal("hello", ((TextBlock)entries[0].Message!.Content[0]).Text);
        Assert.Equal("hi there", ((TextBlock)entries[1].Message!.Content[0]).Text);
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_ForSessionThatWasNeverCreated()
    {
        var entries = await ReadAllAsync("never-created");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task ListSessionsAsync_ReturnsPreviewAndCounts_NewestFirst()
    {
        await _store.AppendAsync(SessionOwner.Local, "older", EntryFor(ChatMessage.User("first session")), CancellationToken.None);
        await Task.Delay(10); // ensure LastUpdatedAt ordering is distinguishable
        await _store.AppendAsync(SessionOwner.Local, "newer", EntryFor(ChatMessage.User("second session")), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "newer", EntryFor(ChatMessage.Assistant([new TextBlock("reply")])), CancellationToken.None);

        var summaries = await _store.ListSessionsAsync(SessionOwner.Local, CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("newer", summaries[0].SessionId);
        Assert.Equal("second session", summaries[0].FirstUserMessagePreview);
        Assert.Equal(2, summaries[0].MessageCount);
        Assert.Equal("older", summaries[1].SessionId);
    }

    [Fact]
    public async Task ListSessionsAsync_FirstUserMessagePreview_SkipsToolResultWrappedAsRoleUser()
    {
        // ChatMessage.ToolResult wraps its result as Role.User too, but it's not something the
        // user typed — the preview must skip past it to the genuine first user message.
        await _store.AppendAsync(SessionOwner.Local, "s1", EntryFor(ChatMessage.ToolResult("call_1", new ToolResult("tool output"))), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "s1", EntryFor(ChatMessage.User("actual first message")), CancellationToken.None);

        var summaries = await _store.ListSessionsAsync(SessionOwner.Local, CancellationToken.None);

        Assert.Equal("actual first message", summaries.Single().FirstUserMessagePreview);
    }

    [Fact]
    public async Task BranchAsync_CopiesOnlyTheFirstNEntries_LeavingSourceUntouched()
    {
        await _store.AppendAsync(SessionOwner.Local, "source", EntryFor(ChatMessage.User("first")), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "source", EntryFor(ChatMessage.Assistant([new TextBlock("reply")])), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "source", EntryFor(ChatMessage.User("second")), CancellationToken.None);

        var branchedId = await _store.BranchAsync(SessionOwner.Local, "source", uptoEntryIndex: 2, CancellationToken.None);

        var branched = await ReadAllAsync(branchedId);
        Assert.Equal(2, branched.Count);
        Assert.Equal("first", ((TextBlock)branched[0].Message!.Content[0]).Text);

        var source = await ReadAllAsync("source");
        Assert.Equal(3, source.Count); // untouched
    }

    [Fact]
    public async Task BranchAsync_SnapsBackward_RatherThanOrphaningAToolUse()
    {
        await _store.AppendAsync(SessionOwner.Local, "source", EntryFor(ChatMessage.User("read the file")), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "source",
            EntryFor(ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", System.Text.Json.JsonDocument.Parse("{}").RootElement)])),
            CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "source", EntryFor(ChatMessage.ToolResult("call_1", new ToolResult("contents"))), CancellationToken.None);

        // Requesting index 2 would keep [user, assistant tool_use] but drop the tool_result —
        // must snap back to 1 (right before the tool_use) instead of writing a broken transcript.
        var branchedId = await _store.BranchAsync(SessionOwner.Local, "source", uptoEntryIndex: 2, CancellationToken.None);

        var branched = await ReadAllAsync(branchedId);
        Assert.Single(branched);
        Assert.Equal("read the file", ((TextBlock)branched[0].Message!.Content[0]).Text);
    }

    [Fact]
    public async Task BranchAsync_ToleratesAMalformedLine_EvenPastTheEventualCutPoint()
    {
        // Before the safe-cut-point snap was added, BranchAsync never parsed JSON at all (a
        // straight text Take), so a corrupt line anywhere in the file — including past whatever
        // was kept — was harmless. Finding a safe cut point now means inspecting every line, so a
        // malformed line elsewhere in the file must degrade gracefully rather than throwing and
        // failing a branch request that has nothing to do with the corruption.
        await _store.AppendAsync(SessionOwner.Local, "source", EntryFor(ChatMessage.User("first")), CancellationToken.None);
        await _store.AppendAsync(SessionOwner.Local, "source", EntryFor(ChatMessage.Assistant([new TextBlock("reply")])), CancellationToken.None);
        await File.AppendAllTextAsync(SessionFilePath("source"), "{not valid json" + Environment.NewLine, CancellationToken.None);

        var branchedId = await _store.BranchAsync(SessionOwner.Local, "source", uptoEntryIndex: 2, CancellationToken.None);

        var branched = await ReadAllAsync(branchedId);
        Assert.Equal(2, branched.Count);
        Assert.Equal("first", ((TextBlock)branched[0].Message!.Content[0]).Text);
    }

    private string SessionFilePath(string sessionId) => Path.Combine(_root, "local", sessionId + ".jsonl");

    [Fact]
    public async Task BranchAsync_ReturnsNewSessionId_DifferentFromSource()
    {
        await _store.AppendAsync(SessionOwner.Local, "source", EntryFor(ChatMessage.User("hi")), CancellationToken.None);

        var branchedId = await _store.BranchAsync(SessionOwner.Local, "source", uptoEntryIndex: 1, CancellationToken.None);

        Assert.NotEqual("source", branchedId);
    }

    [Fact]
    public async Task BranchAsync_Throws_WhenSourceSessionDoesNotExist()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _store.BranchAsync(SessionOwner.Local, "does-not-exist", uptoEntryIndex: 1, CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task AppendAsync_RejectsSessionIdsThatCouldEscapeTheOwnerDirectory(string unsafeSessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AppendAsync(SessionOwner.Local, unsafeSessionId, EntryFor(ChatMessage.User("hi")), CancellationToken.None));
    }

    private async Task<List<TranscriptEntry>> ReadAllAsync(string sessionId)
    {
        var entries = new List<TranscriptEntry>();
        await foreach (var entry in _store.ReadAsync(SessionOwner.Local, sessionId, CancellationToken.None))
            entries.Add(entry);
        return entries;
    }
}
