using Litos.Agent.Messages;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.Agent.Session;

public enum ContextCategory { SystemPrompt, ToolSchemas, Memory, Skills, History, ToolResults, Images, CompactionSummary }

public sealed record ContextBreakdownSubItem(string Label, int EstimatedTokens);

public sealed record ContextBreakdownEntry(ContextCategory Category, string Label, int EstimatedTokens, IReadOnlyList<ContextBreakdownSubItem>? SubItems = null);

/// <summary>
/// Entries are always returned in ContextCategory declaration order and only for categories
/// with at least one token — a category that contributes nothing (e.g. no images this session)
/// is omitted rather than shown as a zero-width bar segment.
/// </summary>
public sealed record ContextBreakdownSnapshot(IReadOnlyList<ContextBreakdownEntry> Entries, int TotalEstimatedTokens, int? LastRealUsageTokens);

/// <summary>
/// Estimates what's actually consuming the context window right now, broken into categories a
/// user can reason about — system prompt, memory (AGENTS.md/CLAUDE.md), skills catalog, tool
/// schemas, conversation history, tool results (sub-grouped by tool name), images, and any
/// compaction summary. Backs the GUI's "View Context" modal (opened by double-clicking the
/// status-bar context meter, see MainWindow.ShowContextBreakdownAsync).
///
/// Every count is chars/4, the same heuristic CompactionPlanner already uses (no real tokenizer
/// exists anywhere in this codebase) — but here it's scaled so the categories sum to the last
/// real provider-reported usage (Transcript.LastUsage) when one is available, the same
/// calibration trick CompactionPlanner.EstimatedTokensUsed already relies on. This keeps the
/// breakdown honest relative to what was actually billed even though the per-category split
/// itself remains an estimate.
///
/// This is a live, current-turn snapshot only — not a historical "what was in context at turn
/// N" reconstruction. Two things make that harder than it looks and are deliberately out of
/// scope here: compaction destructively drops pre-compaction messages from Transcript (see
/// Transcript.ApplyCompaction), and the system prompt is rebuilt fresh per turn from
/// whatever AGENTS.md content and tool list exist right now, not persisted per turn.
/// </summary>
public static class ContextBreakdown
{
    public static ContextBreakdownSnapshot Compute(Transcript transcript, SystemPromptSections? systemPrompt, IReadOnlyList<ToolSchema> tools)
    {
        var entries = new List<ContextBreakdownEntry>();

        if (systemPrompt is not null)
        {
            AddIfNonZero(entries, ContextCategory.SystemPrompt, "System prompt", EstimateTokens(systemPrompt.Identity) + EstimateTokens(systemPrompt.Guidelines) + EstimateTokens(systemPrompt.Footer));
            AddIfNonZero(entries, ContextCategory.Memory, "Memory (AGENTS.md/CLAUDE.md)", systemPrompt.Instructions.Sum(f => EstimateTokens(f.Content)));
            AddIfNonZero(entries, ContextCategory.Skills, "Skills catalog", EstimateTokens(systemPrompt.SkillsCatalog ?? ""));
        }

        AddIfNonZero(entries, ContextCategory.ToolSchemas, "Tool schemas", tools.Sum(t => (t.Name.Length + t.Description.Length + t.ParameterSchema.GetRawText().Length) / 4));

        var toolNameByCallId = BuildToolNameLookup(transcript.Messages);

        var historyTokens = 0;
        var imageTokens = 0;
        var compactionTokens = 0;
        var toolResultTokensByName = new Dictionary<string, int>();

        foreach (var message in transcript.Messages)
        {
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock t:
                        historyTokens += t.Text.Length / 4;
                        break;
                    case ToolUseBlock u:
                        historyTokens += (u.ToolName.Length + u.Arguments.GetRawText().Length) / 4;
                        break;
                    case ToolResultBlock r:
                        var name = toolNameByCallId.GetValueOrDefault(r.CallId, "(unknown tool)");
                        toolResultTokensByName[name] = toolResultTokensByName.GetValueOrDefault(name) + r.Text.Length / 4;
                        break;
                    case ImageBlock:
                        imageTokens += CompactionPlanner.EstimateChars(new ChatMessage(message.Role, [block])) / 4;
                        break;
                    case CompactionSummaryBlock c:
                        compactionTokens += c.Summary.Length / 4;
                        break;
                }
            }
        }

        AddIfNonZero(entries, ContextCategory.History, "Conversation history", historyTokens);

        if (toolResultTokensByName.Count > 0)
        {
            var subItems = toolResultTokensByName
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new ContextBreakdownSubItem(kv.Key, kv.Value))
                .ToList();
            entries.Add(new ContextBreakdownEntry(ContextCategory.ToolResults, "Tool results", subItems.Sum(s => s.EstimatedTokens), subItems));
        }

        AddIfNonZero(entries, ContextCategory.Images, "Images", imageTokens);
        AddIfNonZero(entries, ContextCategory.CompactionSummary, "Compaction summary", compactionTokens);

        var rawTotal = entries.Sum(e => e.EstimatedTokens);
        var lastUsage = transcript.LastUsage;
        var scaled = Scale(entries, rawTotal, lastUsage);

        return new ContextBreakdownSnapshot(scaled, scaled.Sum(e => e.EstimatedTokens), lastUsage is null ? null : lastUsage.InputTokens + lastUsage.OutputTokens);
    }

    /// <summary>
    /// Maps each ToolResultBlock's CallId back to the ToolName of the ToolUseBlock that
    /// requested it, so tool-result tokens can be sub-grouped by originating tool. A
    /// ToolResultBlock with no matching ToolUseBlock (shouldn't normally happen, but the
    /// transcript is walked defensively) falls back to "(unknown tool)" in the caller.
    /// </summary>
    private static Dictionary<string, string> BuildToolNameLookup(IReadOnlyList<ChatMessage> messages) =>
        messages
            .SelectMany(m => m.Content.OfType<ToolUseBlock>())
            .GroupBy(u => u.CallId)
            .ToDictionary(g => g.Key, g => g.First().ToolName);

    private static int EstimateTokens(string text) => text.Length / 4;

    private static void AddIfNonZero(List<ContextBreakdownEntry> entries, ContextCategory category, string label, int tokens)
    {
        if (tokens > 0)
            entries.Add(new ContextBreakdownEntry(category, label, tokens));
    }

    /// <summary>
    /// Scales every entry (and its sub-items) so the total matches real provider-reported
    /// usage exactly, preserving each entry's proportional share of the raw estimate. Left
    /// unscaled (raw chars/4) when there's no real usage yet to calibrate against.
    /// </summary>
    private static List<ContextBreakdownEntry> Scale(List<ContextBreakdownEntry> entries, int rawTotal, UsageInfo? lastUsage)
    {
        if (lastUsage is null || rawTotal == 0)
            return entries;

        var target = lastUsage.InputTokens + lastUsage.OutputTokens;
        var factor = (double)target / rawTotal;

        return entries
            .Select(e => e with
            {
                EstimatedTokens = (int)Math.Round(e.EstimatedTokens * factor),
                SubItems = e.SubItems?.Select(s => s with { EstimatedTokens = (int)Math.Round(s.EstimatedTokens * factor) }).ToList(),
            })
            .ToList();
    }
}
