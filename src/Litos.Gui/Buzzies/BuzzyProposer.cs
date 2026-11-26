using System.Text;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;

namespace Litos.Gui.Buzzies;

/// <summary>A Buzzy config a Buzzy proposed, for the user to confirm before anything is saved
/// (ReadMe_Buzzies.md §1A: creation must show the proposed settings first).</summary>
public sealed record BuzzyProposal(
    string Name,
    string Template,
    string? Model,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> FoldersToGrant,
    string? Soul,
    string Why)
{
    /// <summary>The template it matched, or Buddy when it named none this build knows.</summary>
    public BuzzyTemplate TemplateOrDefault => BuzzyTemplates.Find(Template) ?? BuzzyTemplates.Buddy;
}

/// <summary>
/// "Create a Buzzy by conversation" (Implement_Buzzies.md, Phase 3): the user describes the coworker
/// they want in the chat, one tool-less model call turns it into a proposal, and the window shows it
/// in a card. Nothing is written until the user confirms the card: the proposal is data, never an
/// instruction to create anything (ReadMe_Buzzies.md §7).
///
/// It may propose tool grants only from <see cref="BuzzyToolPolicy.Grantable"/> and a model only as
/// an exact id the provider offers; anything else is dropped, so a confused model can't widen a
/// Buzzy's reach or point it at a model that doesn't exist. Folder grants are proposed by name only
/// and have to be picked by the user in a folder dialog, since a path a model invents must never
/// become a grant.
/// </summary>
public static class BuzzyProposer
{
    internal const string Instructions = """
        You help someone in a desktop app create an AI coworker called a Buzzy. You are given what
        they asked for, the templates that exist, the tools a Buzzy may be granted and the models
        their provider offers.

        Reply with ONLY a JSON object, no commentary and no code fence, with these fields:
        - "name": a short, friendly first name for the coworker (one or two words).
        - "template": one template id from the list you are given.
        - "model": one model id copied exactly from the list, or null to use the app's current model.
        - "tools": the tool names it needs, copied exactly from the allowed list. Include only what
          its job needs. "shell" lets it run commands, but only with the user's approval each time,
          so include it only when the job is about code or the command line.
        - "folders": folder names it would need access to, as short descriptions such as
          "invoice folder" or "the project repository". Use an empty list when its own workspace is
          enough. Never invent a full path.
        - "soul": two to five short lines describing how this coworker should work: its role, what
          it should always do, and anything the user asked it to keep to.
        - "why": one short sentence, to the user, on why those tools and that template fit.

        Keep it minimal and honest. Do not grant tools the job doesn't need, never invent tool or
        model names, and do not include secrets or personal data in the soul.
        """;

    public static async Task<BuzzyProposal> ProposeAsync(
        string request, IReadOnlyList<string> offeredModels, IChatProvider provider, string model, CancellationToken ct)
    {
        var prompt = new StringBuilder(Instructions)
            .Append("\n\n## What the user asked for\n").Append(request.ReplaceLineEndings(" ").Trim()).Append('\n')
            .Append("\n## Templates\n");
        foreach (var template in BuzzyTemplates.All)
            prompt.Append("- ").Append(template.Id).Append(": ").Append(template.Role).Append('\n');

        prompt.Append("\n## Allowed tools\n");
        foreach (var tool in BuzzyToolPolicy.Grantable.Order(StringComparer.Ordinal))
            prompt.Append("- ").Append(tool).Append('\n');

        prompt.Append("\n## Models the provider offers\n");
        if (offeredModels.Count == 0)
            prompt.Append("(the app's current model only)\n");
        foreach (var offered in offeredModels)
            prompt.Append("- ").Append(offered).Append('\n');

        var text = new StringBuilder();
        await foreach (var evt in provider.StreamAsync(new ChatRequest([ChatMessage.User(prompt.ToString())], [], model), ct))
            if (evt is TextDelta delta)
                text.Append(delta.Text);

        return Parse(text.ToString(), request, offeredModels);
    }

    /// <summary>Reads the model's JSON, tolerating a code fence, and narrows it to what a Buzzy may
    /// actually be given. Anything missing or unusable falls back to the template's own defaults, so
    /// a bad reply still produces a card the user can fix rather than an error.</summary>
    internal static BuzzyProposal Parse(string output, string request, IReadOnlyList<string> offeredModels)
    {
        var text = StripFence(output);
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // A chatty model can wrap the object in prose; take the outermost braces if there are any.
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try { document = JsonDocument.Parse(text[start..(end + 1)]); }
                catch (JsonException) { /* fall through to the defaults below */ }
            }
        }

        using (document)
        {
            var root = document?.RootElement;
            var template = BuzzyTemplates.Find(Field(root, "template") ?? "") ?? BuzzyTemplates.Buddy;

            var tools = new List<string>();
            if (root is { } r && r.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array)
                foreach (var entry in toolsElement.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } toolName
                        && BuzzyToolPolicy.Grantable.Contains(toolName) && !tools.Contains(toolName))
                        tools.Add(toolName);
            if (tools.Count == 0)
                tools = [.. template.Tools];

            // A model it names has to be one the provider really offers (exact id, as the prompt
            // asked), otherwise the Buzzy follows the global model instead.
            var requested = Field(root, "model");
            var model = requested is not null && offeredModels.Contains(requested, StringComparer.Ordinal) ? requested : null;

            var folders = new List<string>();
            if (root is { } rr && rr.TryGetProperty("folders", out var foldersElement) && foldersElement.ValueKind == JsonValueKind.Array)
                foreach (var entry in foldersElement.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String && entry.GetString()?.Trim() is { Length: > 0 } folder && !folders.Contains(folder))
                        folders.Add(folder);

            var name = Field(root, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = template.Name;
            if (name.Length > 40)
                name = name[..40].TrimEnd();

            var soul = Field(root, "soul")?.Trim();
            var why = Field(root, "why")?.Trim();
            if (string.IsNullOrWhiteSpace(why))
                why = $"A {template.Role.ToLowerInvariant()} for: {Shorten(request, 80)}";

            return new BuzzyProposal(name, template.Id, model, tools, folders, string.IsNullOrWhiteSpace(soul) ? null : soul, why);
        }
    }

    private static string StripFence(string output)
    {
        var text = output.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;
        var firstLineEnd = text.IndexOf('\n');
        text = firstLineEnd < 0 ? "" : text[(firstLineEnd + 1)..];
        var fence = text.LastIndexOf("```", StringComparison.Ordinal);
        return (fence >= 0 ? text[..fence] : text).Trim();
    }

    private static string? Field(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}