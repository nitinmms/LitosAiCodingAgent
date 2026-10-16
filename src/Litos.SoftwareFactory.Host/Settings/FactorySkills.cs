using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.Tools.Skills;

namespace Litos.SoftwareFactory.Host.Settings;

/// <summary>The skills one worker starts with, and what became of its repository's.</summary>
/// <param name="Path">The run's skills file for the worker; null when it has no skill.</param>
public sealed record PreparedSkills(string? Path, IReadOnlyList<RepositorySkillUse> Repository);

/// <summary>
/// The factory's skills (blueprint §8.2, m3-architecture.md §7.3). The library is written to
/// <c>&lt;data&gt;/skills/&lt;revision&gt;/</c> the first time a run needs that revision, and never
/// changed after, so a running worker's skills stay as they were. A repository's skills are found
/// when a worker starts on its working copy, recorded for the Skills tab, and allowed by the policy.
/// </summary>
public sealed class FactorySkills(FactorySettings settings, IFactoryStore store, FactoryOptions options, IClock clock, ILogger<FactorySkills> logger)
{
    /// <summary>The library as it is now, for a run that starts now.</summary>
    public (long Revision, IReadOnlyList<RunLibrarySkill> Skills, RepositorySkillPolicy Policy) Current()
    {
        var (skills, revision) = settings.ReadWithRevision<SkillSettings>(SettingsSections.Skills);
        return (revision, [.. skills.Enabled.Select(s => new RunLibrarySkill(s.Name, s.Description))], skills.RepositoryPolicy);
    }

    /// <summary>
    /// Which of the project's approved lessons a run of this task is given (m4-architecture.md §4.1),
    /// from the task's words — title, type, the request that delegated it, this run's request and any
    /// approved specification — its manual picks and what its budget has left. Nothing is recorded here.
    /// </summary>
    public async Task<IReadOnlyList<LessonPick>> LessonsForAsync(ClaimedRun claimed, CancellationToken ct)
    {
        var (run, thread, project) = claimed;
        var details = await store.GetThreadAsync(thread.Id, ct);
        var spec = details?.LatestSpec is { ApprovedAt: not null } approved
            ? approved.Summary + "\n" + string.Join("\n", JsonSerializer.Deserialize<List<string>>(approved.AcceptanceCriteriaJson) ?? [])
            : null;
        var taskText = string.Join("\n", new[] { thread.Title, thread.TypeLabel, details?.TaskRequest, run.Request, spec }.OfType<string>());
        long? remaining = thread.BudgetCap is { } cap ? cap - thread.TokensUsed - thread.TokensReserved : null;
        var manual = JsonSerializer.Deserialize<List<Guid>>(thread.ManualLessonsJson) ?? [];
        return LessonRetrieval.Select(await store.ListRetrievableLessonsAsync(project.Id, ct), taskText, manual, remaining);
    }

    /// <summary>The lessons a run is given, as the worker carries them.</summary>
    public static IReadOnlyList<RunLesson> RunLessons(IReadOnlyList<LessonPick> picks) =>
        [.. LessonRetrieval.Included(picks).Select(c => new RunLesson(c.Number, c.Revision, c.Rule))];

    /// <summary>The skills chosen for the task, with their text as the library has it now.</summary>
    public TaskSkillSelection.Resolved ForTask(TaskThread thread) =>
        TaskSkillSelection.Resolve(TaskSkillSelection.Parse(thread.SkillsJson), settings.Skills);

