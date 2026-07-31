using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Tools.ProjectInstructions;
using Litos.Tools.Skills;

namespace Litos.Host;

/// <summary>
/// Builds the default system prompt: a minimal identity line, the available tool
/// list, a couple of always-on guidelines, the skill catalog (name+description only —
/// see Litos.Tools.Skills), any discovered AGENTS.md/CLAUDE.md instruction files (see
/// Litos.Tools.ProjectInstructions), and a trailing date/cwd footer. Loosely adapted
/// from pi's buildSystemPrompt (packages/coding-agent/src/core/system-prompt.ts) but
/// stripped to the minimum LitosAiAgent actually needs — no pi-specific doc
/// cross-references, no customPrompt/append machinery.
/// </summary>
public sealed class LitosSystemPromptProvider(
    ISkillDiscovery skillDiscovery,
    IProjectInstructionsDiscovery projectInstructionsDiscovery) : ISystemPromptProvider
{
    public async Task<string?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct)
    {
        var toolsList = tools.Schemas.Count == 0
            ? "(none)"
            : string.Join('\n', tools.Schemas.Select(t => $"- {t.Name}: {t.Description}"));

        var prompt = $"""
            You are LitosAiAgent, an expert coding assistant operating inside a minimal .NET coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.

            Available tools:
            {toolsList}

            Guidelines:
            - Be concise in your responses
            - Show file paths clearly when working with files
            - ALWAYS use search_code to find where something is defined or used across the codebase. NEVER invoke grep, rg, findstr, or Select-String as a shell command — search_code is faster, token-budgeted, and respects ignore rules that a raw shell search will not.
            """;

        var skills = await skillDiscovery.DiscoverAsync(ct);
        if (skills.Count > 0)
        {
            var skillLines = skills.Select(s => $"- {s.Name}: {s.Description}");
            prompt += "\n\nAvailable skills (call the `skill` tool with a name to load its full instructions):\n"
                + string.Join('\n', skillLines);
        }

        var instructionFiles = await projectInstructionsDiscovery.DiscoverAsync(ct);
        foreach (var file in instructionFiles)
            prompt += $"\n\nInstructions from {file.Path.Replace('\\', '/')}:\n{file.Content}";

        prompt += $"\n\nCurrent date: {DateTime.Now:yyyy-MM-dd}";
        prompt += $"\nCurrent working directory: {(workingDirectory ?? Directory.GetCurrentDirectory()).Replace('\\', '/')}";

        return prompt;
    }
}
