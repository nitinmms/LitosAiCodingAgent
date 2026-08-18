using System.Text.Json;
using Litos.Tools.Skills;

namespace Litos.VsCodeHost.Skills;

/// <summary>
/// /skills and /skill — SkillDiscovery/SkillTool already exist (Litos.Host.AddLitosAgent already
/// registers a workspace-agnostic SkillTool for the model's own use) but no face has ever exposed
/// listing/by-name loading over HTTP. Discovery here is deliberately re-scoped per request to
/// `cwd` (the workspace folder path passed in, matching extension.ts's own resolution of
/// vscode.workspace.workspaceFolders[0]), not the DI-registered SkillDiscovery singleton — same
/// reasoning Litos.Gui's own MainWindow.axaml.cs gives for constructing `new
/// SkillDiscovery(_transcript.WorkingDirectory)` itself rather than resolving the container's copy:
/// project-local skills (.litos/skills/) are scoped to whichever session's working directory is
/// asking, not a single process-wide default.
/// </summary>
public static class SkillsEndpoints
{
    public static IEndpointRouteBuilder MapSkillsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/skills", async (string cwd, CancellationToken ct) =>
        {
            var discovery = new SkillDiscovery(cwd);
            var skills = await discovery.DiscoverAsync(ct);
            return Results.Ok(skills.Select(s => new { s.Name, s.Description }));
        });

        app.MapGet("/skills/{name}", async (string name, string cwd, CancellationToken ct) =>
        {
            var discovery = new SkillDiscovery(cwd);
            // Frontmatter-stripped body only, via SkillTool.InvokeAsync itself — the same public
            // seam the model uses when it calls the skill tool (SkillFrontmatter.Parse, the type
            // that actually strips it, is internal to Litos.Tools; this reuses the tool's own
            // public entry point rather than needing an InternalsVisibleTo into a shared project).
            var tool = new SkillTool(discovery);
            var arguments = JsonSerializer.SerializeToElement(new { name });
            var result = await tool.InvokeAsync(arguments, ct);

            return result.IsError ? Results.NotFound(result.Text) : Results.Ok(new { name, content = result.Text });
        });

        return app;
    }
}
