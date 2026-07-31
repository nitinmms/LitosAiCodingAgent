namespace Litos.Console.Terminal;

/// <summary>
/// A cheap, in-memory snapshot of relative file/directory paths under a root, used to
/// back the live @-mention picker in MentionAutocomplete/Composer. Rebuilt once per prompt
/// call (not cached across turns) so it reflects files created/deleted mid-session.
/// </summary>
public static class FileIndex
{
    private static readonly string[] IgnoredDirNames = [".git", "bin", "obj", "node_modules", ".vs", ".idea"];

    public static IReadOnlyList<string> BuildRelativePaths(string root, int maxEntries = 5000)
    {
        var results = new List<string>();
        Walk(root, root, results, maxEntries);
        return results;
    }

    private static void Walk(string root, string dir, List<string> results, int maxEntries)
    {
        if (results.Count >= maxEntries)
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
            if (results.Count >= maxEntries)
                return;

            var name = Path.GetFileName(entry);
            var isDirectory = Directory.Exists(entry);
            if (isDirectory && IgnoredDirNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            results.Add(isDirectory ? relative + "/" : relative);

            if (isDirectory)
                Walk(root, entry, results, maxEntries);
        }
    }
}
