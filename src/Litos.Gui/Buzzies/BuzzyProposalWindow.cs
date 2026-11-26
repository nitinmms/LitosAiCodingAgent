using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Litos.Gui.Buzzies;

/// <summary>What the user confirmed in <see cref="BuzzyProposalWindow"/>, before anything is saved.</summary>
public sealed record BuzzyProposalApproval(
    string Name,
    string Color,
    BuzzyTemplate Template,
    IReadOnlyList<string> Tools,
    string? Model,
    string? Soul);

/// <summary>
/// The confirmation card for "Create a Buzzy by conversation" (Implement_Buzzies.md, Phase 3): the
/// name, role, model, tools and soul the model proposed, each editable or changeable before creation,
/// plus the folder grants it asked for, which the user picks with a folder dialog because a path the
/// model invented must never become a grant. Returns null when cancelled, and nothing is written in
/// that case. The card is the only thing that creates a Buzzy, so a proposal can never create one on
/// its own.
/// </summary>
public sealed class BuzzyProposalWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush Unselected = new SolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly IBrush ToolBrush = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush AskBrush = new SolidColorBrush(Color.Parse("#D7BA7D"));

    private readonly BuzzyStore _store;
    private readonly BuzzyProposal _proposal;
    private readonly List<(BuzzyTemplate Template, Border Card, BuzzyAvatar Avatar)> _cards = [];
    private readonly List<(string Hex, Border Ring)> _swatches = [];
    private readonly TextBox _nameBox = new() { PlaceholderText = "Name" };
    private readonly TextBox _soulBox = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 110,
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        FontSize = 12,
    };
    private readonly ComboBox _modelBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly WrapPanel _toolsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _grantsPanel = new() { Spacing = 4 };
    private readonly Button _createButton = new() { IsDefault = true };

    private readonly HashSet<string> _tools;
    private readonly List<BuzzyGrant> _grants = [];
    private BuzzyTemplate _template;
    private string _color;

    public BuzzyProposalWindow() : this(new BuzzyStore(), new BuzzyProposal("Buzzy", "buddy", null, [], [], null, ""), [])
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    /// <param name="offeredModels">The provider's models, offered for a per-Buzzy choice; empty
    /// leaves only "the current model", which is always an option.</param>
    public BuzzyProposalWindow(BuzzyStore store, BuzzyProposal proposal, IReadOnlyList<string> offeredModels)
    {
        _store = store;
        _proposal = proposal;
        _template = proposal.TemplateOrDefault;
        _color = _template.Color;
        _tools = [.. proposal.Tools];

        Title = "Create this Buzzy?";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        _nameBox.Text = proposal.Name;
        _soulBox.Text = proposal.Soul ?? _template.Soul;
        BuildModelChoices(offeredModels);

        var templates = new UniformGrid { Columns = BuzzyTemplates.All.Count };
        foreach (var template in BuzzyTemplates.All)
            templates.Children.Add(NewTemplateCard(template));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var (name, hex) in BuzzyTemplates.Palette)
            swatches.Children.Add(NewSwatch(name, hex));

        var addFolder = new Button { Content = "Choose a folder…", HorizontalAlignment = HorizontalAlignment.Left };
        addFolder.Click += async (_, _) => await AddGrantAsync();
        _grantsPanel.Children.Add(addFolder);

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _createButton.Click += (_, _) => Accept();

        Content = new ScrollViewer
        {
            MaxHeight = 720,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Create this Buzzy?", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = proposal.Why, TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontStyle = FontStyle.Italic,
                    },
                    new Border
                    {
                        Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Nothing is saved until you press Create.", FontSize = 12, Foreground = AskBrush },
                                Label("Template"), templates,
                                Label("Name"), _nameBox,
                                Label("Colour"), swatches,
                                Label("Model"), _modelBox,
                                Label("Tools"), _toolsPanel,
                                Label("Soul (how it works)"), _soulBox,
                                Label("Folders it asked for"), new TextBlock
                                {
                                    Text = proposal.FoldersToGrant.Count == 0
                                        ? "None: its own workspace is enough."
                                        : "It asked for these. Pick a real folder for each one you agree to; none is granted otherwise.",
                                    FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
                                },
                                _grantsPanel,
                            },
                        },
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, _createButton },
                    },
                },
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };

        Refresh();
    }

    /// <summary>Shows the card. <paramref name="offeredModels"/> is what the provider offers right now.</summary>
    public static Task<BuzzyProposalApproval?> ShowAsync(Window owner, BuzzyStore store, BuzzyProposal proposal, IReadOnlyList<string> offeredModels) =>
        new BuzzyProposalWindow(store, proposal, offeredModels).ShowDialog<BuzzyProposalApproval?>(owner);

    private void BuildModelChoices(IReadOnlyList<string> offeredModels)
    {
        // "The current model" is represented by null so the Buzzy follows /model afterwards; that is
        // the same behaviour as a Buzzy created without a choice of its own.
        _modelBox.Items.Add(new ComboBoxItem { Content = "Use the app's current model (follows /model)", Tag = null });
        foreach (var id in offeredModels.Distinct(StringComparer.Ordinal))
            _modelBox.Items.Add(new ComboBoxItem { Content = id, Tag = id });
        _modelBox.SelectedIndex = 0;
        if (_proposal.Model is { } proposed)
            foreach (var item in _modelBox.Items.OfType<ComboBoxItem>())
                if (item.Tag is string tag && tag == proposed)
                    _modelBox.SelectedItem = item;
    }

    private Border NewTemplateCard(BuzzyTemplate template)
    {
        var avatar = new BuzzyAvatar(template.Color, 44) { HorizontalAlignment = HorizontalAlignment.Center };
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
        card.PointerPressed += (_, _) =>
        {
            _template = template;
            Refresh();
        };
        ToolTip.SetTip(card, $"{template.Name}: {template.Role}");
        _cards.Add((template, card, avatar));
        return card;
    }

    private Border NewSwatch(string name, string hex)
    {
        var ring = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(3),
            Background = new SolidColorBrush(Color.Parse(hex)), Cursor = new Cursor(StandardCursorType.Hand),
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

    /// <summary>Picks a real folder for a grant the model asked for, and checks it through the same
    /// canonicalisation the guard uses, so a granted folder can't be (or contain) a link.</summary>
    private async Task AddGrantAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose a folder this Buzzy may use",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
            return;

        var chosen = folders[0].TryGetLocalPath();
        var (canonical, error) = WorkspacePathGuard.CanonicalGrant(chosen ?? "", _store.FolderOf(BuzzyStore.Slugify(DisplayName())));
        if (canonical is null)
        {
            _grantsPanel.Children.Insert(0, new TextBlock { Text = $"Can't grant that folder: {error}.", FontSize = 11, Foreground = AskBrush, TextWrapping = TextWrapping.Wrap });
            return;
        }

        // The first click grants read and write; clicking the same one again flips it to read-only.
        var existing = _grants.FindIndex(g => string.Equals(g.Path, canonical, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _grants[existing] = _grants[existing] with { CanWrite = !_grants[existing].CanWrite };
        else
            _grants.Add(new BuzzyGrant(canonical, CanWrite: true));

        RefreshGrants();
    }

    private void RefreshGrants()
    {
        // Everything below the button is rebuilt.
        for (var i = _grantsPanel.Children.Count - 1; i >= 1; i--)
            _grantsPanel.Children.RemoveAt(i);

        foreach (var grant in _grants)
        {
            var label = new TextBlock
            {
                Text = grant.Display, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), VerticalAlignment = VerticalAlignment.Center,
            };
            var toggle = new Button { Content = grant.CanWrite ? "Make read-only" : "Allow writing", FontSize = 11 };
            toggle.Click += (_, _) =>
            {
                var index = _grants.FindIndex(g => g.Path == grant.Path);
                if (index >= 0)
                    _grants[index] = _grants[index] with { CanWrite = !_grants[index].CanWrite };
                RefreshGrants();
            };
            var remove = new Button { Content = "Remove", FontSize = 11 };
            remove.Click += (_, _) =>
            {
                _grants.RemoveAll(g => g.Path == grant.Path);
                RefreshGrants();
            };

            _grantsPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { label, toggle, remove },
            });
        }
    }

    private void Refresh()
    {
        foreach (var (template, card, avatar) in _cards)
        {
            var on = template == _template;
            card.BorderBrush = on ? new SolidColorBrush(Color.Parse("#FFC21A")) : Unselected;
            avatar.SetColor(on ? _color : template.Color);
        }

        foreach (var (hex, ring) in _swatches)
            ring.BorderBrush = hex == _color ? Brushes.White : Brushes.Transparent;

        _createButton.Content = $"Create {DisplayName()}";

        _toolsPanel.Children.Clear();
        foreach (var tool in BuzzyToolPolicy.Grantable.Order(StringComparer.Ordinal))
        {
            var granted = _tools.Contains(tool);
            var asks = tool == BuzzyToolNames.Shell;
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.Parse(granted ? "#2D2D30" : "#1E1E1E")),
                BorderBrush = new SolidColorBrush(Color.Parse(granted ? (asks ? "#D7BA7D" : "#007ACC") : "#3C3C3C")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Opacity = granted ? 1 : 0.55,
                Child = new TextBlock
                {
                    Text = asks ? "shell (asks every time)" : tool,
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                    Foreground = !granted ? Dim : asks ? AskBrush : ToolBrush,
                },
            };
            chip.PointerPressed += (_, _) =>
            {
                if (!_tools.Remove(tool))
                    _tools.Add(tool);
                Refresh();
            };
            ToolTip.SetTip(chip, granted ? "Click to take this tool away" : "Click to grant this tool");
            _toolsPanel.Children.Add(chip);
        }
    }

    private string DisplayName() => string.IsNullOrWhiteSpace(_nameBox.Text) ? _template.Name : _nameBox.Text.Trim();

    private string? SelectedModel => (_modelBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Accept() =>
        Close(new BuzzyProposalApproval(
            DisplayName(),
            _color,
            _template,
            [.. _tools.Order(StringComparer.Ordinal)],
            SelectedModel,
            string.IsNullOrWhiteSpace(_soulBox.Text) ? null : _soulBox.Text.Trim()));

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = Dim };
}