using Litos.Agent.Tools;

namespace Litos.Agent.Session;

/// <summary>
/// One AGENTS.md/CLAUDE.md-style file folded into the system prompt, kept as its own
/// section (rather than pre-flattened into Identity) so a consumer like the "View Context"
/// breakdown can attribute its size to "memory" instead of lumping it in with the rest
/// of the prompt.
/// </summary>
public sealed record SystemPromptInstructionFile(string Path, string Content);

/// <summary>
/// The system prompt broken into the sections LitosSystemPromptProvider builds it from.
/// Render() is the single source of truth for exact on-wire formatting — everything
/// actually sent to a provider goes through it — so a consumer measuring section sizes
/// (e.g. ContextBreakdown) can never drift from what Render() produces.
/// </summary>
public sealed record SystemPromptSections(
    string Identity,
    string ToolsList,
    string Guidelines,
    string? SkillsCatalog,
    IReadOnlyList<SystemPromptInstructionFile> Instructions,
    string Footer)
{
    public string Render()
    {
        List<string> parts = [Identity, ToolsList, Guidelines];

        if (SkillsCatalog is not null)
            parts.Add(SkillsCatalog);

        parts.AddRange(Instructions.Select(file => $"Instructions from {file.Path}:\n{file.Content}"));

        parts.Add(Footer);

        return string.Join("\n\n", parts.Where(p => p.Length > 0));
    }
}

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
    Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct);
}
