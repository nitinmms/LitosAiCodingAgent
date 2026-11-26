using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Gui.Buzzies;

/// <summary>
/// For unattended runs: write_file may create files, and rewrite ones it created earlier in the
/// same run, but never replace a file that was already there. A repeated routine therefore adds
/// a new result each time instead of silently replacing last week's (ReadMe_Buzzies.md §1A,
/// "a repeated task should create a new artifact without overwriting prior results by default").
/// Deliberate edits still work through edit_file, which changes part of a file in place. Sits
/// inside <see cref="WorkspaceScopedTool"/>, so the path it sees is already absolute.
/// </summary>
public sealed class NoOverwriteTool(ITool inner) : ITool
{
    private readonly HashSet<string> _createdThisRun = new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public string Name => inner.Name;
    public string Description => inner.Description + " In this scheduled run it cannot replace a file that already existed; save results under a new name that includes today's date.";
    public JsonElement ParameterSchema => inner.ParameterSchema;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            return await inner.InvokeAsync(arguments, ct);

        bool existedBefore;
        lock (_gate)
            existedBefore = File.Exists(path) && !_createdThisRun.Contains(path);
        if (existedBefore)
            return ToolResult.Error(
                $"'{Path.GetFileName(path)}' already exists from an earlier run, and a scheduled run never replaces earlier results. " +
                "Save under a new name that includes today's date, or use edit_file to change part of the existing file on purpose.");

        var result = await inner.InvokeAsync(arguments, ct);
        if (!result.IsError)
            lock (_gate)
                _createdThisRun.Add(path);
        return result;
    }
}
