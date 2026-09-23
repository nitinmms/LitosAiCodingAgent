using System.Collections.Concurrent;
using Litos.Agent.Tools;
using Litos.Tools.Mcp;

namespace Litos.Kernel;

/// <summary>
/// Owns the chatSessionId -> KernelSession map (§8.2) — modeled as a dictionary keyed by session
/// id rather than a single nullable field, even though Litos.Gui only ever has one active chat
/// session per window today, matching §4.4's "never shared across two different chat sessions"
/// precisely and costing nothing extra.
/// </summary>
public sealed class KernelSessionManager(Func<string, ToolRegistry> bridgedToolsSourceFactory, McpToolProvider? mcpToolProvider = null)
{
    private readonly ConcurrentDictionary<string, KernelSession> _sessions = new();

    /// <param name="scratchDirectoryFactory">Resolves a session id to its scratch directory — deferred rather than resolved eagerly, since the session id is the only thing known at GetOrCreate time.</param>
    public KernelSession GetOrCreate(string sessionId, string workingDirectory, Func<string, string> scratchDirectoryFactory) =>
        _sessions.GetOrAdd(sessionId, id => new KernelSession(
            id,
            workingDirectory,
            scratchDirectoryFactory(id),
            () => bridgedToolsSourceFactory(id),
            mcpToolProvider));

    public async Task ResetAsync(string sessionId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            await session.ResetAsync(ct);
    }

    /// <summary>Called by /new — kills and forgets the session's kernel entirely, per §4.4's reset-trigger table.</summary>
    public async Task DestroyAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
            await session.DisposeAsync();
    }
}
