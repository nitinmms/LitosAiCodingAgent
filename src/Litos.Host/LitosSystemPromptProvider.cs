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
    public async Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct)
    {
        var toolsList = tools.Schemas.Count == 0
            ? "(none)"
            : string.Join('\n', tools.Schemas.Select(t => $"- {t.Name}: {t.Description}"));

        var identity = "You are LitosAiAgent, an expert coding assistant operating inside a minimal .NET coding agent harness. "
            + "You help users by reading files, executing commands, editing code, and writing new files.";

        var guidelines = """
            Guidelines:
            - Be concise in your responses
            - Show file paths clearly when working with files
            - ALWAYS use search_code to find where something is defined or used across the codebase. NEVER invoke grep, rg, findstr, or Select-String as a shell command — search_code is faster, token-budgeted, and respects ignore rules that a raw shell search will not.
            """;

        var skills = await skillDiscovery.DiscoverAsync(ct);
        string? skillsCatalog = null;
        if (skills.Count > 0)
        {
            var skillLines = skills.Select(s => $"- {s.Name}: {s.Description}");
            skillsCatalog = "Available skills (call the `skill` tool with a name to load its full instructions):\n"
                + string.Join('\n', skillLines);
        }

        var instructionFiles = await projectInstructionsDiscovery.DiscoverAsync(ct);
        var instructions = instructionFiles
            .Select(file => new SystemPromptInstructionFile(file.Path.Replace('\\', '/'), file.Content))
            .ToList();

        var footer = $"Current date: {DateTime.Now:yyyy-MM-dd}"
            + $"\nCurrent working directory: {(workingDirectory ?? Directory.GetCurrentDirectory()).Replace('\\', '/')}";

        return new SystemPromptSections(identity, $"Available tools:\n{toolsList}", guidelines, skillsCatalog, instructions, footer);
    }
}
