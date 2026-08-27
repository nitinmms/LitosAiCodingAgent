using Litos.Tools.Attachments;
using Litos.Tools.FileSystem;

namespace Litos.Gui;

/// <summary>
/// A cheap, in-memory snapshot of relative file/directory paths under a working directory, used
/// to back the live "@"-mention popup (MentionPopup/MainWindow.UpdateMentionPopupFromTypedText).
/// Ported from Litos.Console.Terminal.FileIndex, but backed by IgnoreFilter (same .gitignore-aware
/// exclusion GrepTool's own directory walk uses) instead of a fixed ignored-directory-name list,
/// so build output and dependencies don't clutter results in projects with a .gitignore.
///
/// Built fresh per working directory (mirrors MainWindow.PickSkillAsync's "new SkillDiscovery(...)
/// scoped to _transcript.WorkingDirectory" pattern rather than a DI singleton) and cached until
/// InvalidateCache is called, so repeated "@" triggers within the same session don't re-walk the
/// tree on every keystroke.
/// </summary>
public sealed class FileMentionIndex(string workingDirectory)
{
    private const int MaxEntries = 5000;
    private const int MaxSuggestions = 8;

    /// <summary>
    /// Extensions AttachHandler.AttachPathAsync always rejects (PlainTextMedia.IsBinary would flag
    /// them, but that's a content sniff — this is an extension-only pre-filter so the walk stays a
    /// pure Directory.Enumerate, no per-entry file read). Anything not in this list, not a known
    /// image, and not a known document format is assumed to be source/text and shown; anything in
    /// this list never resolves to an attachable file, so it's excluded from the popup rather than
    /// shown and then failing at attach time.
    /// </summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".bin", ".obj", ".o", ".a", ".lib",
        ".zip", ".tar", ".gz", ".7z", ".rar",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".flac",
        ".iso", ".dmg", ".msi", ".class", ".pyc", ".wasm", ".nupkg",
        ".ttf", ".otf", ".woff", ".woff2",
        ".bmp", ".ico", ".tiff", ".psd",
    };

    private static bool IsAttachable(string relativePath) =>
        ImageMedia.TryGetMimeType(relativePath, out _)
        || PlainTextMedia.IsKnownDocumentFormat(relativePath)
        || !BinaryExtensions.Contains(Path.GetExtension(relativePath));

    private IReadOnlyList<string>? _cache;

    public IReadOnlyList<string> GetOrBuild()
    {
        _cache ??= Build(workingDirectory);
        return _cache;
    }

    public void InvalidateCache() => _cache = null;

    private static IReadOnlyList<string> Build(string root)
    {
        var results = new List<string>();
        var ignoreFilter = IgnoreFilter.ForDirectory(root);
        Walk(root, root, ignoreFilter, results);
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
            if (!isDirectory && !IsAttachable(name))
                continue;

            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            results.Add(isDirectory ? relative + "/" : relative);

            if (isDirectory)
                Walk(root, entry, ignoreFilter, results);
        }
    }

    /// <summary>
    /// Pure filter/ranking logic, split out so it's testable without an Avalonia control tree
    /// (same reasoning as CommandMenuPopup.Filter). Case-insensitive substring match; entries
    /// whose path starts with the typed token rank above ones that merely contain it, ties
    /// broken by shortest path — matches MentionAutocomplete.Filter in Litos.Console so the two
    /// faces behave consistently.
    /// </summary>
    internal static IReadOnlyList<string> Filter(IReadOnlyList<string> index, string token)
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
