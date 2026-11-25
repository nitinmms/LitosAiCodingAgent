using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Tools.Shell;

namespace Litos.Gui;

/// <summary>How a pending approval ended, for the card's settled label.</summary>
internal enum ApprovalOutcome { Approved, Denied, TimedOut }

/// <summary>
/// The in-transcript "Approval needed" card for an MCP tool call on a server set to Ask — the Gui
/// counterpart of the VS Code webview's approval entry (webviewContent.ts addApprovalEntry). The
/// call is suspended inside McpAwareApprovalGate on a PendingApprovalStore entry until
/// <see cref="Decided"/> is resolved by the caller, or the store's 10-minute timeout auto-denies.
/// Once settled the buttons are replaced by a one-line outcome so the transcript keeps a record of
/// what was allowed. Same construction style as ToolCallRow: plain controls, no ViewModel.
/// </summary>
internal sealed class ApprovalCard
{
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#3A3220"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#D7BA7D"));
    private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));

    private readonly StackPanel _buttons;
    private readonly TextBlock _outcomeText;

    public Guid ApprovalId { get; }

    /// <summary>The control MainWindow adds to TranscriptPanel.</summary>
    public Control Root { get; }

    /// <summary>Raised once, when the user clicks Approve or Deny.</summary>
    public event Action<Guid, ApprovalDecision>? Decided;

    private bool IsSettled { get; set; }

    public ApprovalCard(PendingApproval approval)
    {
        ApprovalId = approval.Id;

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Approval needed", FontWeight = FontWeight.Bold, Foreground = AccentBrush });
        panel.Children.Add(new TextBlock
        {
            Text = Headline(approval.Preview),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.WhiteSmoke,
        });
        if (!string.IsNullOrWhiteSpace(approval.Preview.DiffOrCommand))
        {
            panel.Children.Add(new TextBlock
            {
                Text = approval.Preview.DiffOrCommand,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = DimTextBrush,
            });
        }

        var approve = new Button { Content = "Approve" };
        var deny = new Button { Content = "Deny" };
        approve.Click += (_, _) => Decide(ApprovalDecision.Approve);
        deny.Click += (_, _) => Decide(ApprovalDecision.Deny);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _buttons.Children.Add(approve);
        _buttons.Children.Add(deny);
        panel.Children.Add(_buttons);

        _outcomeText = new TextBlock { IsVisible = false, FontStyle = FontStyle.Italic, Foreground = DimTextBrush };
        panel.Children.Add(_outcomeText);

        Root = new Border
        {
            Background = CardBrush,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = panel,
        };
    }

    /// <summary>Replaces the buttons with the outcome. Idempotent: the first outcome wins, so a
    /// store Resolved event arriving after the user's own click doesn't relabel the card.</summary>
    public void Settle(ApprovalOutcome outcome)
    {
        if (IsSettled)
            return;

        IsSettled = true;
        _buttons.IsVisible = false;
        _outcomeText.Text = OutcomeLabel(outcome);
        _outcomeText.IsVisible = true;
    }

    private void Decide(ApprovalDecision decision)
    {
        if (IsSettled)
            return;

        Settle(decision == ApprovalDecision.Deny ? ApprovalOutcome.Denied : ApprovalOutcome.Approved);
        Decided?.Invoke(ApprovalId, decision);
    }

    /// <summary>"tool — summary", or just the tool when the preview carries no summary. Pure and
    /// internal so it's testable without an Avalonia Application.</summary>
    internal static string Headline(ToolInvocationPreview preview) =>
        string.IsNullOrWhiteSpace(preview.Summary) || preview.Summary == preview.ToolName
            ? preview.ToolName
            : $"{preview.ToolName} — {preview.Summary}";

    internal static string OutcomeLabel(ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.Approved => "✓ Approved",
        ApprovalOutcome.Denied => "✗ Denied",
        _ => "⏱ Timed out — denied automatically",
    };
}
