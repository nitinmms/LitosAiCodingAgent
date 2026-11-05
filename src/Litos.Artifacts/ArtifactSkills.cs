using System.Text;
using Litos.Tools.Skills;

namespace Litos.Artifacts;

/// <summary>
/// Ships the built-in <c>artifact-design</c> skill. SkillTool reads a skill's body from disk, and
/// Litos.VsCodeHost is a single-file exe with no loose content files, so the skill is an embedded
/// resource written out to <c>~/.litos/builtin-skills/artifact-design/SKILL.md</c> at startup.
///
/// The built-in root goes LAST in the user roots — lowest priority — so a user's or project's own
/// <c>artifact-design</c> skill overrides it. It is deliberately not part of
/// SkillDiscovery.DefaultUserRoots(): only a host that registers the artifact tool should advertise
/// a skill about it, and every other face (Gui, Console) keeps the default roots. See
/// ReadMe_ArtifactsFeature.md §5.
/// </summary>
public static class ArtifactSkills
{
    public const string SkillName = "artifact-design";

    private const string ResourceName = "Litos.Artifacts.Skills.artifact-design.SKILL.md";
    private const string SkillFileName = "SKILL.md";

    public static string DefaultBuiltInRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "builtin-skills");

    /// <summary>The embedded SKILL.md, frontmatter included.</summary>
    public static string EmbeddedSkillMarkdown { get; } = ReadEmbedded();

    private const string DesignRulesStart = "## Where the page runs";

    /// <summary>
    /// The skill's design rules alone — what the sandbox allows, external resources, the page
    /// contract and design effort — without its frontmatter, introduction or file-based workflow.
    /// For a face whose agent publishes differently (the software factory's chat passes the page
    /// inline and has no write tools), which states its own workflow and appends these.
    /// </summary>
    public static string DesignRules { get; } = ExtractDesignRules(EmbeddedSkillMarkdown);

    internal static string ExtractDesignRules(string skillMarkdown)
    {
        var start = skillMarkdown.IndexOf(DesignRulesStart, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"The artifact-design skill has no '{DesignRulesStart}' section to take the design rules from.");
        return skillMarkdown[start..].Replace("\r\n", "\n").Trim();
    }

    /// <summary>
    /// Writes the embedded skill under <paramref name="builtInRoot"/> unless an identical copy is
    /// already there, so a newer host binary refreshes the text and an unchanged one doesn't touch
    /// the file. Never throws for I/O: a missing skill must not stop the host starting — the
    /// artifact tool still works, only without its design guidance.
    /// </summary>
    /// <returns>The skill file's path, or null when it could not be written.</returns>
    public static string? EnsureInstalled(string? builtInRoot = null)
    {
        var path = Path.Combine(builtInRoot ?? DefaultBuiltInRoot, SkillName, SkillFileName);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == EmbeddedSkillMarkdown)
                return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = $"{path}.{Guid.NewGuid():n}.tmp";
            File.WriteAllText(temp, EmbeddedSkillMarkdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The default user skill roots plus the built-in root, lowest priority last.</summary>
    public static IReadOnlyList<string> UserRoots(string? builtInRoot = null) =>
        [.. SkillDiscovery.DefaultUserRoots(), builtInRoot ?? DefaultBuiltInRoot];

    private static string ReadEmbedded()
    {
        using var stream = typeof(ArtifactSkills).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing from Litos.Artifacts.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
