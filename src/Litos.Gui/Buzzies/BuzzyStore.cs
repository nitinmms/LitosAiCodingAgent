using System.Text;
using System.Text.Json;

namespace Litos.Gui.Buzzies;

/// <summary>
/// The Buzzies on this machine, one folder each under <c>~/.litos/buzzies/&lt;id&gt;/</c>:
/// <c>buzzy.json</c> (settings), <c>SOUL.md</c> (identity), <c>MEMORY.md</c> (what it was asked
/// to remember) and <c>workspace/</c> (the only folder its file tools can reach). Sessions are kept
/// by the ordinary transcript store under the Buzzy's own <see cref="BuzzyProfile.Owner"/>.
/// No Avalonia types here, so Phase 1 can move this out of Litos.Gui unchanged.
/// </summary>
public sealed class BuzzyStore(string? rootDirectory = null)
{
    public const string ProfileFileName = "buzzy.json";
    public const string SoulFileName = "SOUL.md";
    public const string MemoryFileName = "MEMORY.md";
    public const string WorkspaceFolderName = "workspace";
    public const string SkillsFolderName = "skills";
    public const string DatabaseFileName = "buzzies.db";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const int MaxIdLength = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string RootDirectory { get; } = rootDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "buzzies");

    public string FolderOf(string id) => Path.Combine(RootDirectory, id);
    public string WorkspaceOf(string id) => Path.Combine(FolderOf(id), WorkspaceFolderName);
    public string SoulPathOf(string id) => Path.Combine(FolderOf(id), SoulFileName);
    public string MemoryPathOf(string id) => Path.Combine(FolderOf(id), MemoryFileName);
    public string SkillsOf(string id) => Path.Combine(FolderOf(id), SkillsFolderName);

    /// <summary>The routines and run history database, shared by every Buzzy (Phase 1).</summary>
    public string DatabasePath => Path.Combine(RootDirectory, DatabaseFileName);

    /// <summary>Every readable Buzzy, oldest first so the sidebar order is stable. A folder with a
    /// missing or damaged buzzy.json is skipped rather than failing the whole list.</summary>
    public IReadOnlyList<BuzzyProfile> List()
    {
        if (!Directory.Exists(RootDirectory))
            return [];

        return [.. Directory.EnumerateDirectories(RootDirectory)
            .Select(dir => TryLoad(Path.GetFileName(dir)))
            .OfType<BuzzyProfile>()
            .OrderBy(p => p.CreatedAt)];
    }

    public BuzzyProfile? Get(string id) => IsValidId(id) ? TryLoad(id) : null;

    /// <summary>Creates the Buzzy's folder, SOUL.md, an empty MEMORY.md and its workspace, then
    /// saves buzzy.json last, so a half-created folder never shows up in <see cref="List"/>.</summary>
    public BuzzyProfile Create(BuzzyTemplate template, string name, string color, DateTimeOffset now)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? template.Name : name.Trim();
        var id = UniqueId(Slugify(displayName));
        var profile = new BuzzyProfile(id, displayName, template.Id, color, template.Tools, now);

        Directory.CreateDirectory(WorkspaceOf(id));
        Directory.CreateDirectory(SkillsOf(id));
        File.WriteAllText(SoulPathOf(id), $"# {displayName}\n\n{template.Soul.Trim()}\n", Utf8NoBom);
        File.WriteAllText(MemoryPathOf(id), "# Memory\n\n", Utf8NoBom);
        Save(profile);
        return profile;
    }

    public void Save(BuzzyProfile profile)
    {
        var path = Path.Combine(FolderOf(profile.Id), ProfileFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, JsonOptions), Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Removes the Buzzy's folder: settings, soul, memory and workspace. Its session
    /// transcripts stay in the session store, as deleting history is not something to do silently.</summary>
    public void Delete(string id)
    {
        if (IsValidId(id) && Directory.Exists(FolderOf(id)))
            DeleteTreeWithoutFollowingLinks(FolderOf(id));
    }

    /// <summary>Deletes a folder tree, removing any symlink or junction inside it as a link (never
    /// descending into its target), so deleting a Buzzy can't delete files outside its folder and
    /// doesn't fail on a junction someone placed in its workspace.</summary>
    internal static void DeleteTreeWithoutFollowingLinks(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            var isLink = entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo sub && !isLink)
                DeleteTreeWithoutFollowingLinks(sub.FullName);
            else if (entry is DirectoryInfo)
                Directory.Delete(entry.FullName, recursive: false);
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        Directory.Delete(directory, recursive: false);
    }

    /// <summary>Lower-case letters, digits and single hyphens, so the id is safe both as a folder
    /// name and inside a <see cref="Litos.Agent.Session.SessionOwner"/> value.</summary>
    internal static string Slugify(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxIdLength)
            slug = slug[..MaxIdLength].TrimEnd('-');
        return slug.Length == 0 ? "buzzy" : slug;
    }

    internal static bool IsValidId(string id) =>
        id.Length is > 0 and <= MaxIdLength + 4 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private string UniqueId(string slug)
    {
        var id = slug;
        for (var n = 2; Directory.Exists(FolderOf(id)); n++)
            id = $"{slug}-{n}";
        return id;
    }

    /// <summary>Loads the folder's buzzy.json. Its id must match the folder name, so a copied or
    /// edited file can never make one Buzzy read another's workspace or sessions.</summary>
    private BuzzyProfile? TryLoad(string folderName)
    {
        if (!IsValidId(folderName))
            return null;

        var path = Path.Combine(FolderOf(folderName), ProfileFileName);
        try
        {
            if (!File.Exists(path))
                return null;
            var profile = JsonSerializer.Deserialize<BuzzyProfile>(File.ReadAllText(path), JsonOptions);
            return profile is not null && profile.Id == folderName ? profile : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
