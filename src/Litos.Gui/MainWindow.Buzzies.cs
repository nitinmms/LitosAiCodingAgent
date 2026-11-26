using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Litos.Agent;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Gui.Buzzies;
using Litos.Gui.Buzzies.Routines;
using Litos.Tools.Shell;

namespace Litos.Gui;

/// <summary>
/// The Buzzies sidebar (Implement_Buzzies.md, Phase 0). The window still shows one conversation at
/// a time: selecting a Buzzy makes it the window's session owner, so its transcripts, last session,
/// tools, system prompt and working directory are its own, and selecting "Litos chat" puts the
/// plain agent back exactly where it was. Only one turn runs at a time, so switching waits for it.
/// </summary>
public sealed partial class MainWindow
{
    internal const string BuzzyPtcLabel = "PTC: off for Buzzies";
    private const string WindowTitle = "LitosAiAgent";

    private static readonly IBrush SelectedRowBrush = new SolidColorBrush(Color.Parse("#37373D"));
    private static readonly IBrush WorkingBrush = new SolidColorBrush(Color.Parse("#4EC9B0"));
    private static readonly IBrush HoneyBrush = new SolidColorBrush(Color.Parse("#FFC21A"));

    private sealed record BuzzyRow(Button Button, BuzzyAvatar Avatar, TextBlock Status);

    /// <summary>The Buzzy on screen, or null for the plain Litos chat.</summary>
    private BuzzyProfile? _buzzy;

    // Where the plain chat was when the user switched to a Buzzy, so coming back restores it.
    private string? _litosWorkingDirectory;
    private string? _litosSessionId;

    private readonly Dictionary<string, BuzzyRow> _buzzyRows = [];
    private Button? _litosRow;

    // Buzzies with an interactive turn running in this window (at most one, but keyed for clarity).
    private readonly HashSet<string> _chattingBuzzies = [];

    /// <summary>Whose sessions the window reads and writes: the selected Buzzy's, or the plain chat's.</summary>
    private SessionOwner ActiveOwner => _buzzy?.Owner ?? SessionOwner.Local;

    private BuzzyStore Buzzies => _session.BuzzyStore;

    private bool TurnInFlight => _turnCts is { IsCancellationRequested: false };

    private void RebuildBuzzySidebar()
    {
        BuzzySidebar.Children.Clear();
        _buzzyRows.Clear();

        var chatIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M4 5h16v11H9l-5 4z"),
            Stroke = DimTextBrush,
            StrokeThickness = 1.6,
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
        };
        _litosRow = SidebarRow(chatIcon, "Litos chat", new TextBlock { Text = "Plain coding agent", FontSize = 12, Foreground = DimTextBrush });
        _litosRow.Click += async (_, _) => await SelectBuzzyAsync(null);
        BuzzySidebar.Children.Add(_litosRow);

        BuzzySidebar.Children.Add(new TextBlock
        {
            Text = "MY BUZZIES",
            FontSize = 11,
            LetterSpacing = 1,
            Foreground = DimTextBrush,
            Margin = new Thickness(10, 14, 10, 6),
        });

        foreach (var profile in Buzzies.List())
        {
            var avatar = new BuzzyAvatar(profile.Color, 40);
            var status = new TextBlock { Text = "Idle", FontSize = 12, Foreground = DimTextBrush };
            var row = SidebarRow(avatar, profile.Name, status);
            row.Click += async (_, _) => await SelectBuzzyAsync(Buzzies.Get(profile.Id) ?? profile);
            row.ContextMenu = BuzzyMenu(profile);
            ToolTip.SetTip(row, $"{profile.Name} · {BuzzyTemplates.Find(profile.Template)?.Role ?? profile.Template}. Right-click for soul, memory and workspace.");
            _buzzyRows[profile.Id] = new BuzzyRow(row, avatar, status);
            BuzzySidebar.Children.Add(row);
        }

