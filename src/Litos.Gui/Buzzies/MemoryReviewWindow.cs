using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Litos.Gui.Buzzies;

/// <summary>
/// Review the memories a Buzzy proposed during routine runs: each with where it came from and
/// when, and Keep (adds it to MEMORY.md) or Discard. Nothing a run read reaches the Buzzy's
/// permanent memory without this step (Implement_Buzzies.md, Phase 2).
/// </summary>
public sealed class MemoryReviewWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#252526"));

    private readonly MemoryProposals _proposals;
    private readonly string _buzzyId;
    private readonly StackPanel _list = new() { Spacing = 8 };

    public MemoryReviewWindow() : this(new MemoryProposals(new BuzzyStore()), "", "Buzzy")
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    public MemoryReviewWindow(MemoryProposals proposals, string buzzyId, string buzzyName)
    {
        _proposals = proposals;
        _buzzyId = buzzyId;
        Title = $"{buzzyName}'s proposed memories";
        Width = 620;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = $"{buzzyName} wanted to remember these during routines nobody was watching. Keep only what is true and useful: " +
                               "a kept memory goes into every future conversation.",
                        TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 12,
                    },
                    _list,
                },
            },
        };
        Refresh();
    }

    public static Task ShowAsync(Window owner, MemoryProposals proposals, string buzzyId, string buzzyName) =>
        new MemoryReviewWindow(proposals, buzzyId, buzzyName).ShowDialog(owner);

    private void Refresh()
    {
        _list.Children.Clear();
        var pending = _proposals.List(_buzzyId);
        if (pending.Count == 0)
            _list.Children.Add(new TextBlock { Text = "Nothing waiting for review.", Foreground = Dim });

        foreach (var proposal in pending)
        {
            var keep = new Button { Content = "Keep" };
            keep.Click += (_, _) =>
            {
                _proposals.Keep(_buzzyId, proposal.Id);
                Refresh();
            };
            var discard = new Button { Content = "Discard" };
            discard.Click += (_, _) =>
            {
                _proposals.Discard(_buzzyId, proposal.Id);
                Refresh();
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = proposal.Note, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"From {proposal.Source} · {RoutinesWindow.Friendly(proposal.ProposedAt)}", Foreground = Dim, FontSize = 12 },
                },
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top, Children = { keep, discard } };
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            _list.Children.Add(new Border { Background = CardBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = grid });
        }
    }
}
