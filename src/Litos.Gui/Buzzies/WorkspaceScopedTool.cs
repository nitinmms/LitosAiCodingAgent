using System.Text.Json;
using System.Text.Json.Nodes;
using Litos.Agent.Tools;

namespace Litos.Gui.Buzzies;

/// <summary>
/// Wraps a file tool so its <c>path</c> argument must lie inside a Buzzy's workspace, or inside a
/// folder granted to it for the operation the tool performs (<see cref="WorkspacePathGuard"/>). An
/// allowed path is handed to the tool as an absolute path, so the tool never depends on the
/// process's current directory; a refused one returns an error result to the model and the tool
/// never runs. A tool whose path is optional (search_code) gets the workspace itself when none is
/// given, rather than the process directory.
/// </summary>
public sealed class WorkspaceScopedTool : ITool
{
    private const string PathArgument = "path";

    private readonly ITool _inner;
    private readonly string _workspaceRoot;
    private readonly IReadOnlyList<BuzzyGrant> _grants;
    private readonly WorkspacePathGuard.Access _access;

    public WorkspaceScopedTool(ITool inner, string workspaceRoot, IReadOnlyList<BuzzyGrant>? grants = null)
    {
        _inner = inner;
        _workspaceRoot = workspaceRoot;
        _grants = grants ?? [];
        _access = AccessFor(inner.Name);
    }

    /// <summary>What the tool does to the path it is given. Anything that isn't a known write tool
    /// is treated as a read, which a read-only grant already covers.</summary>
    internal static WorkspacePathGuard.Access AccessFor(string toolName) => toolName switch
    {
        BuzzyToolNames.WriteFile => WorkspacePathGuard.Access.Write,
        BuzzyToolNames.EditFile => WorkspacePathGuard.Access.Change,
        _ => WorkspacePathGuard.Access.Read,
    };

    private ITool inner => _inner;

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
            resolved = Path.GetFullPath(_workspaceRoot);
        }
        else
        {
            var resolution = WorkspacePathGuard.Resolve(_workspaceRoot, _grants, requested, _access);
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
