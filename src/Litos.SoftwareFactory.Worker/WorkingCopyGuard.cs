using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Tools;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// Keeps an agent's file tools and shell inside the working copy it was given. On a task created
/// against the wrong project, the agent listed the working copy's parent directory, found another
/// project's working copy beside it, and read that one's files and git history with
/// list_directory, search_code and <c>git -C</c>.
///
/// Like <see cref="ShellCommandGuard"/>, this is a guard against a mistake, not a sandbox: a
/// worker runs with the user's rights, and kernel code can still reach the disk through
/// System.IO. Isolating runs from one another is what a container or VM per run is for
/// (ReadMe_LitosSoftwareFactory_V1.md §17). What this does is turn the obvious, well-meant way
/// of wandering into another task's files into a refusal that says why.
/// </summary>
public sealed partial class WorkingCopyGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public WorkingCopyGuard(string workingCopy)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingCopy));
        Parent = Path.GetDirectoryName(Root);
    }

    /// <summary>The working copy: the worker's directory, and the only one an agent may touch.</summary>
    public string Root { get; }

    /// <summary>The directory that holds this working copy and every other project's.</summary>
    private string? Parent { get; }

    /// <summary>Why a file tool may not use this path, or null when it is inside the working copy.
    /// A missing path means the tool's default, the working directory itself.</summary>
    public string? PathRefusal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, Root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // not a path at all: the tool itself will say so
        }

        var inside = full.Equals(Root, PathComparison)
            || full.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison)
            || full.StartsWith(Root + Path.AltDirectorySeparatorChar, PathComparison);
        return inside ? null : OutsideMessage(path);
    }

    /// <summary>Why a shell command may not run, or null. Refused: a command that names another
    /// working copy beside this one, by its full path or as <c>..\name</c>.</summary>
    public string? CommandRefusal(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || Parent is null)
            return null;

        var own = Path.GetFileName(Root);
        foreach (var sibling in SiblingsNamedIn(command))
        {
            if (!sibling.Equals(own, PathComparison) && Directory.Exists(Path.Combine(Parent, sibling)))
                return OutsideMessage(sibling);
        }

        return null;
    }

    /// <summary>Every directory name the command reaches next to the working copy.</summary>
    private IEnumerable<string> SiblingsNamedIn(string command)
    {
        var normalised = command.Replace('\\', '/');
        var parent = Parent!.Replace('\\', '/').TrimEnd('/') + "/";
        for (var at = normalised.IndexOf(parent, PathComparison); at >= 0; at = normalised.IndexOf(parent, at + 1, PathComparison))
        {
            var segment = Segment().Match(normalised, at + parent.Length);
            if (segment.Success && segment.Index == at + parent.Length)
                yield return segment.Value;
        }

        foreach (Match up in UpOneLevel().Matches(normalised))
            yield return up.Groups[1].Value;
    }

    private string OutsideMessage(string path) =>
        $"'{path}' is outside this task's working copy ({Root}). Work only inside the working copy: other directories on this machine, "
        + "including other tasks' working copies, are not this task's to read or change. If the code the request describes is not in this "
        + "working copy, call request_decision and say so.";

    [GeneratedRegex(@"[^/\s""';&|<>`]+")]
    private static partial Regex Segment();

    [GeneratedRegex(@"(?:^|[\s""'=(])\.\./+([^/\s""';&|<>`]+)")]
    private static partial Regex UpOneLevel();
}

/// <summary>A file tool as the factory gives it to an agent: the same tool, refusing paths outside
/// the working copy (<see cref="WorkingCopyGuard"/>).</summary>
public sealed class ConfinedFileTool(ITool tool, WorkingCopyGuard guard) : ITool
{
    public string Name => tool.Name;

    public string Description => tool.Description;

    public JsonElement ParameterSchema => tool.ParameterSchema;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

        return guard.PathRefusal(path) is { } reason
            ? Task.FromResult(ToolResult.Error($"The factory refused this: {reason}"))
            : tool.InvokeAsync(arguments, ct);
    }
}
