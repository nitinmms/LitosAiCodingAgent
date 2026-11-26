using Litos.Agent.Tools;
using Litos.Mail;
using Litos.Tools.Attachments;
using Litos.Tools.Shell;
using Litos.Tools.Skills;

namespace Litos.Gui.Buzzies;

/// <summary>What a Buzzy's tools are built from, besides the shared per-turn registry.</summary>
/// <param name="ShellFactory">Makes a shell that starts in the given folder. Null reuses the
/// shared shell tool, which starts wherever the process happens to be.</param>
/// <param name="Unattended">A scheduled run, with nobody at the screen: tools that would wait on
/// an approval card (shell) are left out instead of blocking the run, and write_file can't replace
/// files an earlier run left (<see cref="NoOverwriteTool"/>).</param>
/// <param name="Remember">Where remembered notes go; null saves them straight to MEMORY.md as from a chat.</param>
/// <param name="Mail">The Gmail connector, when the user has connected a mailbox. Null (or not
/// connected) means a Buzzy granted mail tools gets none, rather than five tools that all fail
/// (Implement_Buzzies.md, Phase 4).</param>
/// <param name="MailApproval">How a mail tool asks the user before fetching an attachment or saving
/// a draft; the Gui supplies the same in-transcript card shell and MCP use.</param>
/// <param name="Converter">Turns attachment bytes into text. Optional: without one, a document
/// attachment says it could not be read rather than dumping binary into the prompt.</param>
public sealed record BuzzyToolContext(
    BuzzyStore Store,
    PendingApprovalStore Approvals,
    Func<string, ITool>? ShellFactory = null,
    bool Unattended = false,
    RememberMode? Remember = null,
    MailConnection? Mail = null,
    IMailApproval? MailApproval = null,
    IAttachmentConverter? Converter = null);

/// <summary>
/// Builds the tool registry a Buzzy's turn runs with, from the Gui's full per-turn registry:
/// only the tools its profile grants, each file tool confined to its workspace, the skill tool
/// scoped to its own skills, shell behind an approval card (and absent from unattended runs),
/// plus its own remember tool. Everything else is left out on purpose: MCP tools (an external
/// process can reach anything) and any name this policy doesn't know, so a hand-edited buzzy.json
/// can't grant more than the templates can. PTC is refused separately, since the kernel isn't a
/// registry tool. Nothing here depends on the process's current directory, so a routine can run
/// while the window shows another Buzzy or the plain chat (Implement_Buzzies.md, Phase 1).
///
/// Mail tools are the one set that isn't resolved from the shared registry: the Gmail connector's
/// five tools are built here by <see cref="MailConnection"/>, and only when a mailbox is connected.
/// An unattended run gets the read-only three — downloading an attachment or saving a draft each
/// need a person's approval, and a routine must never wait on a card that nobody will answer.
/// </summary>
public static class BuzzyToolPolicy
{
    /// <summary>The names a profile may grant. Anything else in buzzy.json is ignored.</summary>
    public static readonly IReadOnlySet<string> Grantable = new HashSet<string>
    {
        BuzzyToolNames.ReadFile, BuzzyToolNames.WriteFile, BuzzyToolNames.EditFile, BuzzyToolNames.ListDirectory,
        BuzzyToolNames.SearchCode, BuzzyToolNames.ShareFile, BuzzyToolNames.WebSearch, BuzzyToolNames.Skill,
        BuzzyToolNames.Artifact, BuzzyToolNames.Remember, BuzzyToolNames.Shell,
        BuzzyToolNames.MailSearch, BuzzyToolNames.MailRead, BuzzyToolNames.MailSummarise,
        BuzzyToolNames.MailDownloadAttachment, BuzzyToolNames.MailDraft,
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
        // Folder grants the user made explicitly. Unattended runs keep the read ones only: a routine
        // must not change files in a folder nobody is watching (Implement_Buzzies.md, Phase 3).
        var folders = context.Unattended ? [.. profile.Folders.Where(g => !g.CanWrite)] : profile.Folders;
        var tools = new List<ITool>();

        foreach (var name in available.Schemas.Select(s => s.Name))
        {
            if (!granted.Contains(name) || name == BuzzyToolNames.Remember || BuzzyToolNames.Mail.Contains(name))
                continue;

            var tool = available.Resolve(name);
            if (BuzzyToolNames.PathScoped.Contains(name))
                tool = new WorkspaceScopedTool(
                    context.Unattended && name == BuzzyToolNames.WriteFile ? new NoOverwriteTool(tool) : tool,
                    workspace, folders);
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
            tools.Add(new RememberTool(profile.Id, store.MemoryPathOf(profile.Id), context.Remember));

        tools.AddRange(MailTools(profile, granted, context));
        return new ToolRegistry(tools);
    }

    /// <summary>The mail tools a Buzzy is allowed, from a connected mailbox, or none. A grant in
    /// buzzy.json is necessary but not sufficient: with no mailbox connected the tool isn't offered
    /// at all, and drafting additionally needs the compose scope the user actually granted.</summary>
    private static IEnumerable<ITool> MailTools(BuzzyProfile profile, HashSet<string> granted, BuzzyToolContext context)
    {
        var wanted = BuzzyToolNames.Mail.Where(granted.Contains).ToList();
        if (wanted.Count == 0 || context.Mail is null || !context.Mail.IsConnected)
            return [];

        var approval = context.MailApproval;
        var allowance = context.Unattended
            ? wanted.Where(MailToolNames.ReadOnly.Contains).ToList()
            : wanted;
        if (allowance.Count == 0)
            return [];

        if (!context.Unattended && context.Mail.Info() is { CanDraft: false })
            allowance.Remove(MailToolNames.Draft);

        // The broker builds all five; keep the ones this turn may have. Draft and attachment download
        // are dropped for a routine above, and the broker's own approval is null in that case.
        var built = context.Unattended
            ? context.Mail.ReadOnlyTools(context.Converter)
            : context.Mail.Tools(approval ?? new DenyAllMailApproval(), context.Converter);

        return built.Where(t => allowance.Contains(t.Name));
    }
}

/// <summary>Used when a caller built mail tools but supplied no approval surface: a request that
/// needs a person is answered "no" rather than hanging a turn on a card nobody can see.</summary>
internal sealed class DenyAllMailApproval : IMailApproval
{
    public Task<bool> ApproveAsync(string summary, string? detail, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Where a Buzzy's skills come from: its own <c>skills/</c> folder first, then the user's
/// skill folders and the built-in ones (artifact-design). Project skills are looked for from its
/// workspace, never from wherever Litos happens to be running.</summary>
public static class BuzzySkills
{
    public static ISkillDiscovery DiscoveryFor(BuzzyStore store, string buzzyId) =>
        new SkillDiscovery(store.WorkspaceOf(buzzyId), [store.SkillsOf(buzzyId), .. Litos.Artifacts.ArtifactSkills.UserRoots()]);
}
