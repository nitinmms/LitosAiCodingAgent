using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Tools.Skills;

public sealed class SkillTool(ISkillDiscovery discovery) : ITool
{
    public string Name => "skill";

    public string Description =>
        "Load a skill's full instructions by name. Call this before acting on a task " +
        "that matches one of the available skill descriptions.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { name = new { type = "string", description = "Name of the skill to load, as listed in the skill catalog." } },
        required = new[] { "name" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var name = arguments.GetStringOrNull("name");
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Error("A 'name' argument is required.");

        var skills = await discovery.DiscoverAsync(ct);
        var skill = skills.SingleOrDefault(s => s.Name == name);
        if (skill is null)
            return ToolResult.Error($"Unknown skill '{name}'.");

        var content = await File.ReadAllTextAsync(Path.Combine(skill.DirectoryPath, "SKILL.md"), ct);
        var (_, body) = SkillFrontmatter.Parse(content);
        return ToolResult.Ok(body);
    }
}
