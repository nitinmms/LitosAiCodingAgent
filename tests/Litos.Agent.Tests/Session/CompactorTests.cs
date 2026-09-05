using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tests.Fakes;

namespace Litos.Agent.Tests.Session;

public class CompactorTests
{
    [Fact]
    public async Task TryCompactAsync_ReturnsFalse_WhenShouldCompactIsFalse()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi")); // no usage recorded -> ShouldCompact is false
        var provider = new FakeChatProvider();
        var compactor = new Compactor(new CompactionSettings());

        var compacted = await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        Assert.False(compacted);
        Assert.Empty(provider.ReceivedRequests);
    }

    [Fact]
    public async Task TryCompactAsync_ReturnsFalse_WhenFindCutPointReturnsNull()
    {
        var transcript = Transcript.CreateNew("/repo");
        // Usage over threshold triggers ShouldCompact, but a single short message means
        // FindCutPoint's "nothing old enough to cut" branch returns null.
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(190_000, 0));
        var provider = new FakeChatProvider();
        var compactor = new Compactor(new CompactionSettings());

        var compacted = await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        Assert.False(compacted);
    }

    [Fact]
    public async Task TryCompactAsync_OnSuccess_SummarizesAndAppliesCompaction()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]), new UsageInfo(190_000, 0));
        transcript.Append(ChatMessage.User("recent question"));

        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("Summary "), new TextDelta("of the conversation."));
        var compactor = new Compactor(new CompactionSettings());

        var compacted = await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        Assert.True(compacted);
        var summaryBlock = Assert.IsType<CompactionSummaryBlock>(Assert.Single(transcript.Messages[0].Content));
        Assert.Equal("Summary of the conversation.", summaryBlock.Summary);
    }

    [Fact]
    public async Task TryCompactAsync_Summarize_OnlyAccumulatesTextDeltaEvents_IgnoringOthers()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]), new UsageInfo(190_000, 0));
        transcript.Append(ChatMessage.User("recent"));

        var provider = new FakeChatProvider();
        provider.Enqueue(
            new ToolCallStarted("call_1", "ignored"),
            new TextDelta("actual "),
            new ErrorOccurred(new InvalidOperationException("ignored error event")),
            new TextDelta("summary"));
        var compactor = new Compactor(new CompactionSettings());

        await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        var summaryBlock = Assert.IsType<CompactionSummaryBlock>(transcript.Messages[0].Content[0]);
        Assert.Equal("actual summary", summaryBlock.Summary);
    }

    [Fact]
    public async Task TryCompactAsync_FallsBackToPlaceholder_WhenNoTextDeltaEventsAreYielded()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]), new UsageInfo(190_000, 0));
        transcript.Append(ChatMessage.User("recent"));

        var provider = new FakeChatProvider();
        provider.Enqueue(new ErrorOccurred(new InvalidOperationException("provider failed")));
        var compactor = new Compactor(new CompactionSettings());

        var compacted = await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        Assert.True(compacted);
        var summaryBlock = Assert.IsType<CompactionSummaryBlock>(transcript.Messages[0].Content[0]);
        Assert.Equal("(compaction summary unavailable)", summaryBlock.Summary);
    }

    [Fact]
    public async Task TryCompactAsync_TokensBefore_MatchesEstimatedTokensUsed_IncludingTrailingMessages()
    {
        // TokensBefore must equal exactly what the context-usage meter (ContextUsage.Compute ->
        // CompactionPlanner.EstimatedTokensUsed) was showing on screen right before compaction ran
        // -- LastUsage alone would silently drop the trailing "recent" message's estimated cost,
        // understating what the user just saw and was asking to compact away.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]), new UsageInfo(190_000, 1_000));
        transcript.Append(ChatMessage.User("recent")); // 6 chars -> 1 trailing token (6/4, integer division)

        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("summary"));
        var compactor = new Compactor(new CompactionSettings());

        await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        var summaryBlock = Assert.IsType<CompactionSummaryBlock>(transcript.Messages[0].Content[0]);
        Assert.Equal(191_001, summaryBlock.TokensBefore);
    }

    [Fact]
    public async Task TryCompactAsync_LeavesLastUsageNull_SoMeterShowsNoUsageReportedYet_UntilNextRealTurn()
    {
        // EstimatedTokensUsed (what the context-usage meter displays) returns null whenever
        // LastUsage is null -- deliberately not backfilled with an estimated baseline. A real,
        // cumulative model-reported usage number doesn't decompose cleanly into "cut vs. kept"
        // portions once the message that carried it and the cut point are far apart (e.g. after
        // /branch, where many kept messages can separate them), so any attempt to synthesize a
        // baseline from it risks a confidently wrong -- sometimes wildly inflated -- meter reading
        // instead of this honest, brief blank until the next real assistant reply.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]), new UsageInfo(190_000, 0));
        transcript.Append(ChatMessage.User("recent"));

        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("summary"));
        var compactor = new Compactor(new CompactionSettings());

        await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        Assert.Null(transcript.LastUsage);
        Assert.Null(CompactionPlanner.EstimatedTokensUsed(transcript));
    }

    // ---- contextWindowTokens (per-turn context-window awareness) ----

    [Fact]
    public async Task TryCompactAsync_ContextWindowTokens_TriggersCompaction_BelowTheSharedDefaultThreshold()
    {
        // Regression guard for the bug this parameter exists to fix: measured against the shared
        // CompactionSettings default (200K), 20_000 estimated tokens is nowhere near the 184K
        // threshold and TryCompactAsync would return false — exactly what silently happened for
        // every local-model session before AgentLoop started passing the resolved model's own
        // (much smaller) context window through.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 40_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 40_000))]), new UsageInfo(19_000, 1_000));
        transcript.Append(ChatMessage.User("recent question"));

        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("summary"));
        var compactor = new Compactor(new CompactionSettings());

        var compactedWithoutContextWindow = await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);
        Assert.False(compactedWithoutContextWindow);

        var compactedWithContextWindow = await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: 16_000, CancellationToken.None);
        Assert.True(compactedWithContextWindow);
    }

    // ---- ForceCompactAsync (/compact) ----

    [Fact]
    public async Task ForceCompactAsync_CompactsEvenWhenShouldCompactWouldBeFalse()
    {
        var transcript = Transcript.CreateNew("/repo");
        // No usage recorded at all -> ShouldCompact/TryCompactAsync would return false, but
        // there's still a valid, old-enough cut point for a forced manual compaction to use.
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]));
        transcript.Append(ChatMessage.User("recent question"));

        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("forced summary"));
        var compactor = new Compactor(new CompactionSettings());

        var compacted = await compactor.ForceCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        Assert.True(compacted);
        var summaryBlock = Assert.IsType<CompactionSummaryBlock>(Assert.Single(transcript.Messages[0].Content));
        Assert.Equal("forced summary", summaryBlock.Summary);
    }

    [Fact]
    public async Task ForceCompactAsync_ReturnsFalse_WhenFindCutPointReturnsNull()
    {
        var transcript = Transcript.CreateNew("/repo");
        // A single short message: even forced, there's nothing old enough to cut.
        transcript.Append(ChatMessage.User("hi"));
        var provider = new FakeChatProvider();
        var compactor = new Compactor(new CompactionSettings());

        var compacted = await compactor.ForceCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        Assert.False(compacted);
        Assert.Empty(provider.ReceivedRequests);
    }

    [Fact]
    public async Task TryCompactAsync_StillReturnsFalse_WhenShouldCompactIsFalse_AfterForceCompactAsyncExists()
    {
        // Regression guard: adding the force:bool parameter to the shared CompactAsync helper
        // must not accidentally flip TryCompactAsync's own threshold-gating behavior.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));
        var provider = new FakeChatProvider();
        var compactor = new Compactor(new CompactionSettings());

        var compacted = await compactor.TryCompactAsync(transcript, provider, "model", contextWindowTokens: null, CancellationToken.None);

        Assert.False(compacted);
        Assert.Empty(provider.ReceivedRequests);
    }
}
