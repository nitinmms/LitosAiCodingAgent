using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>
/// What a run started with (blueprint §8.2, m3-architecture.md §7.4): its tool settings, the
/// MCP servers it was given, never their secrets, and how each server connected, and its skills:
/// the library's revision and what became of each repository skill. Kept with the
/// run, so a settings change affects only runs that start afterwards, a resumed run carries on
/// with what it had, and a rework reuses its task's last snapshot.
/// </summary>
public sealed record RunCapabilities
{
    public bool Ptc { get; init; } = true;
    public int ShellTimeoutSeconds { get; init; } = 300;
    public WebSearchAccess WebSearch { get; init; }

    /// <summary>The enabled MCP servers, as they were set up when the snapshot was taken.</summary>
    public IReadOnlyList<McpServerSettings> McpServers { get; init; } = [];

    /// <summary>How each server connected, as the worker reported when it was ready; null until then.</summary>
    public IReadOnlyList<McpServerReport>? McpStatus { get; init; }

    /// <summary>The revision of the skill library it was given; 0 when it was never written.</summary>
    public long SkillLibraryRevision { get; init; }

    /// <summary>The library skills it was given, by name and description.</summary>
    public IReadOnlyList<RunLibrarySkill> LibrarySkills { get; init; } = [];

    public RepositorySkillPolicy RepositorySkillPolicy { get; init; }

    /// <summary>What became of each skill its working copy had, found when its worker started; null until then.</summary>
    public IReadOnlyList<RepositorySkillUse>? RepositorySkills { get; init; }

    /// <summary>A snapshot of the settings as they are now.</summary>
    public static RunCapabilities From(
        ToolSettings tools, WebSearchAccess webSearch, McpSettings mcp, bool ptc, SkillSettings? skills = null, long skillLibraryRevision = 0) => new()
    {
        Ptc = ptc,
        ShellTimeoutSeconds = tools.ShellTimeoutSeconds,
        WebSearch = webSearch,
        McpServers = [.. mcp.Servers.Where(s => s.Enabled)],
        SkillLibraryRevision = skillLibraryRevision,
        LibrarySkills = [.. (skills?.Enabled ?? []).Select(s => new RunLibrarySkill(s.Name, s.Description))],
        RepositorySkillPolicy = skills?.RepositoryPolicy ?? RepositorySkillPolicy.ApproveEach,
    };

    /// <summary>The same capabilities for a rework, whose servers connect afresh and whose working copy is looked at again.</summary>
    public RunCapabilities ForRework() => this with { McpStatus = null, RepositorySkills = null };
}

public sealed record RunLibrarySkill(string Name, string Description);
