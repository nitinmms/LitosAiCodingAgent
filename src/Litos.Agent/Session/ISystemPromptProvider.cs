using Litos.Agent.Tools;

namespace Litos.Agent.Session;

/// <summary>
/// Builds the system prompt for a turn — e.g. the skill catalog (name + description
/// only, "index not content") that lets the model decide whether to call the skill
/// tool. Defined here so AgentLoop can use it without Litos.Agent depending on
/// Litos.Tools; the environment supplies the implementation.
/// </summary>
public interface ISystemPromptProvider
{
    /// <param name="tools">
    /// The same ToolRegistry snapshot this turn's AgentLoop was built with — passed in per-call
    /// (rather than injected once into the implementation's constructor) so the rendered tool
    /// list always matches what the turn can actually invoke, even though the live/dynamic
    /// portion of the tool set (e.g. MCP-discovered tools) can change between turns.
    /// </param>
    /// <param name="workingDirectory">
    /// The session's own working directory (Transcript.WorkingDirectory) — passed in rather
    /// than read from the live process, so a resumed session reports the directory it was
    /// created in, not wherever the current process happens to be running from.
    /// </param>
    Task<string?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct);
}
