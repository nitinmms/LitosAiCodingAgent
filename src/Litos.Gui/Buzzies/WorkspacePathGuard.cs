namespace Litos.Gui.Buzzies;

/// <summary>
/// Decides whether a path a model asked for stays inside a Buzzy's workspace, or inside a folder
/// the user explicitly granted it (Implement_Buzzies.md, Phase 3). A relative path is resolved
/// against the workspace (never the process's current directory) and the result must be the
/// workspace itself or below it, or a granted folder or below it with a grant that covers the
/// operation. Rejected either way: <c>..</c> traversal out, UNC and device paths, NTFS alternate
/// data streams, and any existing symlink or junction on the way down, since one could point
/// anywhere. A grant is itself resolved through the link check when it is made, so a granted
/// folder that turns out to be (or contain) a link is refused rather than trusted.
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

    /// <summary>What a tool does to a path. Write and Change are both covered by a read-and-write
    /// grant; Read is covered by either kind.</summary>
    public enum Access
    {
        Read,
        Write,
        Change,
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>A path inside the workspace only.</summary>
    public static Resolution Resolve(string workspaceRoot, string requestedPath) =>
        Resolve(workspaceRoot, [], requestedPath, Access.Read);

    /// <summary>A path inside the workspace, or inside a granted folder that allows <paramref name="access"/>.</summary>
    public static Resolution Resolve(
        string workspaceRoot, IReadOnlyList<BuzzyGrant> grants, string requestedPath, Access access)
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

        var inside = IsWithin(root, full);
        if (!inside)
        {
            // Outside the workspace: allowed only through a grant the user made explicitly.
            var grant = MatchingGrant(grants, full);
            if (grant is null)
                return Deny(requestedPath, "it is outside this Buzzy's workspace and no folder grant covers it");
            if (access != Access.Read && !grant.CanWrite)
                return Deny(requestedPath, $"the folder grant for '{grant.Path}' is read-only");
        }

        // A colon past the drive letter names an alternate data stream on NTFS ("a.txt:hidden").
        if (OperatingSystem.IsWindows() && full.IndexOf(':', 2) >= 0)
            return Deny(requestedPath, "it names an alternate data stream");

        // The workspace guard's own link check, and the one for a grant, are both run from the
        // nearest enclosing root: a granted folder may legitimately be reached through its own
        // parent chain, so only the part below the granted folder is inspected.
        var linkRoot = inside ? root : Path.TrimEndingDirectorySeparator(MatchingGrant(grants, full)!.Path);
        if (FindLinkOnPath(linkRoot, full) is { } link)
            return Deny(requestedPath, $"'{Path.GetRelativePath(linkRoot, link)}' is a link, which could lead outside the allowed folders");

        return new Resolution(full, null);
    }

    /// <summary>True when <paramref name="full"/> is <paramref name="root"/> or below it.</summary>
    private static bool IsWithin(string root, string full) =>
        full.Equals(root, PathComparison)
        || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>The deepest grant whose folder contains the path, so a grant on a subfolder is
    /// preferred over one on a parent folder. Null when no grant covers it.</summary>
    private static BuzzyGrant? MatchingGrant(IReadOnlyList<BuzzyGrant> grants, string full) =>
        grants
            .Select(g => new { Grant = g, Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.Path)) })
            .Where(x => IsWithin(x.Root, full))
            .OrderByDescending(x => x.Root.Length)
            .Select(x => x.Grant)
            .FirstOrDefault();

    /// <summary>Canonicalises a folder the user is granting: it must exist, be a folder, and have no
    /// symlink or junction anywhere between its own root and itself. Returns the path to store, or
    /// why it can't be granted. A Buzzy's own folder is never granted: its workspace already is.</summary>
    public static (string? Path, string? Error) CanonicalGrant(string candidate, string buzzyFolder)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return (null, "no folder chosen");

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "it is not a valid path");
        }

        if (!Directory.Exists(full))
            return (null, "it isn't a folder that exists");

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(buzzyFolder));
        if (IsWithin(full, folder) || IsWithin(folder, full))
            return (null, "it is inside this Buzzy's own folder, which it can already reach");

        if (OperatingSystem.IsWindows() && full.IndexOf(':', 2) >= 0)
            return (null, "it names an alternate data stream");

        // Walks from the closest existing ancestor of the drive down to the folder itself, so a
        // link anywhere on the way is refused rather than silently followed.
        var driveRoot = Path.GetPathRoot(full)!;
        if (FindLinkOnPath(Path.TrimEndingDirectorySeparator(driveRoot), full) is { } link)
            return (null, $"'{link}' is a link, which could lead somewhere other than the folder you chose");

        return (full, null);
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
        new(null, $"Access to '{requestedPath}' was refused: {reason}. This Buzzy can use its own workspace (a relative path such as 'notes/today.md') and any folder the user has granted it.");
}
