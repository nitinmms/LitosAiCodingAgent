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

    /// <summary>Folder-per-session layout (§4.5/§8.5): "{sessionId}/transcript.jsonl", a sibling of "{sessionId}/scratch/".</summary>
    private const string TranscriptFileName = "transcript.jsonl";

    private readonly string _rootDirectory;

    public JsonlTranscriptStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "sessions");
    }

    public async Task AppendAsync(SessionOwner owner, string sessionId, TranscriptEntry entry, CancellationToken ct)
    {
        MigrateLegacyIfPresent(owner, sessionId);
        var path = ResolveWriteSessionPath(owner, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(entry, TranscriptJsonContext.Default.TranscriptEntry);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, ct);
    }

    public async IAsyncEnumerable<TranscriptEntry> ReadAsync(
        SessionOwner owner, string sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = ResolveReadSessionPath(owner, sessionId);
        if (path is null || !File.Exists(path))
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

        // Enumerates both shapes and de-duplicates by session id: legacy flat "{sessionId}.jsonl"
        // files directly under the owner directory, and "{sessionId}/transcript.jsonl" one level
        // down (§8.5) — a session migrates itself the next time anyone writes to it (AppendAsync),
        // so both shapes can coexist indefinitely for sessions nobody has touched since the move.
        var files = Directory.EnumerateFiles(ownerDirectory, "*.jsonl")
            .Select(f => (SessionId: Path.GetFileNameWithoutExtension(f), Path: f))
            .Concat(Directory.EnumerateDirectories(ownerDirectory)
                .Select(d => (SessionId: Path.GetFileName(d), Path: Path.Combine(d, TranscriptFileName)))
                .Where(t => File.Exists(t.Path)))
            .DistinctBy(t => t.SessionId);

        var summaries = new List<SessionSummary>();
        foreach (var (sessionId, file) in files)
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
                SessionId: sessionId,
                CreatedAt: entries.Count > 0 ? entries[0].Timestamp : File.GetCreationTimeUtc(file),
                LastUpdatedAt: entries.Count > 0 ? entries[^1].Timestamp : File.GetLastWriteTimeUtc(file),
                FirstUserMessagePreview: firstUserText is { Length: > 0 } ? firstUserText[..Math.Min(firstUserText.Length, 120)] : null,
                MessageCount: entries.Count));
        }

        return Task.FromResult<IReadOnlyList<SessionSummary>>(
            [.. summaries.OrderByDescending(s => s.LastUpdatedAt)]);
    }

    /// <summary>
    /// {root}/{owner}/{sessionId}/scratch — composes with the store's existing _rootDirectory
    /// convention rather than introducing a second, potentially-divergent path scheme (§8.5). Does
    /// not create the directory; KernelSession creates it lazily on first use.
    /// </summary>
    public string GetScratchDirectory(SessionOwner owner, string sessionId)
    {
        var sessionSegment = ValidateSegment(sessionId, nameof(sessionId));
        return Path.Combine(ResolveOwnerDirectory(owner), sessionSegment, "scratch");
    }

    public async Task<string> BranchAsync(SessionOwner owner, string sourceSessionId, int uptoEntryIndex, CancellationToken ct)
    {
        var sourcePath = ResolveReadSessionPath(owner, sourceSessionId)
            ?? throw new FileNotFoundException($"Session '{sourceSessionId}' not found for this owner.");
        var newSessionId = Guid.NewGuid().ToString("n");
        var newPath = ResolveWriteSessionPath(owner, newSessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);

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

    private string LegacyFlatPath(SessionOwner owner, string sessionId) =>
        Path.Combine(ResolveOwnerDirectory(owner), ValidateSegment(sessionId, nameof(sessionId)) + ".jsonl");

    private string FolderPath(SessionOwner owner, string sessionId) =>
        Path.Combine(ResolveOwnerDirectory(owner), ValidateSegment(sessionId, nameof(sessionId)), TranscriptFileName);

    /// <summary>
    /// Read path: checks the new "{sessionId}/transcript.jsonl" form first, falls back to the
    /// legacy flat "{sessionId}.jsonl" form if absent (§8.5) — so an old session that's never been
    /// written to since the migration still resolves correctly with no forced migration step.
    /// Returns null if neither form exists.
    /// </summary>
    private string? ResolveReadSessionPath(SessionOwner owner, string sessionId)
    {
        var folderForm = FolderPath(owner, sessionId);
        if (File.Exists(folderForm))
            return folderForm;
        var legacyForm = LegacyFlatPath(owner, sessionId);
        return File.Exists(legacyForm) ? legacyForm : null;
    }

    /// <summary>Write path always targets the new folder form (§8.5) — MigrateLegacyIfPresent must be called first by any caller that also needs the legacy file's prior content preserved.</summary>
    private string ResolveWriteSessionPath(SessionOwner owner, string sessionId) => FolderPath(owner, sessionId);

    /// <summary>
    /// A session migrates itself the next time anyone writes to it — no separate migration
    /// command, no batch first-run scan, no partial-migration recovery story needed (§8.5). A
    /// session never touched again simply stays in its legacy flat form forever, which
    /// ResolveReadSessionPath's fallback still serves correctly.
    /// </summary>
    private void MigrateLegacyIfPresent(SessionOwner owner, string sessionId)
    {
        var legacyPath = LegacyFlatPath(owner, sessionId);
        if (!File.Exists(legacyPath))
            return;
        var folderPath = FolderPath(owner, sessionId);
        if (File.Exists(folderPath))
            return; // Already migrated (or a name collision) — never overwrite.
        Directory.CreateDirectory(Path.GetDirectoryName(folderPath)!);
        File.Move(legacyPath, folderPath);
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
