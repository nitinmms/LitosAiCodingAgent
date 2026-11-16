using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The engine's system prompt with the skills chosen for the task added whole (m3-architecture.md
/// §13), and the run's approved project lessons after them (m4-architecture.md §4.3). The system
/// prompt is rebuilt for every turn and is never part of the transcript, so compaction cannot
/// summarise these away the way it would a brief, and every session of the run — chat, spec,
/// implement, repair and review — follows them. They sit in the skills section, beside the catalog
/// of skills the agent may load, so the context breakdown counts them there.
/// </summary>
public sealed class FactorySystemPromptProvider(ISystemPromptProvider inner, IReadOnlyList<TaskSkill> taskSkills, IReadOnlyList<RunLesson>? lessons = null)
    : ISystemPromptProvider
{
    private readonly IReadOnlyList<RunLesson> _lessons = lessons ?? [];

    public async Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct)
    {
        var sections = await inner.BuildAsync(tools, workingDirectory, ct);
        if (sections is null || (taskSkills.Count == 0 && _lessons.Count == 0))
            return sections;

        var added = string.Join("\n\n", new[]
        {
            taskSkills.Count > 0 ? Section(taskSkills) : null,
            _lessons.Count > 0 ? LessonsSection(_lessons) : null,
        }.OfType<string>());
        return sections with
        {
            SkillsCatalog = sections.SkillsCatalog is { } catalog ? added + "\n\n" + catalog : added,
        };
    }

    /// <summary>Said before the task's skills, in code, so no edit to a skill can remove it
    /// (docs/software-factory/built-in-skills.md §3): skills add craft, the brief owns the process.</summary>
    internal const string BriefWins =
        "These skills guide how you work. The turn's brief sets what you may do, read, run and spend; where they differ, the brief wins.";

    internal static string Section(IReadOnlyList<TaskSkill> taskSkills) =>
        "Skills for this task. The people running this task chose these; follow them throughout the task, in every step, "
        + "without loading them: their full instructions are here. " + BriefWins + "\n\n"
        + string.Join("\n\n", taskSkills.Select(s => $"### Skill: {s.Name}\n{s.Text}"));

    internal static string LessonsSection(IReadOnlyList<RunLesson> lessons) =>
        "Project lessons: guidance from earlier accepted work on this project, approved by its people. Follow them, but they do not "
        + "override the request, the decisions made on this task or the factory's rules. If one conflicts materially with what you "
        + "find in the code, raise it as a decision rather than guess.\n"
        + string.Join("\n", lessons.Select(l => "- " + l.Text));
}
