using Litos.Tools.FileSystem;

namespace Litos.VsCodeHost.Files;

/// <summary>
/// A cheap, in-memory snapshot of relative file/directory paths under a working directory, backing
/// the webview's live "@"-mention dropdown (see FilesEndpoints' /sessions/{id}/mentions and
/// webviewContent.ts's updateMentionMenu). Ported from Litos.Gui's own FileMentionIndex, but
/// rebuilt fresh per request rather than cached: unlike Gui (one process, one working directory at
/// a time), this host can serve several concurrent sessions with different working directories, so
/// a single cached snapshot would need session-keyed invalidation to stay correct — not worth the
/// complexity for a directory walk this cheap. Matches Litos.Console's FileIndex in that respect.
/// </summary>
public static class FileMentionIndex
{
    private const int MaxEntries = 5000;
    private const int MaxSuggestions = 8;

    public static IReadOnlyList<string> Build(string workingDirectory)
    {
        var results = new List<string>();
        var ignoreFilter = IgnoreFilter.ForDirectory(workingDirectory);
        Walk(workingDirectory, workingDirectory, ignoreFilter, results);
        return results;
    }

    private static void Walk(string root, string dir, IgnoreFilter ignoreFilter, List<string> results)
    {
        if (results.Count >= MaxEntries)
            return;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (results.Count >= MaxEntries)
                return;

            var name = Path.GetFileName(entry);
            var isDirectory = Directory.Exists(entry);
            if (isDirectory ? ignoreFilter.IsDirectoryIgnored(name) : ignoreFilter.IsFileIgnored(name))
                continue;

            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            results.Add(isDirectory ? relative + "/" : relative);

            if (isDirectory)
                Walk(root, entry, ignoreFilter, results);
        }
    }

    /// <summary>
    /// Case-insensitive substring match; entries whose path starts with the typed token rank above
    /// ones that merely contain it, ties broken by shortest path — matches Litos.Gui's
    /// FileMentionIndex.Filter and Litos.Console's MentionAutocomplete.Filter so all three faces
    /// behave consistently for the same typed token.
    /// </summary>
    public static IReadOnlyList<string> Filter(IReadOnlyList<string> index, string token)
    {
        if (token.Length == 0)
            return index.Take(MaxSuggestions).ToList();

        return index
            .Where(p => p.Contains(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => !p.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Length)
            .Take(MaxSuggestions)
            .ToList();
    }
}
