using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Litos.Gui.Buzzies;

/// <summary>
/// Edit what a Buzzy knows about itself, inside the app (Implement_Buzzies.md, Phase 3): its soul
/// (SOUL.md), its memory (MEMORY.md) and its skills (skills/*/SKILL.md), each as plain Markdown,
/// with Save, Reload and Revert. This replaces "open it in Notepad": a Buzzy's identity and memory
/// are the two files that go into every prompt, so they are worth editing somewhere that says so.
///
/// Nothing is written until Save is pressed, and the files stay the single source of truth — this
/// window reads and writes the same SOUL.md, MEMORY.md and SKILL.md that the prompt provider, the
/// remember tool and the skill tool use, so there is no second copy to fall out of step.
/// </summary>
public sealed class BuzzyEditorWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#FFC21A"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#D7BA7D"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly BuzzyStore _store;
    private readonly BuzzySkillLibrary _skills;
    private readonly string _buzzyId;

    private readonly TextBox _soulBox = Editor();
    private readonly TextBox _memoryBox = Editor();
    private readonly TextBox _skillBodyBox = Editor();
    private readonly TextBox _skillNameBox = new() { PlaceholderText = "Skill name" };
    private readonly TextBox _skillDescriptionBox = new() { PlaceholderText = "One line: what it does and when to use it" };
    private readonly ListBox _skillList = new() { Height = 120 };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap };

    /// <summary>The skill folder being edited, or null for a new one.</summary>
    private string? _folder;
    private bool _loadingSkills;

    /// <summary>True while text is being written into the boxes from disk. Setting Text raises
    /// TextChanged, so without this every load would mark the window dirty and warn on a close that
    /// had nothing to lose.</summary>
    private bool _loading;
    private bool _dirty;

    /// <summary>False until the files have been read once. Save refuses to run before then: a failed
    /// read leaves the boxes empty, and writing them back would erase the soul and memory instead of
    /// reporting the error.</summary>
    private bool _loaded;

    public BuzzyEditorWindow() : this(new BuzzyStore(), new BuzzyProfile("buzzy", "Buzzy", "buddy", "#FFC21A", [], DateTimeOffset.Now))
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    public BuzzyEditorWindow(BuzzyStore store, BuzzyProfile profile)
    {
        _store = store;
        _skills = new BuzzySkillLibrary(store);
        _buzzyId = profile.Id;

        Title = $"{profile.Name}: soul, memory and skills";
        Width = 760;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        _soulBox.TextChanged += (_, _) => MarkDirty();
        _memoryBox.TextChanged += (_, _) => MarkDirty();
        _skillBodyBox.TextChanged += (_, _) => MarkDirty();
        _skillNameBox.TextChanged += (_, _) => MarkDirty();
        _skillDescriptionBox.TextChanged += (_, _) => MarkDirty();
        _skillList.SelectionChanged += (_, _) => LoadSelectedSkill();

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Soul", Content = Tab(SoulHelp, _soulBox) },
                new TabItem { Header = "Memory", Content = Tab(MemoryHelp, _memoryBox) },
                new TabItem { Header = "Skills", Content = SkillsTab() },
            },
        };

        var save = new Button { Content = "Save", IsDefault = true };
        save.Click += (_, _) => Save();
        var reload = new Button { Content = "Reload" };
        reload.Click += async (_, _) => await LoadAsync(saveFirst: false);
        var revert = new Button { Content = "Revert" };
        revert.Click += (_, _) => Revert();
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Spacing = 8,
                    Children =
                    {
                        _status,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { revert, reload, save, close },
                        },
                    },
                },
                tabs,
            },
        };

        Closing += async (_, e) =>
        {
            if (!_dirty)
                return;
            // Cancelling and closing again is the only way out of the confirmation, so an
            // accidental close never silently discards someone's soul or memory edit.
            e.Cancel = true;
            var discard = await ConfirmationDialog.ShowAsync(this, "Discard unsaved changes?",
                "Your edits to the soul, memory or a skill have not been saved. Closing now discards them.");
            if (discard)
            {
                _dirty = false;
                Close();
            }
        };

        _ = LoadAsync(saveFirst: false);
    }

    public static Task ShowAsync(Window owner, BuzzyStore store, BuzzyProfile profile) =>
        new BuzzyEditorWindow(store, profile).ShowDialog(owner);

    private const string SoulHelp =
        "Who this Buzzy is and how it works. This goes into every prompt it is given. Keep it short: "
        + "a role, what it should always do, and anything it must never do. Every Buzzy can use the "
        + "artifact and skill tools whatever this says.";

    private const string MemoryHelp =
        "Facts and preferences it should keep. One line each, and the date and source are worth keeping. "
        + "Never paste web pages, email bodies or file contents in here: everything in this file is "
        + "treated as an instruction for every future conversation.";

    private static StackPanel Tab(string help, Control editor) => new()
    {
        Margin = new Thickness(12),
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = help, FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap },
            editor,
        },
    };

    private Control SkillsTab()
    {
        var newSkill = new Button { Content = "New skill" };
        newSkill.Click += (_, _) => StartNewSkill();
        var delete = new Button { Content = "Delete…" };
        delete.Click += async (_, _) => await DeleteSkillAsync();

        return new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Skills this Buzzy can load with the skill tool. Editing one changes what it sees next turn. "
                         + "A routine pinned to an earlier version of a workflow keeps using that version until you move it.",
                    FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _skillList, new StackPanel { Spacing = 6, Children = { newSkill, delete } } } },
                Label("Name"),
                _skillNameBox,
                Label("Description"),
                _skillDescriptionBox,
                Label("What to do"),
                _skillBodyBox,
            },
        };
    }

    /// <summary>Reads all three from disk, discarding unsaved edits.</summary>
    private async Task LoadAsync(bool saveFirst)
    {
        if (saveFirst && _dirty)
            Save();

        string soul, memory;
        try
        {
            soul = File.Exists(_store.SoulPathOf(_buzzyId)) ? await File.ReadAllTextAsync(_store.SoulPathOf(_buzzyId)) : "";
            memory = File.Exists(_store.MemoryPathOf(_buzzyId)) ? await File.ReadAllTextAsync(_store.MemoryPathOf(_buzzyId)) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn't read the files: {ex.Message}", Warning);
            return;
        }

        _loading = true;
        try
        {
            _soulBox.Text = soul;
            _memoryBox.Text = memory;
            _dirty = false;
            LoadSkillList();
        }
        finally
        {
            _loading = false;
        }

        _loaded = true;
        ShowStatus("Loaded.", Dim);
    }

    private void LoadSkillList()
    {
        _loadingSkills = true;
        try
        {
            var selected = _folder;
            _skillList.Items.Clear();
            foreach (var skill in _skills.List(_buzzyId))
                _skillList.Items.Add(new ListBoxItem { Content = skill.Folder, Tag = skill.Folder });

            var match = _skillList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == selected);
            if (match is not null)
                _skillList.SelectedItem = match;
            else if (_skillList.ItemCount > 0)
                _skillList.SelectedIndex = 0;
            else
                StartNewSkill();
        }
        finally
        {
            _loadingSkills = false;
        }

        if (_skillList.SelectedItem is null)
            LoadSelectedSkill();
    }

    private void LoadSelectedSkill()
    {
        if (_loadingSkills)
            return;
        if ((_skillList.SelectedItem as ListBoxItem)?.Tag is not string folder || _skills.Read(_buzzyId, folder) is not { } skill)
        {
            StartNewSkill();
            return;
        }

        _loading = true;
        try
        {
            _folder = skill.Folder;
            _skillNameBox.Text = skill.Name;
            _skillDescriptionBox.Text = skill.Description;
            _skillBodyBox.Text = skill.Body;
            _dirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private void StartNewSkill()
    {
        _loadingSkills = true;
        _skillList.SelectedItem = null;
        _loadingSkills = false;
        _loading = true;
        try
        {
            _folder = null;
            _skillNameBox.Text = "";
            _skillDescriptionBox.Text = "";
            _skillBodyBox.Text = "";
            _dirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save()
    {
        if (!_loaded)
        {
            ShowStatus("These files could not be read, so saving would overwrite them. Close this window and reopen it.", Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(_store.FolderOf(_buzzyId));
            File.WriteAllText(_store.SoulPathOf(_buzzyId), _soulBox.Text ?? "", Utf8NoBom);
            File.WriteAllText(_store.MemoryPathOf(_buzzyId), _memoryBox.Text ?? "", Utf8NoBom);

            // Only write a skill when there is something to write, so Save on an empty New skill form
            // doesn't leave a stub folder behind.
            if (!string.IsNullOrWhiteSpace(_skillNameBox.Text) && !string.IsNullOrWhiteSpace(_skillBodyBox.Text))
            {
                var folder = _folder ?? BuzzySkillLibrary.FolderFor(_skillNameBox.Text!);
                _skills.Save(_buzzyId, new BuzzySkillFile(folder, _skillNameBox.Text!, _skillDescriptionBox.Text ?? "", _skillBodyBox.Text!));
                _folder = folder;
                LoadSkillList();
            }

            _dirty = false;
            ShowStatus("Saved. The changes apply from the next turn.", Accent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowStatus($"Couldn't save: {ex.Message}", Warning);
        }
    }

    private void Revert()
    {
        _ = LoadAsync(saveFirst: false);
    }


    private async Task DeleteSkillAsync()
    {
        if (_folder is not { } folder)
        {
            ShowStatus("Select a skill to delete first.", Warning);
            return;
        }

        var confirmed = await ConfirmationDialog.ShowAsync(this, $"Delete the skill \"{folder}\"?",
            "This removes its SKILL.md (and any saved versions of it) from this Buzzy's skills folder. Its conversations and memory are untouched.");
        if (!confirmed)
            return;

        try
        {
            var directory = Path.Combine(_skills.RootOf(_buzzyId), folder);
            if (Directory.Exists(directory))
                BuzzyStore.DeleteTreeWithoutFollowingLinks(directory);
            StartNewSkill();
            LoadSkillList();
            ShowStatus($"Deleted \"{folder}\".", Accent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn't delete it: {ex.Message}", Warning);
        }
    }

    private void MarkDirty()
    {
        if (_loading || _dirty)
            return;
        _dirty = true;
        ShowStatus("Unsaved changes. Save to keep them, or Revert to discard.", Warning);
    }

    private void ShowStatus(string message, IBrush brush)
    {
        _status.Text = message;
        _status.Foreground = brush;
    }

    private static TextBox Editor() => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        FontSize = 12,
        MinHeight = 360,
    };

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 11, Foreground = Dim };
}