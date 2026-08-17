using System.Text;
using Litos.Agent.Session;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// "/context" breakdown: a text table (category, token count, percentage, block-character bar)
/// standing in for Litos.Gui's ViewContextWindow segmented Border-width bar, displayed in a
/// read-only TextView inside a Dialog. ContextBreakdown.Compute (Litos.Agent) needs no changes —
/// only this rendering is new. Fully read-only, no mutation, no async work inside the dialog
/// itself, so it can use a plain on-thread app.Run rather than AttachDialog's Begin/Iteration
/// pattern (no outer-loop conflict risk without any async work between Begin and End).
/// </summary>
public static class ContextBreakdownDialog
{
    private const int BarWidth = 40;

    public static async Task ShowAsync(IApplication app, ISystemPromptProvider systemPromptProvider, Litos.Agent.Session.Transcript transcript, Litos.Agent.Tools.ToolRegistry tools, int contextLength)
    {
        var systemPrompt = await systemPromptProvider.BuildAsync(tools, transcript.WorkingDirectory, CancellationToken.None);
        var snapshot = ContextBreakdown.Compute(transcript, systemPrompt, tools.Schemas);
        var text = string.Join('\n', RenderLines(snapshot, contextLength));

        var dialog = new Dialog<bool>
        {
            Title = "Context breakdown",
            Width = Dim.Percent(85),
            Height = Dim.Percent(80),
        };

        var textView = new TextView
        {
            Text = text,
            ReadOnly = true,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1,
        };
        dialog.Add(textView);

        var closeButton = new Button { Text = "Close", IsDefault = true };
        closeButton.Accepting += (_, _) => dialog.RequestStop();
        dialog.AddButton(closeButton);

        app.Run(dialog, null);
    }

    /// <summary>
    /// Pure text-table rendering, unit-testable without Terminal.Gui — category, token count,
    /// percentage, and a block-character bar (█/░) in place of Gui's segmented colored Border-width
    /// bar. Sub-items (e.g. per-tool-name breakdown under "Tool results") are indented beneath
    /// their parent category, always expanded (no click-to-expand — a static text dump has no
    /// interactive affordance for that, unlike ViewContextWindow's toggleable rows).
    /// </summary>
    internal static IReadOnlyList<string> RenderLines(ContextBreakdownSnapshot snapshot, int contextLength)
    {
        var lines = new List<string>();

        var fraction = contextLength <= 0 ? 0 : (double)snapshot.TotalEstimatedTokens / contextLength;
        lines.Add($"{snapshot.TotalEstimatedTokens:N0} / {contextLength:N0} tokens ({fraction * 100:0}%)");
        lines.Add(string.Empty);

        foreach (var entry in snapshot.Entries)
        {
            var percentage = snapshot.TotalEstimatedTokens == 0 ? 0 : 100.0 * entry.EstimatedTokens / snapshot.TotalEstimatedTokens;
            var bar = RenderBar(entry.EstimatedTokens, snapshot.TotalEstimatedTokens);
            lines.Add($"{bar}  {entry.Label,-28} {entry.EstimatedTokens,10:N0} tokens  {percentage,5:0.#}%");

            if (entry.SubItems is { Count: > 0 })
            {
                foreach (var sub in entry.SubItems)
                    lines.Add($"{new string(' ', BarWidth + 2)}  {"  " + sub.Label,-26} {sub.EstimatedTokens,10:N0} tokens");
            }
        }

        lines.Add(string.Empty);
        lines.Add(snapshot.LastRealUsageTokens is { } real
            ? $"Estimated from message content, scaled to match last real usage: {real:N0} tokens."
            : "Estimated from message content; no real usage reported yet this session.");

        return lines;
    }

    private static string RenderBar(int tokens, int total)
    {
        var filled = total <= 0 ? 0 : (int)Math.Round(BarWidth * (double)tokens / total);
        filled = Math.Clamp(filled, 0, BarWidth);
        var sb = new StringBuilder(BarWidth);
        sb.Append('█', filled);
        sb.Append('░', BarWidth - filled);
        return sb.ToString();
    }
}