    /// <summary>
    /// Finds the working copy's skills, records them, decides which the worker may use and writes
    /// them where it reads them. Never fails a run: a repository that cannot be read has no skills.
    /// </summary>
    /// <param name="libraryRevision">The library revision the run was given; its folder is written if it is not there yet.</param>
    /// <param name="taskSkills">The skills chosen for the task, whole, for every session's system prompt (m3-architecture.md §13).</param>
    /// <param name="lessons">The approved project lessons the run is given, for the same place (m4-architecture.md §4.3).</param>
    public async Task<PreparedSkills> PrepareAsync(
        Guid runId, Guid projectId, string workingCopy, long libraryRevision, IReadOnlyList<RunLibrarySkill> library,
        RepositorySkillPolicy policy, IReadOnlyList<TaskSkill> taskSkills, CancellationToken ct, IReadOnlyList<RunLesson>? lessons = null)
    {
        var libraryRoot = library.Count == 0 ? null : LibraryRoot(libraryRevision);

        IReadOnlyList<RepositorySkill> found;
        try
        {
            found = Find(workingCopy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Run {RunId}: the repository's skills could not be read.", runId);
            found = [];
        }

        IReadOnlyList<RepositorySkillUse> uses = [];
        if (found.Count > 0)
        {
            var rows = await store.RecordRepositorySkillsAsync(projectId, found, clock.UtcNow, ct);
            var approved = rows.Where(r => r.ApprovedAt is not null).Select(r => (r.Name, r.Hash)).ToHashSet();
            uses = SkillSelection.Decide(policy, found.Select(f => new FoundSkill(f.Name, f.Description, f.Hash)), approved, library.Select(s => s.Name));
        }

        var skills = new RunSkills(libraryRoot, [.. uses.Where(u => u.Used).Select(u => new AllowedRepositorySkill(u.Name, u.Hash))])
        {
            TaskSkills = taskSkills,
            Lessons = lessons ?? [],
        };
        if (skills.IsEmpty)
            return new PreparedSkills(null, uses);

        var path = Path.Combine(options.RunDirectory(runId), "skills.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(skills, FactoryWire.Json), ct);
        return new PreparedSkills(path, uses);
    }

    /// <summary>
    /// The folder of one library revision, written now if no run has needed it yet. Only the
    /// current revision's skills are known, so an older one that is missing — its folder deleted by
    /// hand — is replaced by the current library, and the host's log says so.
    /// </summary>
    public string? LibraryRoot(long revision)
    {
        var root = Path.Combine(options.DataDirectory, "skills", revision.ToString());
        if (Directory.Exists(root))
            return root;

        var (current, currentRevision) = settings.ReadWithRevision<SkillSettings>(SettingsSections.Skills);
        if (currentRevision != revision)
        {
            logger.LogWarning("Skill library revision {Revision} is not on disk; runs given it get revision {Current} instead.", revision, currentRevision);
            root = Path.Combine(options.DataDirectory, "skills", currentRevision.ToString());
            if (Directory.Exists(root))
                return root;
        }

        var enabled = current.Enabled;
        if (enabled.Count == 0)
            return null;

        // Written beside it and moved into place, so a worker never reads half a library.
        var staging = Path.Combine(options.DataDirectory, "skills", $".staging-{Guid.NewGuid():N}");
        for (var i = 0; i < enabled.Count; i++)
        {
            // The folder's name is not the skill's (the SKILL.md names it), so no skill name can be a reserved file name.
            var folder = Path.Combine(staging, $"{i + 1:D3}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), enabled[i].Markdown());
        }

        try
        {
            Directory.Move(staging, root);
        }
        catch (IOException) when (Directory.Exists(root))
        {
            // Another run wrote the same revision first; its copy is the same.
            Directory.Delete(staging, recursive: true);
        }

        return root;
    }

    /// <summary>The skills directly in a working copy's <c>.litos/skills</c>, as rows to record.</summary>
    public static IReadOnlyList<RepositorySkill> Find(string workingCopy)
    {
        var root = RepositorySkillFolder.In(workingCopy);
        return
        [
            .. SkillDiscovery.ScanRoot(root)
                .OrderBy(s => s.DirectoryPath, StringComparer.Ordinal)
                .Select(skill =>
                {
                    var skillMd = Path.Combine(skill.DirectoryPath, "SKILL.md");
                    var others = Directory.EnumerateFiles(skill.DirectoryPath, "*", SearchOption.AllDirectories)
                        .Where(f => !string.Equals(f, skillMd, StringComparison.Ordinal))
                        .Select(f => Path.GetRelativePath(skill.DirectoryPath, f).Replace('\\', '/'))
                        .Order(StringComparer.Ordinal);
                    return new RepositorySkill
                    {
                        Name = skill.Name,
                        Hash = SkillHash.Of(skill.DirectoryPath),
                        Description = skill.Description,
                        Folder = Path.GetRelativePath(workingCopy, skill.DirectoryPath).Replace('\\', '/'),
                        Content = File.ReadAllText(skillMd),
                        OtherFilesJson = JsonSerializer.Serialize(others.ToList()),
                    };
                }),
        ];
    }
}
