using Litos.Agent.Messages;

namespace Litos.Agent.Session;

public sealed record CompactionSettings(bool Enabled = true, int ContextWindowTokens = 200_000, int ReserveTokens = 16_000, int KeepRecentTokens = 20_000);

public sealed record CutPoint(int Index, bool IsSplitTurn, int TurnStartIndex);

/// <summary>
/// Pure functions for deciding whether/where to compact — no I/O, no LLM calls (that
/// part needs a provider and lives in Compactor). Mirrors pi's compaction.ts: never
/// cut between a tool_use message and its tool_result, prefer cutting at user/assistant
/// turn boundaries, and only fire once the *real* usage from the last assistant
/// response is within ReserveTokens of the model's context window.
/// </summary>
public static class CompactionPlanner
{
    private const int EstimatedImageChars = 4800;

    /// <summary>
    /// True if cutting a transcript immediately before this message is safe — i.e. it isn't
    /// a tool result, which must stay glued to the tool_use message that produced it. Shared
    /// by FindCutPoint below and by ITranscriptStore.BranchAsync, which must not truncate a
    /// session mid-tool-call either (a dangling tool_use with no result is rejected by most
    /// providers on the next request).
    /// </summary>
    public static bool IsValidCutPoint(ChatMessage message) =>
        message.Content.OfType<ToolResultBlock>().Any() is false;

    public static bool ShouldCompact(Transcript transcript, CompactionSettings settings)
    {
        if (!settings.Enabled)
            return false;

        var estimatedTokens = EstimatedTokensUsed(transcript);
        if (estimatedTokens is null)
            return false;

        return estimatedTokens > settings.ContextWindowTokens - settings.ReserveTokens;
    }

    /// <summary>
    /// Real usage from the last assistant response plus a char/4 estimate of trailing messages
    /// sent since — the same figure a context-usage indicator needs, not just ShouldCompact.
    /// Null when no real usage has ever been recorded (nothing to estimate from).
    /// </summary>
    public static int? EstimatedTokensUsed(Transcript transcript)
    {
        var lastUsage = transcript.LastUsage;
        if (lastUsage is null)
            return null;

        var trailingChars = transcript.Messages
            .Skip(transcript.Messages.Count - transcript.MessagesSinceLastUsage)
            .Sum(EstimateChars);
        return lastUsage.InputTokens + lastUsage.OutputTokens + trailingChars / 4;
    }

    /// <summary>
    /// Walks backwards accumulating estimated tokens until keepRecentTokens is reached,
    /// then snaps forward to the nearest valid cut point (a user or assistant message —
    /// never a tool result, which must stay glued to the tool_use that produced it).
    /// </summary>
    public static CutPoint? FindCutPoint(IReadOnlyList<ChatMessage> messages, int keepRecentTokens)
    {
        var validCutPoints = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (IsValidCutPoint(messages[i]))
                validCutPoints.Add(i);
        }

        if (validCutPoints.Count == 0)
            return null;

        var accumulated = 0;
        var cutIndex = validCutPoints[0];
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            accumulated += EstimateChars(messages[i]) / 4;
            if (accumulated < keepRecentTokens)
                continue;

            cutIndex = validCutPoints.FirstOrDefault(c => c >= i, validCutPoints[^1]);
            break;
        }

        if (cutIndex == 0)
            return null; // nothing old enough to cut

        var isUserTurnStart = messages[cutIndex].Role == Role.User && messages[cutIndex].Content.OfType<ToolResultBlock>().Any() is false;
        var turnStartIndex = isUserTurnStart ? cutIndex : FindTurnStart(messages, cutIndex);

        return new CutPoint(cutIndex, IsSplitTurn: !isUserTurnStart, turnStartIndex);
    }

    /// <summary>
    /// Walks a caller-requested "keep the first N transcript entries" index backwards past any
    /// assistant message whose ToolUseBlock call(s) wouldn't have a matching ToolResultBlock kept
    /// alongside it — an unpaired tool_use is rejected by most providers on the next request. Used
    /// by ITranscriptStore.BranchAsync (JsonlTranscriptStore and FakeTranscriptStore both call
    /// this rather than duplicating the walk) so branching a session can never truncate
    /// mid-tool-call. Operates on TranscriptEntry rather than ChatMessage because the entry index
    /// BranchAsync takes counts every JSONL line, including the non-message "session" header entry
    /// — a requested index that already lands after a completed tool round (or isn't one at all)
    /// is returned unchanged.
    /// </summary>
    public static int SnapToSafeBranchPoint(IReadOnlyList<TranscriptEntry> entries, int uptoEntryIndex)
    {
        var upto = Math.Clamp(uptoEntryIndex, 0, entries.Count);
        while (upto > 0)
        {
            // Every ToolUseBlock call id anywhere among the kept entries — not just the last
            // one — needs its ToolResultBlock kept too: a parallel tool-call round can leave an
            // earlier call's result stranded even when the entry landing exactly at the cut
            // isn't itself a tool_use.
            var kept = entries.Take(upto).ToList();
            var callIds = kept.SelectMany(e => e.Message?.Content.OfType<ToolUseBlock>() ?? []).Select(t => t.CallId);
            var resultCallIds = kept.SelectMany(e => e.Message?.Content.OfType<ToolResultBlock>() ?? []).Select(r => r.CallId).ToHashSet();

            if (callIds.All(resultCallIds.Contains))
                return upto;

            upto--;
        }

        return upto;
    }

    private static int FindTurnStart(IReadOnlyList<ChatMessage> messages, int fromIndex)
    {
        for (var i = fromIndex; i >= 0; i--)
            if (messages[i].Role == Role.User && messages[i].Content.OfType<ToolResultBlock>().Any() is false)
                return i;
        return 0;
    }

    private static int EstimateChars(ChatMessage message) =>
        message.Content.Sum(block => block switch
        {
            TextBlock t => t.Text.Length,
            ToolUseBlock u => u.ToolName.Length + u.Arguments.GetRawText().Length,
            ToolResultBlock r => r.Text.Length,
            CompactionSummaryBlock c => c.Summary.Length,
            ImageBlock => EstimatedImageChars,
            _ => 0,
        });
}
