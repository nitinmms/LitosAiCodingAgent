using Litos.Agent.Messages;
using Litos.Agent.Streaming;

namespace Litos.Agent.Session;

public sealed class Transcript
{
    private readonly List<ChatMessage> _messages = [];
    private readonly Dictionary<int, UsageInfo> _usageByIndex = [];

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public ChatMessage Last => _messages[^1];

    /// <summary>
    /// The directory this session is scoped to. Set once when the session is created and
    /// carried with it across /resume, rather than re-read from the live process's ambient
    /// CWD — see ReadMe_AgentDesign.md §4.5.1.
    /// </summary>
    public string? WorkingDirectory { get; private set; }

    public void Append(ChatMessage message, UsageInfo? usage = null)
    {
        _messages.Add(message);
        if (usage is not null)
            _usageByIndex[_messages.Count - 1] = usage;
    }

    /// <summary>
    /// Real usage reported by the most recent assistant message, if any — the same
    /// "walk backwards to the last assistant usage" lookup compaction needs to know
    /// how close the conversation actually is to the model's context window.
    /// </summary>
    public UsageInfo? LastUsage
    {
        get
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
                if (_usageByIndex.TryGetValue(i, out var usage))
                    return usage;
            return null;
        }
    }

    /// <summary>Number of messages appended after the one carrying LastUsage — these have no real usage yet.</summary>
    public int MessagesSinceLastUsage
    {
        get
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
                if (_usageByIndex.ContainsKey(i))
                    return _messages.Count - 1 - i;
            return _messages.Count;
        }
    }

    /// <summary>
    /// Replaces the messages before cutIndex (exclusive) with a single compaction summary message.
    /// Deliberately does NOT carry forward an estimated baseline usage for the summary message:
    /// LastUsage is real, cumulative, model-reported usage covering however much of the original
    /// (pre-compaction) conversation it was billed against, while everything this method has to
    /// work with post-cut is a chars/4 estimate over arbitrary, possibly very different, surviving
    /// text. Netting one against the other (a prior version of this method tried exactly that)
    /// produces numbers that can be wildly wrong whenever LastUsage's message sits far from the
    /// cut point — e.g. after /branch, where dozens of kept messages can separate them. Leaving
    /// LastUsage null here means the context-usage meter shows "no usage reported yet" until the
    /// next real assistant reply — a brief, honest blank beats a confidently wrong number.
    /// </summary>
    public void ApplyCompaction(int cutIndex, ChatMessage summaryMessage)
    {
        var kept = _messages.Skip(cutIndex).ToList();
        _messages.Clear();
        _usageByIndex.Clear();
        _messages.Add(summaryMessage);
        _messages.AddRange(kept);
    }

    /// <summary>Creates a brand-new, empty session scoped to the given working directory.</summary>
    public static Transcript CreateNew(string workingDirectory) =>
        new() { WorkingDirectory = workingDirectory };

    /// <summary>
    /// Replays a session's JSONL entries into a fresh Transcript. The JSONL itself is
    /// append-only — compaction never rewrites or deletes prior lines (see Compactor and
    /// ApplyCompaction's remarks) — so a message carrying a CompactionSummaryBlock is a
    /// checkpoint marker, not just another entry: replay discards every message
    /// accumulated so far and restarts from the summary, mirroring what ApplyCompaction
    /// already does to the live in-memory Transcript at compaction time. Without this, a
    /// resumed session would replay the full pre-compaction history AND the summary,
    /// which is worse than not compacting at all. Mirrors pi's compaction.ts, which skips
    /// straight to its own checkpoint marker (firstKeptEntryId) on reload instead of
    /// replaying everything the checkpoint already summarized.
    ///
    /// A session compacted more than once has one such checkpoint per compaction; only
    /// the latest is kept (each is discarded in turn as a later one is reached), same as
    /// ApplyCompaction discarding an earlier summary when a later compaction's cut point
    /// passes over it.
    /// </summary>
    public static async Task<Transcript> LoadAsync(ITranscriptStore store, SessionOwner owner, string sessionId, CancellationToken ct)
    {
        var transcript = new Transcript();
        await foreach (var entry in store.ReadAsync(owner, sessionId, ct))
        {
            if (entry.Kind == "session" && entry.WorkingDirectory is not null)
                transcript.WorkingDirectory = entry.WorkingDirectory;
            else if (entry.Message is not null && entry.Message.Content.OfType<CompactionSummaryBlock>().Any())
                transcript.RestartFromCheckpoint(entry.Message);
            else if (entry.Message is not null)
                transcript.Append(entry.Message, entry.Usage);
        }
        return transcript;
    }

    /// <summary>
    /// Discards every message replayed so far and starts over from a compaction-summary
    /// checkpoint — the replay-time counterpart to ApplyCompaction. See LoadAsync's remarks.
    /// </summary>
    private void RestartFromCheckpoint(ChatMessage summaryMessage)
    {
        _messages.Clear();
        _usageByIndex.Clear();
        _messages.Add(summaryMessage);
    }
}