        var create = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 6, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "+", FontSize = 18, Foreground = HoneyBrush, Width = 40, TextAlignment = TextAlignment.Center },
                    new TextBlock { Text = "Create a Buzzy", VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        create.Click += async (_, _) => await CreateBuzzyAsync();
        BuzzySidebar.Children.Add(create);

        var routines = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 2, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new Avalonia.Controls.Shapes.Path
                    {
                        Data = Geometry.Parse("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18M12 7v5l3 2"),
                        Stroke = HoneyBrush, StrokeThickness = 1.6, Width = 20, Height = 20, Stretch = Stretch.Uniform,
                        Margin = new Thickness(10, 0, 10, 0),
                    },
                    new TextBlock { Text = "Routines", VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(routines, "Things your Buzzies repeat on a schedule");
        routines.Click += (_, _) => ShowRoutines(_buzzy?.Id);
        BuzzySidebar.Children.Add(routines);

        BuzzySidebar.Children.Add(new TextBlock
        {
            Text = "Each Buzzy has its own memory, sessions and workspace folder. Its wings beat while it works, in a chat or a routine.",
            FontSize = 11,
            Foreground = DimTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 14, 10, 0),
        });

        HighlightSidebarSelection();
        foreach (var id in _buzzyRows.Keys)
            RefreshBuzzyRowStatus(id);
        RefreshTrayVisibility();
    }

    private static Button SidebarRow(Control icon, string name, TextBlock subtitle)
    {
        var iconBox = new Border { Width = 40, Height = 36, Child = icon, VerticalAlignment = VerticalAlignment.Center };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 6),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    iconBox,
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { new TextBlock { Text = name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis }, subtitle },
                    },
                },
            },
        };
    }

    private ContextMenu BuzzyMenu(BuzzyProfile profile)
    {
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        return new ContextMenu
        {
            Items =
            {
                Item("Edit soul (SOUL.md)", () => TryLaunch(Buzzies.SoulPathOf(profile.Id))),
                Item("Edit memory (MEMORY.md)", () => TryLaunch(Buzzies.MemoryPathOf(profile.Id))),
                Item("Routines…", () => ShowRoutines(profile.Id)),
                Item("Review proposed memories…", () => _ = ReviewMemoriesAsync(Buzzies.Get(profile.Id) ?? profile)),
                Item("Open workspace folder", () =>
                {
                    Directory.CreateDirectory(Buzzies.WorkspaceOf(profile.Id));
                    TryLaunch(Buzzies.WorkspaceOf(profile.Id));
                }),
                new Separator(),
                Item("Delete…", () => _ = DeleteBuzzyAsync(profile)),
            },
        };
    }

    private void HighlightSidebarSelection()
    {
        if (_litosRow is not null)
            _litosRow.Background = _buzzy is null ? SelectedRowBrush : Brushes.Transparent;
        foreach (var (id, row) in _buzzyRows)
            row.Button.Background = id == _buzzy?.Id ? SelectedRowBrush : Brushes.Transparent;
    }

    /// <summary>An interactive turn with this Buzzy started or ended: its wings beat meanwhile, and
    /// no routine starts for it until the turn is over.</summary>
    private void SetBuzzyWorking(string id, bool working)
    {
        if (working)
        {
            _chattingBuzzies.Add(id);
            _routines?.BeginInteractive(id);
        }
        else
        {
            _chattingBuzzies.Remove(id);
            _routines?.EndInteractive(id);
        }
        RefreshBuzzyRowStatus(id);
    }

    /// <summary>The row's subtitle and wings: running a routine, chatting, the next scheduled run, or idle.</summary>
    private void RefreshBuzzyRowStatus(string id)
    {
        if (!_buzzyRows.TryGetValue(id, out var row))
            return;

        var routineRun = _routines?.RunningFor(id);
        var chatting = _chattingBuzzies.Contains(id);
        row.Avatar.IsWorking = routineRun is not null || chatting;
        if (routineRun is not null)
        {
            var name = _routines!.Database.GetRoutine(routineRun.RoutineId)?.Name ?? "a routine";
            row.Status.Text = $"Running {name}…";
            row.Status.Foreground = WorkingBrush;
        }
        else if (chatting)
        {
            row.Status.Text = "Working…";
            row.Status.Foreground = WorkingBrush;
        }
        else if (PendingMemoryCount(id) is > 0 and var pending)
        {
            // Routines proposed memories: say so until the user reviews them (right-click menu).
            row.Status.Text = pending == 1 ? "1 memory to review" : $"{pending} memories to review";
            row.Status.Foreground = HoneyBrush;
        }
        else
        {
            row.Status.Text = NextRunLabel(id) ?? "Idle";
            row.Status.Foreground = DimTextBrush;
        }
    }

    private string? NextRunLabel(string buzzyId)
    {
        if (_routines is null)
            return null;
        var now = DateTimeOffset.UtcNow;
        var next = _routines.Database.ListRoutines(buzzyId)
            .Where(r => r.Enabled)
            .Select(r => RoutineSchedule.Next(r, now, RoutineSchedule.ResolveTimeZone(r.TimeZoneId)))
            .OfType<DateTimeOffset>()
            .DefaultIfEmpty()
            .Min();
        return next == default ? null : $"Next: {RoutinesWindow.Friendly(next)}";
    }

    /// <summary>True (after saying so) when the Buzzy on screen is in the middle of a routine run:
    /// one task per Buzzy at a time, so the chat waits for it.</summary>
    private bool RejectIfBuzzyBusy()
    {
        if (_buzzy is null || _routines?.RunningFor(_buzzy.Id) is not { } run)
            return false;
        var name = _routines.Database.GetRoutine(run.RoutineId)?.Name ?? "a routine";
        AddToolLine($"{_buzzy.Name} is running its routine \"{name}\" right now. Wait for it to finish, or cancel it in Routines.");
        return true;
    }

    /// <summary>Switches the window to <paramref name="target"/> (null: the plain Litos chat), reopening
    /// its last session if it has one with messages, else starting a fresh one in its directory.</summary>
    private async Task SelectBuzzyAsync(BuzzyProfile? target)
    {
        if (target?.Id == _buzzy?.Id)
            return;
        if (TurnInFlight)
        {
            AddToolLine("A turn is still running. Wait for it to finish, or press Escape, before switching.");
            return;
        }

        if (_buzzy is null)
        {
            _litosWorkingDirectory = Environment.CurrentDirectory;
            _litosSessionId = _sessionId;
        }

        _buzzy = target;
        string directory;
        Transcript? transcript;
        if (target is null)
        {
            directory = _litosWorkingDirectory is { } dir && Directory.Exists(dir) ? dir : Environment.CurrentDirectory;
            transcript = await TryLoadSessionToRestoreAsync(_session.TranscriptStore, SessionOwner.Local, _litosSessionId, CancellationToken.None);
            if (transcript is not null)
                ShowSession(_litosSessionId!, transcript);
        }
        else
        {
            directory = Buzzies.WorkspaceOf(target.Id);
            Directory.CreateDirectory(directory);
            Environment.CurrentDirectory = directory;
            transcript = await TryLoadSessionToRestoreAsync(_session.TranscriptStore, target.Owner, target.LastSessionId, CancellationToken.None);
            if (transcript is not null)
                ShowSession(target.LastSessionId!, transcript);
        }

        if (transcript is null)
            StartFreshSession(directory);

        // A Buzzy always works in its own workspace, whatever directory an older transcript recorded.
        if (target is not null)
            ApplyWorkingDirectory(directory);

        Title = target is null ? WindowTitle : $"{WindowTitle} — {target.Name}";
        ChangeDirectoryButton.IsEnabled = target is null;
        RefreshKernelToggleButton();
        HighlightSidebarSelection();

        if (target is not null)
        {
            var role = BuzzyTemplates.Find(target.Template)?.Role ?? target.Template;
            AddToolLine(transcript is null
                ? $"{target.Name} ({role}) is ready. It works in {directory} and keeps its own memory."
                : $"Reopened {target.Name}'s last conversation ({transcript.Messages.Count} messages). Type /new for a fresh one.");
        }
    }

    private void StartFreshSession(string directory)
    {
        _sessionId = Guid.NewGuid().ToString("n");
        _transcript = Transcript.CreateNew(directory);
        RenderTranscriptHistory(_transcript);
        RefreshContextUsage();
        RememberCurrentSession();
        ApplyWorkingDirectory(directory);
    }

    private void ApplyWorkingDirectory(string directory)
    {
        Environment.CurrentDirectory = directory;
        WorkingDirectoryText.Text = directory;
        _mentionIndex = new FileMentionIndex(directory);
    }

    /// <summary>Saves the session on screen as the selected Buzzy's last one, in its buzzy.json.</summary>
    private void RememberBuzzySession()
    {
        if (_buzzy is null || _buzzy.LastSessionId == _sessionId)
            return;

        var updated = _buzzy with { LastSessionId = _sessionId };
        try
        {
            Buzzies.Save(updated);
            _buzzy = updated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next switch tries again; at worst the Buzzy opens a fresh session next time.
        }
    }

    private async Task CreateBuzzyAsync()
    {
        if (TurnInFlight)
        {
            AddToolLine("A turn is still running. Wait for it to finish before creating a Buzzy.");
            return;
        }

        var request = await CreateBuzzyWindow.ShowAsync(this, Buzzies);
        if (request is null)
            return;

        BuzzyProfile profile;
        try
        {
            profile = Buzzies.Create(request.Template, request.Name, request.Color, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddToolLine($"Couldn't create the Buzzy: {ex.Message}");
            return;
        }

        RebuildBuzzySidebar();
        await SelectBuzzyAsync(profile);
    }

    private async Task DeleteBuzzyAsync(BuzzyProfile profile)
    {
        if (TurnInFlight)
        {
            AddToolLine("A turn is still running. Wait for it to finish before deleting a Buzzy.");
            return;
        }

        var confirmed = await ConfirmationDialog.ShowAsync(this, $"Delete {profile.Name}?",
            $"This deletes {profile.Name}'s soul, memory and workspace folder ({Buzzies.FolderOf(profile.Id)}), including any files in it. " +
            "Its routines and their history are deleted too. Its past conversations stay in Litos's session history.");
        if (!confirmed)
            return;

        if (_buzzy?.Id == profile.Id)
            await SelectBuzzyAsync(null);

        try
        {
            if (_routines is not null)
            {
                foreach (var run in _routines.Database.ListRecentRuns(profile.Id, 50).Where(r => r.IsActive))
                    _routines.Cancel(run.Id);
                _routines.Database.DeleteForBuzzy(profile.Id);
            }
            Buzzies.Delete(profile.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddToolLine($"Couldn't delete {profile.Name}: {ex.Message}");
        }

        RebuildBuzzySidebar();
    }

    private ISystemPromptProvider BuzzyPromptProvider(BuzzyProfile profile) =>
        new BuzzySystemPromptProvider(_session.LoopFactory.SystemPromptProvider, profile, Buzzies);

    /// <summary>The registry and loop a Buzzy's turn runs with: its allowlisted, workspace-confined
    /// tools from the same per-turn registry the plain chat gets, and its own system prompt.</summary>
    private (ToolRegistry Registry, AgentLoop Loop) BuildBuzzyLoop(BuzzyProfile profile)
    {
        var registry = BuzzyToolPolicy.Build(_session.ToolRegistryFactory.Create(), profile, BuzzyToolContext());
        var loop = _session.LoopFactory.WithSystemPromptProvider(BuzzyPromptProvider(profile)).Create(_session.ChatProvider, registry);
        return (registry, loop);
    }

    private BuzzyToolContext BuzzyToolContext() => new(
        Buzzies,
        _session.PendingApprovals,
        // A shell of the Buzzy's own that starts in its workspace, auto-approved underneath because
        // ApprovalRequiredTool has already asked: nothing depends on the process's current directory.
        workspace => new ShellTool(new GuiApprovalGate(), _session.Config.ShellCommandTimeout, _session.Config.ShellMaxOutputBytes, workspace));
}
