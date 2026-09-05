using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;

namespace Litos.Agent.Session;

/// <summary>
/// Performs the actual compaction: summarizes messages before the cut point via a
/// plain (non-tool, non-streamed-to-user) call to the same provider/model the turn
/// is already using, then replaces them in the transcript with one summary message.
/// </summary>
public sealed class Compactor(CompactionSettings settings)
{
    private const string SummarizationPrompt = """
        The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

        Use this exact format:

        ## Goal
        [What is the user trying to accomplish?]

        ## Progress
        - [Key things done so far]

        ## Key Decisions
        - [Notable decisions and why]

        ## Next Steps
        - [What should happen next]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    /// <param name="contextWindowTokens">
    /// The active turn's real (or best-known-fallback) model context window, when the caller
    /// knows it — see CompactionSettings.ForContextWindow. Null keeps this call measured against
    /// the shared CompactionSettings default (200K), which is what every caller that doesn't yet
    /// track a resolved context length per session (Litos.Console) should keep passing.
    /// </param>
    public Task<bool> TryCompactAsync(Transcript transcript, IChatProvider provider, string model, int? contextWindowTokens, CancellationToken ct) =>
        CompactAsync(transcript, provider, model, contextWindowTokens, force: false, ct);

    /// <summary>
    /// User-requested compaction (the /compact command): skips ShouldCompact's context-window
    /// threshold check — the user asked for this regardless of how full the context currently
    /// is — but still runs FindCutPoint, so it still no-ops rather than producing a degenerate
    /// summary when there isn't yet enough history old enough to safely cut.
    /// </summary>
    /// <param name="contextWindowTokens">See TryCompactAsync's parameter of the same name.</param>
    public Task<bool> ForceCompactAsync(Transcript transcript, IChatProvider provider, string model, int? contextWindowTokens, CancellationToken ct) =>
        CompactAsync(transcript, provider, model, contextWindowTokens, force: true, ct);

    private async Task<bool> CompactAsync(Transcript transcript, IChatProvider provider, string model, int? contextWindowTokens, bool force, CancellationToken ct)
    {
        var effectiveSettings = contextWindowTokens is { } window ? settings.ForContextWindow(window) : settings;

        if (!force && !CompactionPlanner.ShouldCompact(transcript, effectiveSettings))
            return false;

        var cutPoint = CompactionPlanner.FindCutPoint(transcript.Messages, effectiveSettings.KeepRecentTokens);
        if (cutPoint is null)
            return false;

        var toSummarize = transcript.Messages.Take(cutPoint.Index).ToList();
        if (toSummarize.Count == 0)
            return false;

        // Recorded on the summary block for internal/diagnostic purposes (not shown to the user —
        // see CompactionOccurred's callers) via EstimatedTokensUsed rather than LastUsage alone,
        // since the latter silently drops the trailing-messages estimate. Not carried forward as
        // the transcript's new baseline usage: see ApplyCompaction's remarks for why that's unsafe.
        var tokensBefore = CompactionPlanner.EstimatedTokensUsed(transcript) ?? 0;
        var summary = await SummarizeAsync(toSummarize, provider, model, ct);

        transcript.ApplyCompaction(cutPoint.Index, ChatMessage.CompactionSummary(summary, tokensBefore));
        return true;
    }

    private static async Task<string> SummarizeAsync(IReadOnlyList<ChatMessage> messages, IChatProvider provider, string model, CancellationToken ct)
    {
        var request = new ChatRequest(
            Messages: [.. messages, ChatMessage.User(SummarizationPrompt)],
            Tools: [],
            Model: model);

        var text = new System.Text.StringBuilder();
        await foreach (var evt in provider.StreamAsync(request, ct))
            if (evt is TextDelta delta)
                text.Append(delta.Text);

        return text.Length > 0 ? text.ToString() : "(compaction summary unavailable)";
    }
}
