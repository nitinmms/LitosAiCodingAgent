using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tests.Fakes;

namespace Litos.Agent.Tests.Session;

public class TranscriptTests
{
    [Fact]
    public void CreateNew_SetsWorkingDirectory_AndStartsEmpty()
    {
        var transcript = Transcript.CreateNew(@"C:\repo");

        Assert.Equal(@"C:\repo", transcript.WorkingDirectory);
        Assert.Empty(transcript.Messages);
    }

    [Fact]
    public void Append_AddsMessage_AccessibleViaMessagesAndLast()
    {
        var transcript = Transcript.CreateNew("/repo");
        var message = ChatMessage.User("hi");

        transcript.Append(message);

        Assert.Equal([message], transcript.Messages);
        Assert.Equal(message, transcript.Last);
    }

    [Fact]
    public void Last_Throws_OnEmptyTranscript()
    {
        var transcript = Transcript.CreateNew("/repo");

        // Last is backed by List<T>[^1], which throws ArgumentOutOfRangeException on an
        // empty list (not the raw array indexer's IndexOutOfRangeException).
        Assert.Throws<ArgumentOutOfRangeException>(() => transcript.Last);
    }

    [Fact]
    public void LastUsage_IsNull_WhenNoUsageEverRecorded()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));
        transcript.Append(ChatMessage.Assistant([new TextBlock("hello")]));

        Assert.Null(transcript.LastUsage);
    }

    [Fact]
    public void LastUsage_ReturnsMostRecentlyRecordedUsage_NotNecessarilyOnLastMessage()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));
        transcript.Append(ChatMessage.Assistant([new TextBlock("hello")]), new UsageInfo(100, 50));
        transcript.Append(ChatMessage.User("follow-up, no usage attached"));

        var usage = transcript.LastUsage;

        Assert.NotNull(usage);
        Assert.Equal(100, usage!.InputTokens);
        Assert.Equal(50, usage.OutputTokens);
    }

    [Fact]
    public void MessagesSinceLastUsage_EqualsTotalCount_WhenNoUsageEverRecorded()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("a"));
        transcript.Append(ChatMessage.User("b"));

        Assert.Equal(2, transcript.MessagesSinceLastUsage);
    }

    [Fact]
    public void MessagesSinceLastUsage_IsZero_WhenLastMessageItselfHasUsage()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));
        transcript.Append(ChatMessage.Assistant([new TextBlock("hello")]), new UsageInfo(10, 5));

        Assert.Equal(0, transcript.MessagesSinceLastUsage);
    }

    [Fact]
    public void MessagesSinceLastUsage_CountsMessagesAfterTheUsageCarryingOne()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("a"));
        transcript.Append(ChatMessage.Assistant([new TextBlock("b")]), new UsageInfo(10, 5));
        transcript.Append(ChatMessage.User("c"));
        transcript.Append(ChatMessage.User("d"));

        Assert.Equal(2, transcript.MessagesSinceLastUsage);
    }

    [Fact]
    public void ApplyCompaction_ReplacesOlderMessages_KeepingSummaryFirst()
    {
        var transcript = Transcript.CreateNew("/repo");
        var kept1 = ChatMessage.User("kept 1");
        var kept2 = ChatMessage.User("kept 2");
        transcript.Append(ChatMessage.User("old 1"));
        transcript.Append(ChatMessage.User("old 2"));
        transcript.Append(kept1);
        transcript.Append(kept2);

        var summary = ChatMessage.CompactionSummary("summary text", tokensBefore: 100);
        transcript.ApplyCompaction(cutIndex: 2, summary);

        Assert.Equal([summary, kept1, kept2], transcript.Messages);
    }

    [Fact]
    public void ApplyCompaction_AtCutIndexZero_StillPrependsSummary_KeepingEverythingElse()
    {
        var transcript = Transcript.CreateNew("/repo");
        var only = ChatMessage.User("only message");
        transcript.Append(only);

        var summary = ChatMessage.CompactionSummary("summary", tokensBefore: 0);
        transcript.ApplyCompaction(cutIndex: 0, summary);

        Assert.Equal([summary, only], transcript.Messages);
    }

    [Fact]
    public void ApplyCompaction_AtCutIndexBeyondCount_KeepsOnlyTheSummary()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("a"));
        transcript.Append(ChatMessage.User("b"));

        var summary = ChatMessage.CompactionSummary("summary", tokensBefore: 0);
        transcript.ApplyCompaction(cutIndex: 10, summary);

        Assert.Equal([summary], transcript.Messages);
    }

    [Fact]
    public void ApplyCompaction_DiscardsAllPriorUsageTracking()
    {
        // Deliberate: the summary message carries no usage of its own. A prior attempt at this
        // fix stored tokensBefore as the summary's baseline usage, but that number is real,
        // cumulative model-reported usage that doesn't decompose cleanly into "cut vs. kept"
        // portions once LastUsage's message and the cut point are far apart (e.g. after /branch)
        // -- producing confidently wrong, sometimes wildly inflated, meter readings. Leaving
        // LastUsage null is a brief, honest blank until the next real assistant reply instead.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("a"));
        transcript.Append(ChatMessage.Assistant([new TextBlock("b")]), new UsageInfo(10, 5));

        transcript.ApplyCompaction(cutIndex: 0, ChatMessage.CompactionSummary("summary", 15));

        Assert.Null(transcript.LastUsage);
    }

    [Fact]
    public async Task LoadAsync_ReplaysSessionHeaderAndMessages_FromStore()
    {
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        await store.AppendAsync(owner, "session-1", TranscriptEntry.SessionHeader("/repo"), CancellationToken.None);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.User("hi")), CancellationToken.None);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock("hello")]), new UsageInfo(10, 5)), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, owner, "session-1", CancellationToken.None);

        Assert.Equal("/repo", transcript.WorkingDirectory);
        Assert.Equal(2, transcript.Messages.Count);
        Assert.Equal(10, transcript.LastUsage!.InputTokens);
    }

    [Fact]
    public async Task LoadAsync_SkipsEntriesWithNoMessageAndNoWorkingDirectory_WithoutThrowing()
    {
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        // A malformed/empty entry: not a session header, no message.
        await store.AppendAsync(owner, "session-1", new TranscriptEntry("user", DateTimeOffset.UtcNow, null, null, null), CancellationToken.None);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.User("real message")), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, owner, "session-1", CancellationToken.None);

        Assert.Single(transcript.Messages);
    }

    [Fact]
    public async Task LoadAsync_OnEmptyStore_ProducesEmptyTranscript_WithNullWorkingDirectory()
    {
        var store = new FakeTranscriptStore();

        var transcript = await Transcript.LoadAsync(store, SessionOwner.Local, "nonexistent-session", CancellationToken.None);

        Assert.Empty(transcript.Messages);
        Assert.Null(transcript.WorkingDirectory);
    }

    [Fact]
    public async Task LoadAsync_DiscardsMessagesBeforeACompactionSummary()
    {
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        await store.AppendAsync(owner, "session-1", TranscriptEntry.SessionHeader("/repo"), CancellationToken.None);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.User("old 1")), CancellationToken.None);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock("old 2")]), new UsageInfo(100, 50)), CancellationToken.None);
        var summary = ChatMessage.CompactionSummary("summary text", tokensBefore: 150);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(summary), CancellationToken.None);
        var kept = ChatMessage.User("kept 1");
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(kept), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, owner, "session-1", CancellationToken.None);

        Assert.Equal([summary, kept], transcript.Messages);
    }

    [Fact]
    public async Task LoadAsync_WithCompactionSummary_DoesNotCarryForwardPreCompactionUsage()
    {
        // Mirrors ApplyCompaction_DiscardsAllPriorUsageTracking above: real, cumulative
        // pre-compaction usage doesn't decompose into "cut vs. kept" once replayed, so
        // LoadAsync must drop it the same way ApplyCompaction does in-memory rather than
        // reporting a stale/misleading LastUsage after a resume.
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.User("old")), CancellationToken.None);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock("reply")]), new UsageInfo(10, 5)), CancellationToken.None);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.CompactionSummary("summary", tokensBefore: 15)), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, owner, "session-1", CancellationToken.None);

        Assert.Null(transcript.LastUsage);
    }

    [Fact]
    public async Task LoadAsync_WithMultipleCompactionSummaries_KeepsOnlyTheLatest()
    {
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.User("old 1")), CancellationToken.None);
        var firstSummary = ChatMessage.CompactionSummary("first summary", tokensBefore: 100);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(firstSummary), CancellationToken.None);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(ChatMessage.User("kept between compactions")), CancellationToken.None);
        var secondSummary = ChatMessage.CompactionSummary("second summary", tokensBefore: 200);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(secondSummary), CancellationToken.None);
        var kept = ChatMessage.User("kept after second compaction");
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(kept), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, owner, "session-1", CancellationToken.None);

        Assert.Equal([secondSummary, kept], transcript.Messages);
    }

    [Fact]
    public async Task LoadAsync_WithOnlyACompactionSummary_ProducesJustTheSummary()
    {
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        var summary = ChatMessage.CompactionSummary("summary", tokensBefore: 0);
        await store.AppendAsync(owner, "session-1", TranscriptEntry.FromMessage(summary), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, owner, "session-1", CancellationToken.None);

        Assert.Equal([summary], transcript.Messages);
    }

    [Fact]
    public async Task LoadAsync_AfterBranchingFromBeforeACompaction_ReplaysFullHistory_NoSummary()
    {
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        var q1 = ChatMessage.User("Q1");
        var a1 = ChatMessage.Assistant([new TextBlock("A1")]);
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(q1), CancellationToken.None); // entry 0
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(a1), CancellationToken.None); // entry 1
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(ChatMessage.CompactionSummary("summary", tokensBefore: 50)), CancellationToken.None); // entry 2

        // Branch from entry 1 (+1, matching the UI's inclusive-of-picked-message convention) — before the compaction.
        var branchedId = await store.BranchAsync(owner, "source", uptoEntryIndex: 1 + 1, CancellationToken.None);
        var transcript = await Transcript.LoadAsync(store, owner, branchedId, CancellationToken.None);

        Assert.Equal([q1, a1], transcript.Messages);
    }

    [Fact]
    public async Task LoadAsync_AfterBranchingFromAfterACompaction_StartsFromTheSummary()
    {
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        var summary = ChatMessage.CompactionSummary("summary", tokensBefore: 50);
        var q2 = ChatMessage.User("Q2");
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(ChatMessage.User("Q1")), CancellationToken.None); // entry 0
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(summary), CancellationToken.None); // entry 1
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(q2), CancellationToken.None); // entry 2

        // Branch from entry 2 ("Q2") — after the compaction.
        var branchedId = await store.BranchAsync(owner, "source", uptoEntryIndex: 2 + 1, CancellationToken.None);
        var transcript = await Transcript.LoadAsync(store, owner, branchedId, CancellationToken.None);

        Assert.Equal([summary, q2], transcript.Messages);
    }

    [Fact]
    public async Task LoadAsync_AfterBranchingFromBetweenTwoCompactions_StartsFromTheEarlierSummary()
    {
        var store = new FakeTranscriptStore();
        var owner = SessionOwner.Local;
        var firstSummary = ChatMessage.CompactionSummary("first summary", tokensBefore: 50);
        var betweenMessage = ChatMessage.User("Q2 between compactions");
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(ChatMessage.User("Q1")), CancellationToken.None); // entry 0
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(firstSummary), CancellationToken.None); // entry 1
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(betweenMessage), CancellationToken.None); // entry 2
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(ChatMessage.CompactionSummary("second summary", tokensBefore: 200)), CancellationToken.None); // entry 3
        await store.AppendAsync(owner, "source", TranscriptEntry.FromMessage(ChatMessage.User("Q3")), CancellationToken.None); // entry 4

        // Branch from entry 2 ("Q2 between compactions") — after the first compaction, before the second.
        var branchedId = await store.BranchAsync(owner, "source", uptoEntryIndex: 2 + 1, CancellationToken.None);
        var transcript = await Transcript.LoadAsync(store, owner, branchedId, CancellationToken.None);

        Assert.Equal([firstSummary, betweenMessage], transcript.Messages);
    }
}
