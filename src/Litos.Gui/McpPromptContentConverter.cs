using ModelContextProtocol.Protocol;

namespace Litos.Gui;

/// <summary>
/// Converts a fetched GetPromptResult into plain text so a selected MCP prompt can be run
/// through the exact same RunTurnAsync path MainWindow.SubmitAsync already uses for typed text
/// (see BuildTurnContent) — never a bypassed/direct invocation, matching Claude Code's own
/// prompt UX where the server-authored text stands in for what the user would otherwise have
/// typed. Deliberately not using ModelContextProtocol.AIContentExtensions.ToChatMessages: that
/// produces Microsoft.Extensions.AI.ChatMessage, a third message shape unrelated to both MCP's
/// own PromptMessage and Litos's own ChatMessage, which would need its own second conversion
/// into Litos.Agent.Messages.ContentBlock anyway.
/// </summary>
internal static class McpPromptContentConverter
{
    internal readonly record struct ConversionResult(string Text, IReadOnlyList<int> SkippedBlockIndexes);

    /// <summary>
    /// Only TextContentBlock content is extracted — a prompt message's Role is ignored, since
    /// Litos's turn-content shape (a single flat IReadOnlyList&lt;ContentBlock&gt; per outgoing
    /// turn, see BuildTurnContent) has no slot for a seeded multi-role exchange; every message's
    /// text is joined in order into one block, as if the user had said all of it themselves. Any
    /// message whose Content isn't text (image/resource content, etc.) is skipped rather than
    /// thrown on, with its 0-based index recorded so the caller can report how many were dropped.
    /// </summary>
    internal static ConversionResult Convert(GetPromptResult result)
    {
        var segments = new List<string>();
        var skipped = new List<int>();

        for (var i = 0; i < result.Messages.Count; i++)
        {
            if (result.Messages[i].Content is TextContentBlock text)
                segments.Add(text.Text);
            else
                skipped.Add(i);
        }

        return new ConversionResult(string.Join("\n\n", segments), skipped);
    }
}
