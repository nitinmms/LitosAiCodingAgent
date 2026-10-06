using Litos.SoftwareFactory.Core.Ports;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>
/// How the working copy differs from the run's last checkpoint (§16: on resume, check git status
/// against the checkpoint). The run continues either way; the difference is stated to the person
/// in a note and to the agent in the resume brief.
///
/// Snapshots are taken between steps, so a run interrupted in the middle of a turn also lists that
/// turn's own edits. The wording therefore says what changed, never who changed it.
/// </summary>
public sealed record WorkspaceDrift(
    string? OldBranch, string? NewBranch, string? OldHead, string? NewHead,
    IReadOnlyList<string> Edited, IReadOnlyList<string> Added, IReadOnlyList<string> Reverted, IReadOnlyList<string> Deleted)
{
    public bool IsEmpty =>
        OldBranch is null && OldHead is null && Edited.Count == 0 && Added.Count == 0 && Reverted.Count == 0 && Deleted.Count == 0;

    /// <summary>
    /// Compares a checkpoint with the working copy now. Paths are listed in ordinal order.
    /// </summary>
    /// <remarks>A path is "added" when it differs now but did not at the checkpoint, "reverted"
    /// when it differed at the checkpoint and no longer does, "deleted" when it now differs by
    /// being deleted, and "edited" when it differed both times but its content changed.</remarks>
    public static WorkspaceDrift Compare(WorkspaceSnapshot before, WorkspaceSnapshot after)
    {
        var edited = new List<string>();
        var added = new List<string>();
        var deleted = new List<string>();
        foreach (var (path, hash) in after.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (before.Files.TryGetValue(path, out var earlier))
            {
                if (earlier == hash)
                    continue;
                (hash == WorkspaceSnapshot.Deleted ? deleted : edited).Add(path);
            }
            else
            {
                (hash == WorkspaceSnapshot.Deleted ? deleted : added).Add(path);
            }
        }

        // A file left out of a truncated snapshot is not known to have been reverted.
        List<string> reverted = after.Truncated
            ? []
            : [.. before.Files.Keys.Where(p => !after.Files.ContainsKey(p)).Order(StringComparer.Ordinal)];

        var branchMoved = before.Branch != after.Branch;
        var headMoved = before.Head != after.Head;
        return new WorkspaceDrift(
            branchMoved ? before.Branch : null, branchMoved ? after.Branch : null,
            headMoved ? before.Head : null, headMoved ? after.Head : null,
            edited, added, reverted, deleted);
    }

    /// <summary>One line per kind of change, each listing at most <paramref name="maxPaths"/>
    /// paths. Empty when nothing changed.</summary>
    public string Describe(int maxPaths = 20)
    {
        if (IsEmpty)
            return "";

        var lines = new List<string>();
        if (OldBranch is not null)
            lines.Add($"- The checked-out branch is `{NewBranch}`, not `{OldBranch}`.");
        if (OldHead is not null)
            lines.Add($"- The head commit moved from {Short(OldHead)} to {Short(NewHead!)}.");
        Add(lines, "Changed since then", Edited, maxPaths);
        Add(lines, "Newly changed", Added, maxPaths);
        Add(lines, "Deleted", Deleted, maxPaths);
        Add(lines, "No longer changed (back to the head commit)", Reverted, maxPaths);
        return string.Join("\n", lines);
    }

    private static void Add(List<string> lines, string label, IReadOnlyList<string> paths, int maxPaths)
    {
        if (paths.Count == 0)
            return;

        var shown = string.Join(", ", paths.Take(maxPaths).Select(p => $"`{p}`"));
        var more = paths.Count > maxPaths ? $" and {paths.Count - maxPaths} more" : "";
        lines.Add($"- {label}: {shown}{more}.");
    }

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;
}
