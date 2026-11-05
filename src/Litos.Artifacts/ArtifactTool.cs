using System.Globalization;
using System.Text;
using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Artifacts;

/// <summary>
/// The model-facing <c>artifact</c> tool: publish a self-contained HTML page the user opens in
/// their browser, then read, list or delete what was published. Same action-discriminated shape
/// as Claude Code's Artifact tool; see ReadMe_ArtifactsFeature.md §3.
///
/// Not approval-gated, like share_file: publishing copies a file the model could already read into
/// a store served only on loopback, and delete removes only that copy, never the source file.
///
/// Publish result text is the "artifact published" signal the UI sees — there is no dedicated
/// event — so the URL always ends its own line: the webview's bare-URL linkifier stops only at
/// whitespace or ')', and trailing punctuation would otherwise become part of the link.
/// </summary>
/// <param name="baseUrl">Read on every call, never at construction: the host's port is only known
/// after it starts listening, which is after DI registration (see Litos.VsCodeHost's Program.cs).</param>
public sealed class ArtifactTool(ArtifactStore store, Func<string> baseUrl) : ITool
{
    public const string ToolName = "artifact";

    public const long MaxHtmlBytes = 10 * 1024 * 1024;

    /// <summary>A read returns at most this much of a page, so one large artifact can't flood the context.</summary>
    public const int MaxReadChars = 100_000;

    public string Name => ToolName;

    public string Description =>
        "Publish a self-contained HTML page (a report, dashboard, diagram, explainer, or small interactive tool) "
        + "that the user opens in their browser from a link. Before writing or rewriting a page, load the "
        + $"`{ArtifactSkills.SkillName}` skill with the skill tool if its guidance is not already in your context. "
        + "Write the page to a file first (absolute path), then call this with action \"publish\" and that file_path. "
        + "To change a published page, edit the same file and publish the same file_path again: it becomes a new "
        + "version at the same URL, and the user refreshes their browser tab. "
        + "Other actions: \"read\" (id, optional version), \"list\", \"delete\" (id).";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                @enum = new[] { "publish", "read", "list", "delete" },
                description = "What to do. Defaults to publish.",
            },
            file_path = new { type = "string", description = "publish: absolute path of the .html file to publish." },
            title = new { type = "string", description = "publish: the artifact's name. Defaults to the page's <title>." },
            id = new { type = "string", description = "publish: an existing artifact to add a version to. read/delete: the artifact." },
            version = new { type = "integer", description = "read: a specific version. Defaults to the latest." },
        },
    });

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) =>
        (arguments.GetStringOrNull("action") ?? "publish") switch
        {
            "publish" => PublishAsync(arguments, ct),
            "read" => ReadAsync(arguments, ct),
            "list" => ListAsync(ct),
            "delete" => DeleteAsync(arguments, ct),
            var other => Task.FromResult(ToolResult.Error($"Unknown action '{other}'. Use publish, read, list or delete.")),
        };

    private async Task<ToolResult> PublishAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringOrNull("file_path");
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("The 'file_path' argument is required for publish.");
        if (!Path.IsPathFullyQualified(path))
            return ToolResult.Error($"file_path must be an absolute path: {path}");

        path = Path.GetFullPath(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".html" or ".htm"))
            return ToolResult.Error($"Only .html or .htm files can be published as an artifact: {path}");
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var size = new FileInfo(path).Length;
        if (size > MaxHtmlBytes)
            return ToolResult.Error($"{path} is {size / (1024 * 1024)}MB, which exceeds the {MaxHtmlBytes / (1024 * 1024)}MB artifact limit.");

        var html = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        var title = ArtifactTitles.Clean(arguments.GetStringOrNull("title")) ?? ArtifactTitles.FromHtml(html) ?? Path.GetFileNameWithoutExtension(path);

        var id = arguments.GetStringOrNull("id");
        var published = await store.PublishAsync(html, title, path, id, ct);
        if (published is null)
            return ToolResult.Error($"No artifact with id '{id}'. Omit id to create a new artifact, or call list to find it.");

        var (meta, created) = published;
        var next = created
            ? "The user opens this link in their browser. Publishing the same file_path again updates the page at this same URL."
            : "Same URL as before; the user refreshes their browser tab to see this version.";
        return ToolResult.Ok(
            $"Published artifact \"{meta.Title}\" (id {meta.Id}, version {meta.CurrentVersion}, {(created ? "new" : "updated")}).\n"
            + $"Open: {UrlOf(meta.Id)}\n"
            + next);
    }

    private async Task<ToolResult> ReadAsync(JsonElement arguments, CancellationToken ct)
    {
        var id = arguments.GetStringOrNull("id");
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error("The 'id' argument is required for read.");

        int? version = arguments.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;
        if (await store.TryGetAsync(id, version, ct) is not { } found)
            return ToolResult.Error(version is null
                ? $"No artifact with id '{id}'."
                : $"Artifact '{id}' has no stored version {version}.");

        var html = await File.ReadAllTextAsync(found.FilePath, Encoding.UTF8, ct);
        var shownVersion = version ?? found.Meta.CurrentVersion;
        var header = $"Artifact \"{found.Meta.Title}\" (id {found.Meta.Id}), version {shownVersion} of {found.Meta.CurrentVersion}:\n";
        return html.Length <= MaxReadChars
            ? ToolResult.Ok(header + html)
            : ToolResult.Ok(header + html[..MaxReadChars]
                + string.Create(CultureInfo.InvariantCulture, $"\n[truncated: showing {MaxReadChars:N0} of {html.Length:N0} characters]"));
    }

    private async Task<ToolResult> ListAsync(CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        if (all.Count == 0)
            return ToolResult.Ok("No artifacts published yet.");

        var lines = all.Select(m =>
            string.Create(CultureInfo.InvariantCulture, $"- \"{m.Title}\" id {m.Id}, version {m.CurrentVersion}, updated {m.UpdatedAt:u}: {UrlOf(m.Id)}"));
        return ToolResult.Ok(string.Join('\n', lines));
    }

    private async Task<ToolResult> DeleteAsync(JsonElement arguments, CancellationToken ct)
    {
        var id = arguments.GetStringOrNull("id");
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error("The 'id' argument is required for delete.");

        if (await store.DeleteAsync(id, ct) is not { } meta)
            return ToolResult.Error($"No artifact with id '{id}'.");

        return ToolResult.Ok($"Deleted artifact \"{meta.Title}\" (id {id}) and all its versions. Its link no longer works; the source file was not touched.");
    }

    private string UrlOf(string id) => $"{baseUrl().TrimEnd('/')}/artifacts/{id}";
}
