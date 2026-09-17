using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;

namespace Litos.Agent.Tests.Session;

public class CompactionPlannerTests
{
    private static readonly CompactionSettings DefaultSettings = new();

    // ---- CompactionSettings.ForContextWindow ----

    [Fact]
    public void ForContextWindow_SmallWindow_CapsReserveAndKeepRecent_ToAQuarterOfTheWindow()
    {
        // A local model's real (or best-known-fallback) context window is often far smaller than
        // the 200K default this record otherwise assumes — reusing the 16K/20K defaults as-is
        // against e.g. an 8K window would make the trigger negative (compact on every turn) and
        // KeepRecentTokens exceed the whole window (FindCutPoint could never find anything old
        // enough to cut, so compaction would never actually apply).
        var scaled = DefaultSettings.ForContextWindow(8_000);

        Assert.Equal(8_000, scaled.ContextWindowTokens);
        Assert.Equal(2_000, scaled.ReserveTokens);
        Assert.Equal(2_000, scaled.KeepRecentTokens);
        // At or below SmallWindowThresholdTokens the capacity bound alone governs: 8_000 - 2_000.
        Assert.Equal(6_000, scaled.TriggerAtTokens);
    }

    [Fact]
    public void ForContextWindow_WindowLargerThanDefaults_LeavesReserveAndKeepRecentUnchanged()
    {
        // A quarter of 200K (50K) comfortably exceeds both the 16K reserve and 20K keep-recent
        // defaults, so a hosted model's real/larger window must not perturb those.
        var scaled = DefaultSettings.ForContextWindow(200_000);

        Assert.Equal(200_000, scaled.ContextWindowTokens);
        Assert.Equal(16_000, scaled.ReserveTokens);
        Assert.Equal(20_000, scaled.KeepRecentTokens);
    }

    [Theory]
    // At or below SmallWindowThresholdTokens (64K) the capacity bound alone governs, preserving
    // the 75% these windows had before the proportional trigger existed.
    [InlineData(8_000, 6_000)]
    [InlineData(32_000, 24_000)]
    [InlineData(64_000, 48_000)]
    // Just past the threshold the 0.65 proportional bound takes over — a deliberate step down
    // (48_000 -> 41_600), not a gradual crossover: the split is by window size, not by which
    // bound happens to be tighter (capacity is never the tighter one at any size).
    [InlineData(64_001, 41_600)]
    [InlineData(128_000, 83_200)]
    [InlineData(200_000, 130_000)]
    // Above ~385K the absolute MaxTriggerTokens ceiling takes over from the proportional bound.
    [InlineData(400_000, 250_000)]
    [InlineData(1_000_000, 250_000)]
    public void ForContextWindow_TriggerIsProportional_AndCappedAtMaxTriggerTokens(int window, int expectedTrigger)
    {
        // Regression test for the cost bug this replaced: the trigger used to be capacity alone
        // (window - reserve), whose effective percentage drifted upward as windows grew — 92% of a
        // 200K window and 98.4% of a 1M one (984_000). Since AgentLoop re-checks compaction every
        // tool-calling round rather than once per turn, a long turn re-sent a near-full context on
        // every round before anything fired.
        Assert.Equal(expectedTrigger, DefaultSettings.ForContextWindow(window).TriggerAtTokens);
    }

    [Fact]
    public void ForContextWindow_TriggerNeverExceedsCapacity()
    {
        // The capacity bound is not the binding term at any size today, but it must still hold if
        // TriggerFractionOfWindow/MaxTriggerTokens are ever retuned upward.
        foreach (var window in new[] { 4_000, 8_000, 32_000, 128_000, 200_000, 1_000_000, 10_000_000 })
        {
            var scaled = DefaultSettings.ForContextWindow(window);
            Assert.True(
                scaled.TriggerAtTokens <= window - scaled.ReserveTokens,
                $"window {window}: trigger {scaled.TriggerAtTokens} exceeded capacity {window - scaled.ReserveTokens}");
            Assert.True(scaled.TriggerAtTokens > 0, $"window {window}: trigger must stay positive");
        }
    }

