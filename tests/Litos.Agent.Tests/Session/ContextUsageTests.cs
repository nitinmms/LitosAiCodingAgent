using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;

namespace Litos.Agent.Tests.Session;

public class ContextUsageTests
{
    [Fact]
    public void Compute_ReturnsNull_WhenNoUsageEverRecorded()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));

        Assert.Null(ContextUsage.Compute(transcript, contextLength: 200_000));
    }

    [Fact]
    public void Compute_ReturnsFractionAndNormalLevel_WellUnderReserveThreshold()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(10_000, 0));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000, reserveTokens: 16_000);

        Assert.NotNull(snapshot);
        Assert.Equal(10_000, snapshot!.UsedTokens);
        Assert.Equal(200_000, snapshot.ContextLength);
        Assert.Equal(0.05, snapshot.Fraction, precision: 5);
        Assert.Equal(ContextUsageLevel.Normal, snapshot.Level);
    }

    [Fact]
    public void Compute_ReturnsCriticalLevel_AtOrAboveReserveThreshold()
    {
        // ContextWindowTokens=200_000, ReserveTokens=16_000 -> reserveThreshold = 184_000.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(184_000, 0));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000, reserveTokens: 16_000);

        Assert.NotNull(snapshot);
        Assert.Equal(ContextUsageLevel.Critical, snapshot!.Level);
    }

    [Fact]
    public void Compute_ReturnsWarningLevel_AboveWarningFractionOfReserveThreshold_ButBelowReserveThreshold()
    {
        // reserveThreshold = 184_000; warning kicks in at 0.6 * 184_000 = 110_400.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(150_000, 0));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000, reserveTokens: 16_000);

        Assert.NotNull(snapshot);
        Assert.Equal(ContextUsageLevel.Warning, snapshot!.Level);
    }

    [Fact]
    public void Compute_ReturnsNormalLevel_JustBelowWarningFractionOfReserveThreshold()
    {
        // reserveThreshold = 184_000; warning threshold = 110_400 -> one token under stays Normal.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(110_399, 0));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000, reserveTokens: 16_000);

        Assert.NotNull(snapshot);
        Assert.Equal(ContextUsageLevel.Normal, snapshot!.Level);
    }

    [Fact]
    public void Compute_IncludesTrailingMessagesSinceLastUsage_InUsedTokens()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(10_000, 0));
        // 40 chars -> +10 estimated tokens (40/4, integer division).
        transcript.Append(ChatMessage.User(new string('x', 40)));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000);

        Assert.NotNull(snapshot);
        Assert.Equal(10_010, snapshot!.UsedTokens);
    }

    [Fact]
    public void Compute_ClampsToZeroFraction_WhenContextLengthIsZeroOrNegative()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(10_000, 0));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 0);

        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.Fraction);
    }

    // ---- Fraction clamp and staleness: the estimate this is built from (real usage plus a
    // char/4 count of every message since) can run past a real conversation's actual size when
    // many messages pile up with no fresh usage report in between — e.g. repeated
    // sends/steering against a turn stuck mid-tool-call (McpServerConnection.CallToolAsync's
    // hard timeout was added for the same underlying bug). These guard the display side of that:
    // never show more than 100%, and flag when the number can no longer be trusted.

    [Fact]
    public void Compute_ClampsFractionToOneHundredPercent_WhenEstimateExceedsContextLength()
    {
        var transcript = Transcript.CreateNew("/repo");
        // Real usage already at 90% of the window, plus a huge trailing message pushes the raw
        // estimate well past contextLength.
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(180_000, 0));
        transcript.Append(ChatMessage.User(new string('x', 400_000))); // +100_000 estimated tokens

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000);

        Assert.NotNull(snapshot);
        Assert.Equal(280_000, snapshot!.UsedTokens); // the raw estimate is preserved for display...
        Assert.Equal(1.0, snapshot.Fraction);         // ...but Fraction itself never exceeds 100%.
    }

    [Fact]
    public void Compute_IsNotStale_WhenFewMessagesSinceLastUsage()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(10_000, 0));
        transcript.Append(ChatMessage.User("a normal follow-up"));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IsStale);
    }

    [Fact]
    public void Compute_IsStale_WhenManyMessagesHavePiledUpSinceTheLastRealUsageReport()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(10_000, 0));
        // Simulates repeated retries against a turn that never completes a round (so LastUsage
        // never refreshes) — e.g. the user hitting Send/steering again and again against a stuck
        // tool call, each attempt appending another message with no new usage report to anchor to.
        for (var i = 0; i <= ContextUsage.StaleAfterMessagesSinceLastUsage; i++)
            transcript.Append(ChatMessage.User("retry"));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsStale);
    }

    [Fact]
    public void Compute_IsNotStale_ExactlyAtTheThreshold()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(10_000, 0));
        for (var i = 0; i < ContextUsage.StaleAfterMessagesSinceLastUsage; i++)
            transcript.Append(ChatMessage.User("retry"));

        var snapshot = ContextUsage.Compute(transcript, contextLength: 200_000);

        Assert.NotNull(snapshot);
        Assert.Equal(ContextUsage.StaleAfterMessagesSinceLastUsage, transcript.MessagesSinceLastUsage);
        Assert.False(snapshot!.IsStale);
    }

    // ---- Branch/resume scenarios: the context meter must reflect only the loaded transcript,
    // never anything left behind by BranchAsync's truncation or carried over from a prior session.

    [Fact]
    public void Compute_AfterLoadingABranchedTranscript_ReflectsOnlyTheKeptMessages_NotTheOriginalSessions()
    {
        // Simulates /branch: the original session had a big assistant reply with real usage,
        // then more turns after it; branching back to right after the first user message
        // produces a transcript with none of that later usage in it at all.
        var original = Transcript.CreateNew("/repo");
        original.Append(ChatMessage.User("first question"));
        original.Append(ChatMessage.Assistant([new TextBlock("first answer")]), new UsageInfo(150_000, 5_000));
        original.Append(ChatMessage.User("second question"));
        original.Append(ChatMessage.Assistant([new TextBlock("second answer")]), new UsageInfo(190_000, 5_000));

        var branched = Transcript.CreateNew("/repo");
        branched.Append(ChatMessage.User("first question")); // only entry kept by the branch

        var branchedSnapshot = ContextUsage.Compute(branched, contextLength: 200_000, reserveTokens: 16_000);
        var originalSnapshot = ContextUsage.Compute(original, contextLength: 200_000, reserveTokens: 16_000);

        // The branched session has no assistant usage of its own yet -> null ("—" in the GUI),
        // never the 195_000 total the pre-branch session had racked up.
        Assert.Null(branchedSnapshot);
        Assert.NotNull(originalSnapshot);
        Assert.Equal(195_000, originalSnapshot!.UsedTokens);
    }

    [Fact]
    public void Compute_AfterLoadingABranchedTranscript_UsesTheBranchedSessionsOwnUsage_WhenItHasAnAssistantReply()
    {
        // Branching to a point that keeps a completed assistant turn means the meter should
        // report that turn's own usage, not the fuller original session's later, larger usage.
        var branched = Transcript.CreateNew("/repo");
        branched.Append(ChatMessage.User("first question"));
        branched.Append(ChatMessage.Assistant([new TextBlock("first answer")]), new UsageInfo(20_000, 1_000));

        var snapshot = ContextUsage.Compute(branched, contextLength: 200_000, reserveTokens: 16_000);

        Assert.NotNull(snapshot);
        Assert.Equal(21_000, snapshot!.UsedTokens);
        Assert.Equal(ContextUsageLevel.Normal, snapshot.Level);
    }
}
