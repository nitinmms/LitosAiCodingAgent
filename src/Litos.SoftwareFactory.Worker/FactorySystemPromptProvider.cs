using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The engine's system prompt with the skills chosen for the task added whole (m3-architecture.md
/// §13). The system prompt is rebuilt for every turn and is never part of the transcript, so
/// compaction cannot summarise these away the way it would a brief, and every session of the run —
/// chat, spec, implement, repair and review — follows them. They sit in the skills section, beside
/// the catalog of skills the agent may load, so the context breakdown counts them as skills.
/// </summary>
public sealed class FactorySystemPromptProvider(ISystemPromptProvider inner, IReadOnlyList<TaskSkill> taskSkills) : ISystemPromptProvider
{
    public async Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct)
    {
        var sections = await inner.BuildAsync(tools, workingDirectory, ct);
        if (sections is null || taskSkills.Count == 0)
            return sections;

        var chosen = Section(taskSkills);
        return sections with
        {
            SkillsCatalog = sections.SkillsCatalog is { } catalog ? chosen + "\n\n" + catalog : chosen,
        };
    }

    internal static string Section(IReadOnlyList<TaskSkill> taskSkills) =>
        "Skills for this task. The people running this task chose these; follow them throughout the task, in every step, "
        + "without loading them: their full instructions are here.\n\n"
        + string.Join("\n\n", taskSkills.Select(s => $"### Skill: {s.Name}\n{s.Text}"));
}