    [Theory]
    // The flat 20K floor holds until 0.075 * window overtakes it (~267K).
    [InlineData(200_000, 20_000)]
    [InlineData(400_000, 30_000)]
    [InlineData(1_000_000, 75_000)]
    public void ForContextWindow_KeepRecentScalesUpOnLargeWindows(int window, int expectedKeepRecent)
    {
        // A fixed 20K verbatim tail is most lossy exactly where windows are largest, and the
        // tightened trigger above makes compaction fire more often — so how much survives each cut
        // compounds. Agent-compression research finds recency is the single most important thing
        // to preserve (summarizing recent turns causes execution-state mislocalization, terminal-
        // recognition loss and regressive exploration), so the tail scales with the window.
        Assert.Equal(expectedKeepRecent, DefaultSettings.ForContextWindow(window).KeepRecentTokens);
    }

    [Fact]
    public void ForContextWindow_KeepRecentStillCappedToAQuarter_OnVerySmallWindows()
    {
        // window/4 must still win over both the 20K floor and the 0.075 fraction, or nothing would
        // ever be old enough to cut.
        Assert.Equal(2_000, DefaultSettings.ForContextWindow(8_000).KeepRecentTokens);
    }

    // ---- ShouldCompact ----

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenDisabled()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(190_000, 0));

        Assert.False(CompactionPlanner.ShouldCompact(transcript, DefaultSettings with { Enabled = false }));
    }

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenNoUsageEverRecorded()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));

        Assert.False(CompactionPlanner.ShouldCompact(transcript, DefaultSettings));
    }

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenWellUnderContextWindow()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1000, 500));

        Assert.False(CompactionPlanner.ShouldCompact(transcript, DefaultSettings));
    }

    [Fact]
    public void ShouldCompact_ReturnsFalse_AtExactThreshold_StrictlyGreaterThanRequired()
    {
        // The bare (un-rescoped) DefaultSettings carries TriggerAtTokens=184_000 directly;
        // ForContextWindow is what derives a proportional trigger, and is covered above.
        // lastUsage sums to exactly 184_000 with no trailing messages, so estimatedTokens == threshold.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(184_000, 0));

        Assert.False(CompactionPlanner.ShouldCompact(transcript, DefaultSettings));
    }

    [Fact]
    public void ShouldCompact_ReturnsTrue_OneTokenOverThreshold()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(184_001, 0));

        Assert.True(CompactionPlanner.ShouldCompact(transcript, DefaultSettings));
    }

    [Fact]
    public void ShouldCompact_IncludesTrailingMessagesSinceLastUsage_InEstimate()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(183_999, 0));
        // One trailing message with 10 chars -> +2 estimated tokens (10/4 = 2, integer division), pushing over 184_000.
        transcript.Append(ChatMessage.User(new string('x', 10)));

        Assert.True(CompactionPlanner.ShouldCompact(transcript, DefaultSettings));
    }

    // ---- FindCutPoint ----

    [Fact]
    public void FindCutPoint_ReturnsNull_WhenMessagesListIsEmpty()
    {
        Assert.Null(CompactionPlanner.FindCutPoint([], keepRecentTokens: 100));
    }

    [Fact]
    public void FindCutPoint_ReturnsNull_WhenEveryMessageContainsAToolResult()
    {
        List<ChatMessage> messages = [ChatMessage.ToolResult("call_1", new Litos.Agent.Tools.ToolResult("output"))];

        Assert.Null(CompactionPlanner.FindCutPoint(messages, keepRecentTokens: 0));
    }

    [Fact]
    public void FindCutPoint_ReturnsNull_WhenConversationIsShorterThanKeepRecentTokens()
    {
        // Short conversation: accumulated chars never reach keepRecentTokens, and message[0]
        // is a valid cut point (no tool result), so cutIndex stays at validCutPoints[0] == 0,
        // which the "nothing old enough to cut" check nulls out.
        List<ChatMessage> messages = [ChatMessage.User("hi"), ChatMessage.Assistant([new TextBlock("hello")])];

        Assert.Null(CompactionPlanner.FindCutPoint(messages, keepRecentTokens: 1_000_000));
    }

    [Fact]
    public void FindCutPoint_ShortConversation_WithToolResultAtIndexZero_ReturnsNonNullCutPoint()
    {
        // Documented edge case: if index 0 isn't a valid cut point (it has a tool result),
        // validCutPoints[0] is some index > 0, so even a conversation too short to satisfy
        // the backward-accumulation threshold produces a non-null cut point.
        List<ChatMessage> messages =
        [
            ChatMessage.ToolResult("call_1", new Litos.Agent.Tools.ToolResult("tool output")), // index 0: NOT a valid cut point
            ChatMessage.User("hi"), // index 1: valid cut point
        ];

        var cutPoint = CompactionPlanner.FindCutPoint(messages, keepRecentTokens: 1_000_000);

        Assert.NotNull(cutPoint);
        Assert.Equal(1, cutPoint!.Index);
    }

    [Fact]
    public void FindCutPoint_NeverCutsBetweenToolUseAndToolResult()
    {
        List<ChatMessage> messages =
        [
            ChatMessage.User(new string('a', 400)),                                                   // 0: valid
            ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", ValidJsonArgs())]),         // 1: valid (no tool RESULT block)
            ChatMessage.ToolResult("call_1", new Litos.Agent.Tools.ToolResult(new string('b', 400))),  // 2: NOT valid, 400 chars -> 100 tokens
            ChatMessage.User(new string('c', 400)),                                                    // 3: valid, 400 chars -> 100 tokens
        ];

        // keepRecentTokens=150 is deliberately tuned so the backward scan does NOT already
        // satisfy the threshold at i=3 (accumulated=100 < 150) and only crosses it once i=2
        // is folded in (accumulated=200 >= 150) — i.e. the scan genuinely reaches the tool
        // result message before snapping, so this actually exercises "snap forward past an
        // invalid cut point" rather than trivially avoiding index 2 by never scanning that far.
        var cutPoint = CompactionPlanner.FindCutPoint(messages, keepRecentTokens: 150);

        Assert.NotNull(cutPoint);
        Assert.Equal(3, cutPoint!.Index);
    }

    [Fact]
    public void FindCutPoint_CutAtUserMessage_IsNotASplitTurn()
    {
        List<ChatMessage> messages =
        [
            ChatMessage.User(new string('a', 4000)),
            ChatMessage.Assistant([new TextBlock(new string('b', 4000))]),
            ChatMessage.User(new string('c', 10)),
            ChatMessage.Assistant([new TextBlock(new string('d', 10))]),
        ];

        // keepRecentTokens small enough that the cut lands at/after the second user message.
        var cutPoint = CompactionPlanner.FindCutPoint(messages, keepRecentTokens: 3);

        Assert.NotNull(cutPoint);
        Assert.False(cutPoint!.IsSplitTurn);
        Assert.Equal(cutPoint.Index, cutPoint.TurnStartIndex);
    }

    [Fact]
    public void FindCutPoint_CutOnAssistantMessage_IsASplitTurn_AndTurnStartIndexPointsToPrecedingUserMessage()
    {
        // cutIndex landing on an assistant message (not Role.User) makes isUserTurnStart
        // false regardless of tool-result content, so IsSplitTurn is true and
        // TurnStartIndex must walk back to the nearest preceding user message.
        List<ChatMessage> messages =
        [
            ChatMessage.User(new string('a', 4000)),                      // 0: turn start
            ChatMessage.Assistant([new TextBlock(new string('b', 10))]),  // 1: last valid cut point; tiny char cost
        ];

        // keepRecentTokens=1 makes the backward scan snap on the very first (last) message
        // it inspects (index 1), whose own estimated tokens alone (10/4=2) already exceed 1.
        var cutPoint = CompactionPlanner.FindCutPoint(messages, keepRecentTokens: 1);

        Assert.NotNull(cutPoint);
        Assert.Equal(1, cutPoint!.Index);
        Assert.True(cutPoint.IsSplitTurn);
        Assert.Equal(0, cutPoint.TurnStartIndex);
    }

    [Fact]
    public void FindCutPoint_FindTurnStart_FallsBackToZero_WhenNoPrecedingUserMessageExists()
    {
        // Conversation that starts with an assistant message (unusual but not guarded
        // against) — FindTurnStart should fall back to 0 rather than throwing.
        List<ChatMessage> messages =
        [
            ChatMessage.Assistant([new TextBlock(new string('a', 10))]), // 0: valid cut point, not a user turn start
            ChatMessage.Assistant([new TextBlock(new string('b', 10))]), // 1: valid cut point
        ];

        var cutPoint = CompactionPlanner.FindCutPoint(messages, keepRecentTokens: 1);

        Assert.NotNull(cutPoint);
        Assert.Equal(0, cutPoint!.TurnStartIndex);
    }

    [Fact]
    public void FindCutPoint_EstimateChars_ThrowsForUninitializedToolUseArguments()
    {
        // Calling .GetRawText() on a default(JsonElement) throws InvalidOperationException —
        // documents this as a real, reachable failure mode if a ToolUseBlock is ever
        // constructed without properly parsed JSON arguments.
        List<ChatMessage> messages =
        [
            ChatMessage.User("valid"),
            ChatMessage.Assistant([new ToolUseBlock("call_1", "tool", default)]),
        ];

        Assert.Throws<InvalidOperationException>(() => CompactionPlanner.FindCutPoint(messages, keepRecentTokens: 1));
    }

    [Fact]
    public void FindCutPoint_ImageBlock_UsesFixedCharEstimate_RegardlessOfActualByteSize()
    {
        // EstimatedImageChars is a fixed 4800 -> 1200 tokens per image regardless of actual
        // byte size (Compaction.cs:18/91). keepRecentTokens=1000 is tuned so that, under the
        // CORRECT fixed-cost implementation, the backward scan crosses the threshold on the
        // image message alone (1200 >= 1000) for BOTH a tiny and a huge image, snapping the
        // cut to the image message itself (index 1) in both cases. If EstimateChars were
        // regressed to use Data.Length instead, the tiny image (1 byte -> 0 tokens) would
        // NOT cross 1000 on its own and the scan would continue to the preceding message
        // (landing on index 0, or null), while the huge image (1,000,000 bytes -> 250,000
        // tokens) would still cross it immediately at index 1 — the two results would
        // diverge. Asserting the exact non-null index (not just equality of two results)
        // is what makes this catch that divergence rather than being vacuously true.
        List<ChatMessage> bigImage = [ChatMessage.User("preceding"), ChatMessage.User([new ImageBlock("image/png", new byte[1_000_000])])];
        List<ChatMessage> tinyImage = [ChatMessage.User("preceding"), ChatMessage.User([new ImageBlock("image/png", [1])])];

        var bigResult = CompactionPlanner.FindCutPoint(bigImage, keepRecentTokens: 1000);
        var tinyResult = CompactionPlanner.FindCutPoint(tinyImage, keepRecentTokens: 1000);

        Assert.Equal(bigResult, tinyResult);
        Assert.NotNull(bigResult);
        Assert.Equal(1, bigResult!.Index);
    }

    private static JsonElement ValidJsonArgs() => JsonDocument.Parse("{}").RootElement;

    // ---- SnapToSafeBranchPoint ----

    private static TranscriptEntry EntryFor(ChatMessage message) =>
        new(message.Role == Role.Assistant ? "assistant" : "user", DateTimeOffset.UtcNow, message, CallId: null, Usage: null);

    [Fact]
    public void SnapToSafeBranchPoint_ReturnsSameIndex_WhenNoEntriesExist()
    {
        Assert.Equal(0, CompactionPlanner.SnapToSafeBranchPoint([], uptoEntryIndex: 0));
    }

    [Fact]
    public void SnapToSafeBranchPoint_ReturnsRequestedIndex_WhenLastKeptEntryHasNoToolUse()
    {
        List<TranscriptEntry> entries =
        [
            EntryFor(ChatMessage.User("hi")),
            EntryFor(ChatMessage.Assistant([new TextBlock("hello")])),
        ];

        Assert.Equal(2, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: 2));
    }

    [Fact]
    public void SnapToSafeBranchPoint_ReturnsRequestedIndex_WhenToolCallAlreadyHasItsResultKept()
    {
        List<TranscriptEntry> entries =
        [
            EntryFor(ChatMessage.User("read the file")),
            EntryFor(ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", ValidJsonArgs())])),
            EntryFor(ChatMessage.ToolResult("call_1", new Litos.Agent.Tools.ToolResult("contents"))),
        ];

        Assert.Equal(3, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: 3));
    }

    [Fact]
    public void SnapToSafeBranchPoint_SnapsBackward_WhenCuttingRightAfterAToolUseWithNoResultYet()
    {
        // Requesting index 2 keeps [user, assistant tool_use] but drops the tool_result at
        // index 2 — a dangling tool_use most providers would reject on the next request, so
        // this must snap back to 1 (right before the tool_use).
        List<TranscriptEntry> entries =
        [
            EntryFor(ChatMessage.User("read the file")),
            EntryFor(ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", ValidJsonArgs())])),
            EntryFor(ChatMessage.ToolResult("call_1", new Litos.Agent.Tools.ToolResult("contents"))),
        ];

        Assert.Equal(1, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: 2));
    }

    [Fact]
    public void SnapToSafeBranchPoint_SnapsBackward_WhenOnlySomeOfMultipleToolCallsHaveResultsKept()
    {
        // A single assistant message can carry more than one ToolUseBlock (parallel tool
        // calls). Keeping only one of the two results back is still unsafe.
        List<TranscriptEntry> entries =
        [
            EntryFor(ChatMessage.User("read both files")),
            EntryFor(ChatMessage.Assistant([
                new ToolUseBlock("call_1", "read_file", ValidJsonArgs()),
                new ToolUseBlock("call_2", "read_file", ValidJsonArgs()),
            ])),
            EntryFor(ChatMessage.ToolResult("call_1", new Litos.Agent.Tools.ToolResult("contents 1"))),
            EntryFor(ChatMessage.ToolResult("call_2", new Litos.Agent.Tools.ToolResult("contents 2"))),
        ];

        Assert.Equal(1, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: 3));
        Assert.Equal(4, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: 4));
    }

    [Fact]
    public void SnapToSafeBranchPoint_NonMessageEntry_LikeSessionHeader_IsAlwaysSafe()
    {
        List<TranscriptEntry> entries = [TranscriptEntry.SessionHeader("/repo"), EntryFor(ChatMessage.User("hi"))];

        Assert.Equal(1, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: 1));
    }

    [Fact]
    public void SnapToSafeBranchPoint_ClampsRequestedIndex_ToEntriesRange()
    {
        List<TranscriptEntry> entries = [EntryFor(ChatMessage.User("hi"))];

        Assert.Equal(1, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: 99));
        Assert.Equal(0, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: -5));
    }

    [Fact]
    public void SnapToSafeBranchPoint_SnapsAllTheWayToZero_WhenTheOnlyEntryIsAnUnresolvedToolCall()
    {
        List<TranscriptEntry> entries = [EntryFor(ChatMessage.Assistant([new ToolUseBlock("call_1", "read_file", ValidJsonArgs())]))];

        Assert.Equal(0, CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex: 1));
    }

    // ---- prompt-cache-aware token accounting ----

    [Fact]
    public void EstimatedTokensUsed_CountsCachedPrefixTokens_NotJustBilledInputTokens()
    {
        // Regression test for the interaction between prompt caching and compaction. Anthropic
        // reports input_tokens, cache_creation_input_tokens and cache_read_input_tokens as
        // mutually exclusive counts — input_tokens covers only what follows the last cache
        // breakpoint. Measuring InputTokens alone (as this did before caching was enabled) makes a
        // heavily-cached session look nearly empty: here a window holding 100_050 tokens would
        // have reported 50, so ShouldCompact could never fire no matter how long the session ran.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(
            ChatMessage.Assistant([new TextBlock("hi")]),
            new UsageInfo(InputTokens: 50, OutputTokens: 7, CacheCreationInputTokens: 2_000, CacheReadInputTokens: 98_000));

        Assert.Equal(100_057, CompactionPlanner.EstimatedTokensUsed(transcript));
    }

    [Fact]
    public void ShouldCompact_FiresOnCachedPrefix_WhenBilledInputTokensAloneWouldNot()
    {
        // The same failure expressed at the decision it actually breaks: 50 billed input tokens is
        // nowhere near any threshold, but the context window really is 260K full.
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(
            ChatMessage.Assistant([new TextBlock("hi")]),
            new UsageInfo(InputTokens: 50, OutputTokens: 0, CacheCreationInputTokens: 10_000, CacheReadInputTokens: 250_000));

        Assert.True(CompactionPlanner.ShouldCompact(transcript, DefaultSettings.ForContextWindow(1_000_000)));
    }

    [Fact]
    public void UsageInfo_CacheFieldsDefaultToZero_SoNonCachingProvidersAreUnaffected()
    {
        // Providers that do not report a cache split (OpenAI, Gemini, Local, MeshApi) keep
        // constructing UsageInfo with two arguments; TotalInputTokens must then equal InputTokens.
        var usage = new UsageInfo(1_000, 200);

        Assert.Equal(0, usage.CacheCreationInputTokens);
        Assert.Equal(0, usage.CacheReadInputTokens);
        Assert.Equal(1_000, usage.TotalInputTokens);
    }
}
