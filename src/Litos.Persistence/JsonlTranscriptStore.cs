using System.Runtime.CompilerServices;
using System.Text.Json;
using Litos.Agent.Session;

namespace Litos.Persistence;

public sealed class JsonlTranscriptStore : ITranscriptStore
{
    // Stand-in for a blank/malformed JSONL line so BranchAsync's cut-point walk always has a
    // non-null entry to inspect (Message: null reads as "no pending tool calls", i.e. a safe
    // cut point) instead of needing its own null-handling branch alongside every real entry.
    private static readonly TranscriptEntry NoOpEntry = new("empty", default, null, null, null);

    private readonly string _rootDirectory;

    public JsonlTranscriptStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "sessions");
    }

    public async Task AppendAsync(SessionOwner owner, string sessionId, TranscriptEntry entry, CancellationToken ct)
    {
        var path = ResolveSessionPath(owner, sessionId, mustExist: false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(entry, TranscriptJsonContext.Default.TranscriptEntry);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, ct);
    }

    public async IAsyncEnumerable<TranscriptEntry> ReadAsync(
        SessionOwner owner, string sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = ResolveSessionPath(owner, sessionId, mustExist: false);
        if (!File.Exists(path))
            yield break;

        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var entry = JsonSerializer.Deserialize(line, TranscriptJsonContext.Default.TranscriptEntry);
            if (entry is not null)
                yield return entry;
        }
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionOwner owner, CancellationToken ct)
    {
        var ownerDirectory = ResolveOwnerDirectory(owner);
        if (!Directory.Exists(ownerDirectory))
            return Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(ownerDirectory, "*.jsonl"))
        {
            var lines = File.ReadAllLines(file);
            var entries = lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize(l, TranscriptJsonContext.Default.TranscriptEntry))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            // A genuine user turn, not a Role.User-wrapped tool result (ChatMessage.cs wraps tool
            // results as Role.User too — same distinction Compaction.cs's FindTurnStart makes) —
            // this is what the user actually typed to start the conversation, which identifies
            // the session far better than the session id ever could.
            var firstUserText = entries
                .FirstOrDefault(e => e.Message is { Role: Agent.Messages.Role.User } msg
                    && msg.Content.OfType<Agent.Messages.ToolResultBlock>().Any() is false)
                ?.Message?.Content.OfType<Agent.Messages.TextBlock>().FirstOrDefault()?.Text;

            summaries.Add(new SessionSummary(
                SessionId: Path.GetFileNameWithoutExtension(file),
                CreatedAt: entries.Count > 0 ? entries[0].Timestamp : File.GetCreationTimeUtc(file),
                LastUpdatedAt: entries.Count > 0 ? entries[^1].Timestamp : File.GetLastWriteTimeUtc(file),
                FirstUserMessagePreview: firstUserText is { Length: > 0 } ? firstUserText[..Math.Min(firstUserText.Length, 120)] : null,
                MessageCount: entries.Count));
        }

        return Task.FromResult<IReadOnlyList<SessionSummary>>(
            [.. summaries.OrderByDescending(s => s.LastUpdatedAt)]);
    }

    public async Task<string> BranchAsync(SessionOwner owner, string sourceSessionId, int uptoEntryIndex, CancellationToken ct)
    {
        var sourcePath = ResolveSessionPath(owner, sourceSessionId, mustExist: true);
        var newSessionId = Guid.NewGuid().ToString("n");
        var newPath = ResolveSessionPath(owner, newSessionId, mustExist: false);

        var lines = await File.ReadAllLinesAsync(sourcePath, ct);
        // Tolerates a malformed line anywhere in the file, not just blank ones: BranchAsync only
        // used to ever look at the lines it kept (a straight Take), so a corrupt line elsewhere
        // in the file — including past the eventual cut point — was never a problem. Now that
        // finding a safe cut point means inspecting the whole file, a line that fails to parse
        // must degrade to "no pending tool calls" (NoOpEntry) rather than throwing and failing
        // the branch outright for corruption the caller may not even be branching past.
        var entries = lines.Select(TryDeserializeOrNoOp).ToList();
        var safeUpto = CompactionPlanner.SnapToSafeBranchPoint(entries, uptoEntryIndex);
        await File.WriteAllLinesAsync(newPath, lines.Take(safeUpto), ct);

        return newSessionId;
    }

    private static TranscriptEntry TryDeserializeOrNoOp(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return NoOpEntry;

        try
        {
            return JsonSerializer.Deserialize(line, TranscriptJsonContext.Default.TranscriptEntry) ?? NoOpEntry;
        }
        catch (JsonException)
        {
            return NoOpEntry;
        }
    }

    private string ResolveOwnerDirectory(SessionOwner owner)
    {
        var ownerSegment = ValidateSegment(owner.Value, nameof(owner));
        return Path.Combine(_rootDirectory, ownerSegment);
    }

    private string ResolveSessionPath(SessionOwner owner, string sessionId, bool mustExist)
    {
        var sessionSegment = ValidateSegment(sessionId, nameof(sessionId));
        var path = Path.Combine(ResolveOwnerDirectory(owner), sessionSegment + ".jsonl");
        if (mustExist && !File.Exists(path))
            throw new FileNotFoundException($"Session '{sessionId}' not found for this owner.", path);
        return path;
    }

    /// <summary>
    /// Owner and session identifiers become path segments, so anything that could escape
    /// the intended directory (path separators, "..", empty) is rejected outright rather
    /// than sanitized — a rejected request is safe; a silently-corrected one can still
    /// point somewhere unintended.
    /// </summary>
    private static string ValidateSegment(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value must not be empty.", paramName);
        if (value.Contains("..") || value.Any(c => c is '/' or '\\') || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Value '{value}' is not a valid path segment.", paramName);
        return value;
    }
}
