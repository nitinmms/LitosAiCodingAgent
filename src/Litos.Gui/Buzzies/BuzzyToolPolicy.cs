using Litos.Agent.Tools;
using Litos.Tools.Shell;
using Litos.Tools.Skills;

namespace Litos.Gui.Buzzies;

/// <summary>What a Buzzy's tools are built from, besides the shared per-turn registry.</summary>
/// <param name="ShellFactory">Makes a shell that starts in the given folder. Null reuses the
/// shared shell tool, which starts wherever the process happens to be.</param>
/// <param name="Unattended">A scheduled run, with nobody at the screen: tools that would wait on
/// an approval card (shell) are left out instead of blocking the run.</param>
public sealed record BuzzyToolContext(
    BuzzyStore Store,
    PendingApprovalStore Approvals,
    Func<string, ITool>? ShellFactory = null,
    bool Unattended = false);

/// <summary>
/// Builds the tool registry a Buzzy's turn runs with, from the Gui's full per-turn registry:
/// only the tools its profile grants, each file tool confined to its workspace, the skill tool
/// scoped to its own skills, shell behind an approval card (and absent from unattended runs),
/// plus its own remember tool. Everything else is left out on purpose: MCP tools (an external
/// process can reach anything) and any name this policy doesn't know, so a hand-edited buzzy.json
/// can't grant more than the templates can. PTC is refused separately, since the kernel isn't a
/// registry tool. Nothing here depends on the process's current directory, so a routine can run
/// while the window shows another Buzzy or the plain chat (Implement_Buzzies.md, Phase 1).
/// </summary>
public static class BuzzyToolPolicy
{
    /// <summary>The names a profile may grant. Anything else in buzzy.json is ignored.</summary>
    public static readonly IReadOnlySet<string> Grantable = new HashSet<string>
    {
        BuzzyToolNames.ReadFile, BuzzyToolNames.WriteFile, BuzzyToolNames.EditFile, BuzzyToolNames.ListDirectory,
        BuzzyToolNames.SearchCode, BuzzyToolNames.ShareFile, BuzzyToolNames.WebSearch, BuzzyToolNames.Skill,
        BuzzyToolNames.Artifact, BuzzyToolNames.Remember, BuzzyToolNames.Shell,
    };

    /// <summary>Granted to every Buzzy whatever its profile says: publishing artifacts, with the
    /// built-in artifact-design skill the skill tool loads, works for a Buzzy exactly as it does for
    /// the plain Litos chat.</summary>
    public static readonly IReadOnlySet<string> AlwaysGranted = new HashSet<string>
    {
        BuzzyToolNames.Artifact, BuzzyToolNames.Skill,
    };

    internal const string ShellNote = "It starts in the Buzzy's workspace but is not confined to it.";

    public static ToolRegistry Build(ToolRegistry available, BuzzyProfile profile, BuzzyToolContext context)
    {
        var store = context.Store;
        var workspace = store.WorkspaceOf(profile.Id);
        var granted = profile.Tools.Where(Grantable.Contains).Concat(AlwaysGranted).ToHashSet();
        var tools = new List<ITool>();

        foreach (var name in available.Schemas.Select(s => s.Name))
        {
            if (!granted.Contains(name) || name == BuzzyToolNames.Remember)
                continue;

            var tool = available.Resolve(name);
            if (BuzzyToolNames.PathScoped.Contains(name))
                tool = new WorkspaceScopedTool(tool, workspace);
            else if (name == BuzzyToolNames.Skill)
                tool = new SkillTool(BuzzySkills.DiscoveryFor(store, profile.Id));
            else if (name == BuzzyToolNames.Shell)
            {
                if (context.Unattended)
                    continue;
                tool = new ApprovalRequiredTool(context.ShellFactory?.Invoke(workspace) ?? tool, context.Approvals, profile.Name, ShellNote);
            }
            tools.Add(tool);
        }

        if (granted.Contains(BuzzyToolNames.Remember))
            tools.Add(new RememberTool(store.MemoryPathOf(profile.Id)));

        return new ToolRegistry(tools);
    }
}

/// <summary>Where a Buzzy's skills come from: its own <c>skills/</c> folder first, then the user's
/// skill folders and the built-in ones (artifact-design). Project skills are looked for from its
/// workspace, never from wherever Litos happens to be running.</summary>
public static class BuzzySkills
{
    public static ISkillDiscovery DiscoveryFor(BuzzyStore store, string buzzyId) =>
        new SkillDiscovery(store.WorkspaceOf(buzzyId), [store.SkillsOf(buzzyId), .. Litos.Artifacts.ArtifactSkills.UserRoots()]);
}
