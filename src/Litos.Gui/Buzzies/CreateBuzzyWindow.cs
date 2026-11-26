using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Litos.Gui.Buzzies;

/// <summary>What the user confirmed in <see cref="CreateBuzzyWindow"/>.</summary>
public sealed record CreateBuzzyRequest(BuzzyTemplate Template, string Name, string Color);

/// <summary>
/// "Create a Buzzy": pick a template, then a name and a colour, and confirm the workspace and
/// tools it will get before anything is saved (ReadMe_Buzzies.md §1A: creation shows the proposed
/// settings for confirmation). Built in code, like ToolCallRow and ApprovalCard, since it is one
/// small dialog. Returns null when cancelled.
/// </summary>
public sealed class CreateBuzzyWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#FFC21A"));
    private static readonly IBrush Unselected = new SolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly IBrush ToolBrush = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush AskBrush = new SolidColorBrush(Color.Parse("#D7BA7D"));

    private readonly BuzzyStore _store;
    private readonly List<(BuzzyTemplate Template, Border Card, BuzzyAvatar Avatar)> _cards = [];
    private readonly List<(string Hex, Border Ring)> _swatches = [];
    private readonly TextBox _nameBox = new() { PlaceholderText = "Name" };
    private readonly TextBlock _workspaceText = new() { FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _toolsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Button _createButton = new() { IsDefault = true };

    private BuzzyTemplate _template = BuzzyTemplates.Buddy;
    private string _color = BuzzyTemplates.Buddy.Color;
    private bool _nameEdited;

    public CreateBuzzyWindow() : this(new BuzzyStore())
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    public CreateBuzzyWindow(BuzzyStore store)
    {
        _store = store;
        Title = "Create a Buzzy";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        var templates = new UniformGrid { Columns = BuzzyTemplates.All.Count };
        foreach (var template in BuzzyTemplates.All)
            templates.Children.Add(NewTemplateCard(template));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var (name, hex) in BuzzyTemplates.Palette)
            swatches.Children.Add(NewSwatch(name, hex));

        _nameBox.TextChanged += (_, _) =>
        {
            _nameEdited = !string.IsNullOrWhiteSpace(_nameBox.Text) && _nameBox.Text != _template.Name;
            Refresh();
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _createButton.Click += (_, _) => Accept();

        var confirm = new Border
        {
            Background = Panel,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Label("Please confirm"),
                    Row("Workspace", _workspaceText),
                    Row("Model", new TextBlock { Text = "The model chosen with /model, shared with the plain chat.", TextWrapping = TextWrapping.Wrap }),
                    Row("Can reach", new TextBlock { Text = "Only files inside its workspace. No MCP tools, no PTC.", TextWrapping = TextWrapping.Wrap }),
                    _toolsPanel,
                },
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Create a Buzzy", FontSize = 18, FontWeight = FontWeight.SemiBold },
                Label("Template"),
                templates,
                Label("Name"),
                _nameBox,
                Label("Colour"),
                swatches,
                confirm,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _createButton },
                },
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };

        PickTemplate(BuzzyTemplates.Buddy);
    }

    public static Task<CreateBuzzyRequest?> ShowAsync(Window owner, BuzzyStore store) =>
        new CreateBuzzyWindow(store).ShowDialog<CreateBuzzyRequest?>(owner);

    private Border NewTemplateCard(BuzzyTemplate template)
    {
        var avatar = new BuzzyAvatar(template.Color, 52) { HorizontalAlignment = HorizontalAlignment.Center };
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Unselected,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 10),
            Margin = new Thickness(4, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    avatar,
                    new TextBlock { Text = template.Name, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = template.Role, FontSize = 11, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        card.PointerPressed += (_, _) => PickTemplate(template);
        ToolTip.SetTip(card, $"{template.Name}: {template.Role}");
        _cards.Add((template, card, avatar));
        return card;
    }

    private Border NewSwatch(string name, string hex)
    {
        var ring = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(3),
            Background = new SolidColorBrush(Color.Parse(hex)),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(ring, name);
        ring.PointerPressed += (_, _) =>
        {
            _color = hex;
            Refresh();
        };
        _swatches.Add((hex, ring));
        return ring;
    }

    private void PickTemplate(BuzzyTemplate template)
    {
        _template = template;
        _color = template.Color;
        if (!_nameEdited)
            _nameBox.Text = template.Name;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (template, card, avatar) in _cards)
        {
            var on = template == _template;
            card.BorderBrush = on ? Selected : Unselected;
            avatar.SetColor(on ? _color : template.Color);
        }

        foreach (var (hex, ring) in _swatches)
            ring.BorderBrush = hex == _color ? Brushes.White : Brushes.Transparent;

        var name = DisplayName();
        _workspaceText.Text = Path.Combine(_store.RootDirectory, BuzzyStore.Slugify(name), BuzzyStore.WorkspaceFolderName);
        _createButton.Content = $"Create {name}";

        _toolsPanel.Children.Clear();
        foreach (var tool in _template.Tools)
        {
            var asks = tool == BuzzyToolNames.Shell;
            _toolsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Child = new TextBlock
                {
                    Text = asks ? "shell (asks every time)" : tool,
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                    Foreground = asks ? AskBrush : ToolBrush,
                },
            });
        }
    }

    private string DisplayName() => string.IsNullOrWhiteSpace(_nameBox.Text) ? _template.Name : _nameBox.Text.Trim();

    private void Accept() => Close(new CreateBuzzyRequest(_template, DisplayName(), _color));

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = Dim };

    private static Grid Row(string label, Control value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
        grid.Children.Add(new TextBlock { Text = label, Foreground = Dim });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }
}
