using System.Collections.Concurrent;
using Litos.Agent.Tools;
using Litos.Hosting;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// Which tools each kind of turn gets (ReadMe_LitosSoftwareFactory_V1.md §8). The set is fixed
/// in M1 — no MCP, no skills, no web search:
///
/// - implement, repair and rework turns edit code and finish with submit_work or request_decision;
/// - a review turn can only read, and finishes with submit_review;
/// - a spec turn can only read, and finishes with submit_spec;
/// - a chat turn can only read.
///
/// A nudge carries no kind of its own: it gets whatever the turn it follows had, so a nudged
/// review still cannot edit files.
/// </summary>
public sealed class FactoryToolSetPolicy : IToolSetPolicy
{
    private static readonly string[] ReadOnlyTools = ["read_file", "list_directory", "search_code"];
    private static readonly string[] WorkTools = ["read_file", "write_file", "edit_file", "list_directory", "search_code", "shell"];

    private readonly Dictionary<string, ITool> _builtIn;
    private readonly FactoryHostClient _host;
    private readonly ConcurrentDictionary<string, TurnKind> _lastKind = new();

    private static readonly string[] FileTools = ["read_file", "write_file", "edit_file", "list_directory", "search_code"];

    /// <param name="workingCopy">The directory the agent may work in; the worker's own by default,
    /// which the host starts it in.</param>
    public FactoryToolSetPolicy(IEnumerable<ITool> registeredTools, FactoryHostClient host, string? workingCopy = null)
    {
        _host = host;
        _builtIn = registeredTools.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());

        // An agent gets the shell and the file tools behind guards, whether it calls them directly
        // or from kernel code: no stopping processes by name, and nothing outside the working copy.
        var guard = new WorkingCopyGuard(workingCopy ?? Directory.GetCurrentDirectory());
        if (_builtIn.TryGetValue("shell", out var shell))
            _builtIn["shell"] = new GuardedShellTool(shell, guard);
        foreach (var name in FileTools)
        {
            if (_builtIn.TryGetValue(name, out var tool))
                _builtIn[name] = new ConfinedFileTool(tool, guard);
        }

        var missing = WorkTools.Where(name => !_builtIn.ContainsKey(name)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"The factory tool set needs tools that are not registered: {string.Join(", ", missing)}.");
    }

    /// <exception cref="InvalidOperationException">The host did not say what kind of turn this is,
    /// or named one this worker does not know. A turn is never started on a guessed tool set.</exception>
    public ToolRegistry Create(string sessionId, string? turnKind)
    {
        if (!Enum.TryParse<TurnKind>(turnKind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
            throw new InvalidOperationException($"Unknown turn kind '{turnKind ?? "(none)"}'. The factory host must name the kind of every turn.");

        var effective = kind;
        if (kind == TurnKind.Nudge && !_lastKind.TryGetValue(sessionId, out effective))
        {
            // A nudge with nothing before it — a restarted worker, say. Guessing "implement" here
            // would hand write tools and the shell to what may be a review session.
            throw new InvalidOperationException(
                $"Session '{sessionId}' has had no turn in this worker, so a Nudge has no tool set to continue. Start it with the turn's real kind.");
        }

        _lastKind[sessionId] = effective;
        return ToolsFor(effective, sessionId);
    }

    /// <summary>
    /// The tools kernel code may call for a session: the same set its current turn has. Read on
    /// every bridged call, so a session that moves from one kind of turn to another never keeps
    /// the earlier turn's tools. A session that has had no turn yet can only read.
    /// </summary>
    public ToolRegistry CreateForBridge(string sessionId) =>
        _lastKind.TryGetValue(sessionId, out var kind) ? ToolsFor(kind, sessionId) : new ToolRegistry(BuiltIn(ReadOnlyTools));

    private ToolRegistry ToolsFor(TurnKind kind, string sessionId) => kind switch
    {
        TurnKind.Implement or TurnKind.Repair or TurnKind.Rework =>
            new ToolRegistry([.. BuiltIn(WorkTools), new SubmitWorkTool(_host, sessionId), new RequestDecisionTool(_host, sessionId)]),
        TurnKind.Review => new ToolRegistry([.. BuiltIn(ReadOnlyTools), new SubmitReviewTool(_host, sessionId)]),
        TurnKind.Spec => new ToolRegistry([.. BuiltIn(ReadOnlyTools), new SubmitSpecTool(_host, sessionId)]),
        TurnKind.Chat => new ToolRegistry(BuiltIn(ReadOnlyTools)),
        _ => throw new InvalidOperationException($"No tool set is defined for a {kind} turn."),
    };

    private IEnumerable<ITool> BuiltIn(IEnumerable<string> names) => names.Select(name => _builtIn[name]);
}
