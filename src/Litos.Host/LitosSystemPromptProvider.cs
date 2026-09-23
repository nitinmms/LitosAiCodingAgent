using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Tools.ProjectInstructions;
using Litos.Tools.Skills;

// ReservedToolNames lives in Litos.Agent (not Litos.Kernel) precisely so callers like this one
// can detect kernel-mode ON without Litos.Host taking a Litos.Kernel reference (ReservedToolNames'
// own doc comment, ReadMe_PTCPersistentKernel.md §8.2).

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

        // Kernel-mode ON means tools.Schemas is exactly [KernelCodeTool] (§1/§6/§8.2) — no separate
        // toggle flag is threaded through here; the registry's own shape is already the signal,
        // since ToolRegistryFactory.Create() is rebuilt fresh from the toggle every turn anyway.
        var kernelModeOn = tools.Schemas.Count == 1 && tools.Schemas[0].Name == ReservedToolNames.KernelCode;

        var guidelines = kernelModeOn
            ? """
                Guidelines:
                - Be concise in your responses
                - Show file paths clearly when working with files
                - run_kernel_code is your only tool this session. Collapse multi-step, result-dependent work into ONE script rather than several separate calls — e.g. "read file A, and if it imports X also read file B" is one script with an if-statement, not two calls with you deciding in between.
                - Variables, imports, and locally declared functions persist in the kernel across calls within this session. Before re-deriving something, consider whether an earlier call already computed it — the trailer on each result ("[kernel state changed this round: ...]") tells you what changed most recently; call KernelState.List() to re-orient on everything built so far in a long session.
                - Keep your script's own printed output and return value short — a summary, not raw data. Large results are truncated automatically; prefer keeping intermediate data in variables instead of printing/returning it.
                - See run_kernel_code's own tool description for the full list of what's callable inside a script (SCRATCH_DIR, KernelState, and every bridged tool), plus known C#-scripting pitfalls (raw-string nesting, read_file output not being safe to round-trip into write_file).
                """
            : """
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
