using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Litos.Gui.Buzzies;

/// <summary>
/// A Buzzy's access settings (Implement_Buzzies.md, Phase 3): the folders it may read or change
/// outside its workspace, and the model it runs on. Each folder is picked with the system dialog and
/// checked through <see cref="WorkspacePathGuard.CanonicalGrant"/> on the way in, so nothing on the
/// list is a link or inside the Buzzy's own folder. Changes are saved as they are made; the window
/// returns the current profile so the sidebar and the next turn use it straight away.
/// </summary>
public sealed class BuzzySettingsWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#D7BA7D"));

    private readonly BuzzyStore _store;
    private readonly IReadOnlyList<string> _offeredModels;
    private readonly StackPanel _grantsPanel = new() { Spacing = 6 };
    private readonly TextBlock _errorText = new() { FontSize = 11, Foreground = Warning, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ComboBox _modelBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    private BuzzyProfile _profile;

    public BuzzySettingsWindow() : this(new BuzzyStore(), new BuzzyProfile("buzzy", "Buzzy", "buddy", "#FFC21A", [], DateTimeOffset.Now), [])
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    public BuzzySettingsWindow(BuzzyStore store, BuzzyProfile profile, IReadOnlyList<string> offeredModels)
    {
        _store = store;
        _profile = profile;
        _offeredModels = offeredModels;

        Title = $"{profile.Name}: access and model";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        BuildModelChoices();

        var addFolder = new Button { Content = "Grant a folder…", HorizontalAlignment = HorizontalAlignment.Left };
        addFolder.Click += async (_, _) => await AddGrantAsync();
        _grantsPanel.Children.Add(addFolder);

        var close = new Button { Content = "Done", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(_profile);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"{profile.Name}: access and model", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new Border
                {
                    Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Model", FontSize = 12, Foreground = Dim },
                            _modelBox,
                            new TextBlock
                            {
                                Text = "A Buzzy with its own model keeps it whichever model the plain chat is on, including in its routines.",
                                FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
                            },
                        },
                    },
                },
                new Border
                {
                    Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Folders outside its workspace", FontSize = 12, Foreground = Dim },
                            new TextBlock
                            {
                                Text = "It can always use its own workspace. A folder granted here is reachable by its file tools: "
                                     + "read only, or read and write. Its routines only get read-only grants, since nobody is watching.",
                                FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
                            },
                            _grantsPanel,
                            _errorText,
                        },
                    },
                },
                new TextBlock
                {
                    Text = "Its shell, if it has one, is not confined to these folders: the approval card on every command is the control.",
                    FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
                },
                close,
            },
        };

        RefreshGrants();
    }

    public static Task<BuzzyProfile?> ShowAsync(Window owner, BuzzyStore store, BuzzyProfile profile, IReadOnlyList<string> offeredModels) =>
        new BuzzySettingsWindow(store, profile, offeredModels).ShowDialog<BuzzyProfile?>(owner);

    private void BuildModelChoices()
    {
        _modelBox.Items.Add(new ComboBoxItem { Content = "Follow the app's current model (/model)", Tag = null });
        foreach (var id in _offeredModels.Distinct(StringComparer.Ordinal))
            _modelBox.Items.Add(new ComboBoxItem { Content = id, Tag = id });

        _modelBox.SelectedIndex = 0;
        if (_profile.Model is { } own)
        {
            var found = false;
            foreach (var item in _modelBox.Items.OfType<ComboBoxItem>())
                if (item.Tag is string tag && tag == own)
                {
                    _modelBox.SelectedItem = item;
                    found = true;
                }

            // A model picked with /model and then dropped by the provider still shows, so the choice
            // isn't silently lost; the user can see it and change it.
            if (!found)
            {
                var item = new ComboBoxItem { Content = $"{own} (not in this provider's list)", Tag = own };
                _modelBox.Items.Add(item);
                _modelBox.SelectedItem = item;
            }
        }

        _modelBox.SelectionChanged += (_, _) => SaveModel();
    }

    private void SaveModel()
    {
        var chosen = (_modelBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (chosen == _profile.Model)
            return;
        try
        {
            _profile = _store.SetModel(_profile.Id, chosen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't save the model: {ex.Message}");
        }
    }

    private async Task AddGrantAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder this Buzzy may use",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
            return;

        var chosen = folders[0].TryGetLocalPath();
        var (canonical, error) = WorkspacePathGuard.CanonicalGrant(chosen ?? "", _store.FolderOf(_profile.Id));
        if (canonical is null)
        {
            ShowError($"Can't grant that folder: {error}.");
            return;
        }

        _errorText.IsVisible = false;
        try
        {
            _profile = _store.Grant(_profile.Id, canonical, canWrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't save the grant: {ex.Message}");
        }
        RefreshGrants();
    }

    private void RefreshGrants()
    {
        for (var i = _grantsPanel.Children.Count - 1; i >= 1; i--)
            _grantsPanel.Children.RemoveAt(i);

        if (_profile.Folders.Count == 0)
            _grantsPanel.Children.Add(new TextBlock { Text = "No folders granted.", FontSize = 11, Foreground = Dim });
        foreach (var grant in _profile.Folders)
            _grantsPanel.Children.Add(GrantRow(grant));
    }

    private Control GrantRow(BuzzyGrant grant)
    {
        var toggle = new Button { Content = grant.CanWrite ? "Read and write" : "Read only", FontSize = 11 };
        toggle.Click += (_, _) => Change(grant, !grant.CanWrite);
        var remove = new Button { Content = "Remove", FontSize = 11 };
        remove.Click += (_, _) => Revoke(grant);

        var label = new TextBlock
        {
            Text = grant.Path, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 340,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(label, "Click the button to allow or stop changes in this folder.");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { label, toggle, remove },
        };
    }

    private void Change(BuzzyGrant grant, bool canWrite)
    {
        try
        {
            _profile = _store.Grant(_profile.Id, grant.Path, canWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't save the grant: {ex.Message}");
        }
        RefreshGrants();
    }

    private void Revoke(BuzzyGrant grant)
    {
        try
        {
            _profile = _store.RevokeGrant(_profile.Id, grant.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't remove the grant: {ex.Message}");
        }
        RefreshGrants();
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.IsVisible = true;
    }
}