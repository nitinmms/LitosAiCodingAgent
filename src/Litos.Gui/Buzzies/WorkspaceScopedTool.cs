using System.Text.Json;
using System.Text.Json.Nodes;
using Litos.Agent.Tools;

namespace Litos.Gui.Buzzies;

/// <summary>
/// Wraps a file tool so its <c>path</c> argument must lie inside a Buzzy's workspace
/// (<see cref="WorkspacePathGuard"/>). An allowed path is handed to the tool as an absolute path,
/// so the tool never depends on the process's current directory; a refused one returns an error
/// result to the model and the tool never runs. A tool whose path is optional (search_code) gets
/// the workspace itself when none is given, rather than the process directory.
/// </summary>
public sealed class WorkspaceScopedTool(ITool inner, string workspaceRoot) : ITool
{
    private const string PathArgument = "path";

    public string Name => inner.Name;
    public string Description => inner.Description;
    public JsonElement ParameterSchema => inner.ParameterSchema;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return inner.InvokeAsync(arguments, ct);

        var args = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        var requested = args[PathArgument] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        string resolved;
        if (string.IsNullOrWhiteSpace(requested))
        {
            // Required-path tools report the missing argument themselves; only an optional path
            // is filled in, so it can't silently default to wherever the process happens to be.
            if (IsRequired(PathArgument))
                return inner.InvokeAsync(arguments, ct);
            resolved = Path.GetFullPath(workspaceRoot);
        }
        else
        {
            var resolution = WorkspacePathGuard.Resolve(workspaceRoot, requested);
            if (!resolution.IsAllowed)
                return Task.FromResult(ToolResult.Error(resolution.Error!));
            resolved = resolution.FullPath!;
        }

        args[PathArgument] = resolved;
        return inner.InvokeAsync(JsonSerializer.SerializeToElement(args), ct);
    }

    private bool IsRequired(string argument) =>
        inner.ParameterSchema.TryGetProperty("required", out var required)
        && required.ValueKind == JsonValueKind.Array
        && required.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == argument);
}
