namespace Litos.Gui.Buzzies;

/// <summary>
/// Decides whether a path a model asked for stays inside a Buzzy's workspace. A relative path is
/// resolved against the workspace (never the process's current directory) and the result must be
/// the workspace itself or below it. Rejected: <c>..</c> traversal out of it, absolute paths
/// elsewhere, UNC and device paths, NTFS alternate data streams, and any existing symlink or
/// junction on the way down, since one could point anywhere.
///
/// This is an application-enforced restriction on the tools that go through it, not an OS sandbox
/// (ReadMe_Buzzies.md §1A "Minimal security"): a shell command or a process outside these tools is
/// not confined by it.
/// </summary>
public static class WorkspacePathGuard
{
    public readonly record struct Resolution(string? FullPath, string? Error)
    {
        public bool IsAllowed => FullPath is not null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static Resolution Resolve(string workspaceRoot, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return Deny(requestedPath, "the path is empty");

        string root;
        string full;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(root, requestedPath)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Deny(requestedPath, "it is not a valid path");
        }

        var inside = full.Equals(root, PathComparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
        if (!inside)
            return Deny(requestedPath, "it is outside this Buzzy's workspace");

        // A colon past the drive letter names an alternate data stream on NTFS ("a.txt:hidden").
        if (OperatingSystem.IsWindows() && full.IndexOf(':', 2) >= 0)
            return Deny(requestedPath, "it names an alternate data stream");

        if (FindLinkOnPath(root, full) is { } link)
            return Deny(requestedPath, $"'{Path.GetRelativePath(root, link)}' is a link, which could lead outside the workspace");

        return new Resolution(full, null);
    }

    /// <summary>The first existing symlink, junction or other reparse point between the workspace
    /// and the target (the target included), or null. Components that don't exist yet are fine:
    /// write_file creates them as ordinary folders.</summary>
    private static string? FindLinkOnPath(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".")
            return null;

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
                return null;
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return current;
        }

        return null;
    }

    private static Resolution Deny(string? requestedPath, string reason) =>
        new(null, $"Access to '{requestedPath}' was refused: {reason}. This Buzzy can only use files inside its own workspace; use a relative path such as 'notes/today.md'.");
}
