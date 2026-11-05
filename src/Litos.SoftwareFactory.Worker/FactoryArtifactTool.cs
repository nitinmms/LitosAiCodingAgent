using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Artifacts;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The chat turn's <c>artifact</c> tool (ReadMe_ArtifactsFeature.md §11.3): publish a page on the
/// thread, read or list the thread's pages, or fetch the design rules. A chat turn has no write
/// tools, so a page travels whole in the call; the host stores it and refuses anything but a chat
/// run's own thread. The design rules are fetched on demand — the people running the factory chose
/// that over carrying them in every chat's system prompt.
/// </summary>
public sealed class FactoryArtifactTool(FactoryHostClient host) : ITool
{
    public const string ToolName = "artifact";

    public string Name => ToolName;

    public string Description =>
        "Publish a self-contained HTML page (a diagram, comparison, sortable table, dashboard or explainer) on this thread. "
        + "It appears as a card under your answer that anyone on the thread can open in a new browser tab. "
        + "Call action \"guide\" once first for the design rules, then \"publish\" with the whole page as `html`. "
        + "To change a page already on the thread, \"read\" it and publish the whole new page with its `id`: that adds a version. "
        + "\"list\" shows the thread's pages.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                @enum = new[] { "guide", "publish", "read", "list" },
                description = "What to do.",
            },
            html = new { type = "string", description = "publish: the whole page, one self-contained HTML document." },
            title = new { type = "string", description = "publish: the page's name. Defaults to its <title>." },
            id = new { type = "string", description = "publish: a page on this thread to add a version to. read: the page." },
            version = new { type = "integer", description = "read: a specific version. Defaults to the latest." },
        },
        required = new[] { "action" },
    });

    /// <summary>What <c>guide</c> returns: how publishing works here, then the shared design rules.</summary>
    internal static string Guide { get; } =
        "# Pages in the factory\n\n"
        + "- Pass the whole page as `html` to `publish`; there is no file to write. From kernel code, build the page in a "
        + "variable and pass name and value pairs: `await artifact(\"action\", \"publish\", \"html\", page, \"title\", \"Export Pipeline\")`.\n"
        + "- It appears as a card under your answer, for anyone on the thread. Say in a sentence what it shows; don't paste a link.\n"
        + "- To change a page, `read` it, then `publish` the whole new page with its `id`. Don't publish a second copy.\n\n"
        + ArtifactSkills.DesignRules;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        try
        {
            return arguments.GetStringOrNull("action") switch
            {
                "guide" => ToolResult.Ok(Guide),
                "publish" => await PublishAsync(arguments, ct),
                "read" => await ReadAsync(arguments, ct),
                "list" => await ListAsync(ct),
                null => ToolResult.Error("The 'action' argument is required: guide, publish, read or list."),
                var other => ToolResult.Error($"Unknown action '{other}'. Use guide, publish, read or list."),
            };
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"The factory host could not be reached: {ex.Message}");
        }
    }

    private async Task<ToolResult> PublishAsync(JsonElement arguments, CancellationToken ct)
    {
        var html = arguments.GetStringOrNull("html");
        if (string.IsNullOrWhiteSpace(html))
            return ToolResult.Error("The 'html' argument is required for publish: the whole page, as one HTML document.");

        Guid? id = null;
        if (arguments.GetStringOrNull("id") is { Length: > 0 } rawId)
        {
            if (!Guid.TryParse(rawId, out var parsed))
                return ToolResult.Error($"'{rawId}' is not a page id. Call list for the thread's pages, or leave id out to publish a new one.");
            id = parsed;
        }

        var response = await host.PublishArtifactAsync(new ArtifactPublishRequest(html, arguments.GetStringOrNull("title"), id), ct);
        if (!response.Accepted || response.Artifact is not { } artifact)
            return ToolResult.Error(response.Error ?? "The factory host refused the page.");

        return ToolResult.Ok(
            $"Published \"{artifact.Title}\" (id {artifact.Id}, version {artifact.LatestVersion}, {(response.Created ? "new" : "updated")}). "
            + "It appears as a card under your answer.");
    }

    private async Task<ToolResult> ReadAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.GetStringOrNull("id") is not { Length: > 0 } rawId || !Guid.TryParse(rawId, out var id))
            return ToolResult.Error("The 'id' argument is required for read: a page id from list or from the brief.");

        int? version = arguments.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        var read = await host.ReadArtifactAsync(id, version, ct);
        if (read.Artifact is not { } artifact)
            return ToolResult.Error($"There is no page with id {id} on this thread.");
        if (read.Html is null)
            return ToolResult.Error($"\"{artifact.Title}\" has no version {read.Version}; its latest is {artifact.LatestVersion}.");

        return ToolResult.Ok($"\"{artifact.Title}\" (id {artifact.Id}), version {read.Version} of {artifact.LatestVersion}:\n{read.Html}");
    }

    private async Task<ToolResult> ListAsync(CancellationToken ct)
    {
        var pages = await host.ListArtifactsAsync(ct);
        return pages.Count == 0
            ? ToolResult.Ok("No pages have been published on this thread yet.")
            : ToolResult.Ok(string.Join('\n', pages.Select(p => $"- \"{p.Title}\" id {p.Id}, version {p.LatestVersion}")));
    }
}
