using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Tools.Attachments;
using Litos.Tools.Mcp;
using Litos.Tools.Skills;
using MarkView.Avalonia;
using ContentBlock = Litos.Agent.Messages.ContentBlock;
using ImageBlock = Litos.Agent.Messages.ImageBlock;
using MessageTextBlock = Litos.Agent.Messages.TextBlock;

namespace Litos.Gui;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// One chip in the staging strip above the input box: a file attached via /attach but not
    /// yet sent. ImageBytes is non-null only for image attachments (drives thumbnail decode);
    /// null means a document (MarkItDown-converted) and renders a generic file glyph instead.
    /// Remove undoes this attachment's staging by reference, so removing one chip can't drift
    /// out of sync with _pendingAttachments/_pendingImages even as unrelated chips are added or
    /// removed.
    /// </summary>
    private sealed record StagedAttachment(string DisplayName, byte[]? ImageBytes, Action Remove);

    private static readonly IBrush UserBubbleBrush = new SolidColorBrush(Color.Parse("#2B5797"));
    private static readonly IBrush AssistantBubbleBrush = new SolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly IBrush ToolTextBrush = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush ToolErrorBrush = new SolidColorBrush(Color.Parse("#F48771"));
    private static readonly IBrush ErrorBubbleBrush = new SolidColorBrush(Color.Parse("#5A1F1F"));
    private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));

    private readonly MainWindowSession _session;
    private Transcript _transcript;
    private string _sessionId = Guid.NewGuid().ToString("n");
    private CancellationTokenSource? _turnCts;
    private bool _turnStarted;
    private ChannelWriter<SteeringMessage>? _currentSteeringWriter;

    private readonly List<DocumentMarkdown> _pendingAttachments = [];
    private readonly List<ImageBlock> _pendingImages = [];
    private readonly List<StagedAttachment> _stagedChips = [];

    private TextBlock? _currentAssistantText;
    private Border? _currentAssistantBubble;
    private readonly StringBuilder _currentAssistantMarkdown = new();
    private Border? _workingIndicator;
    private Border? _layoutNudgePhantom;
    private readonly Dictionary<string, Queue<ToolCallRow>> _toolRows = new();

    private const string SkillsTrigger = "/skills ";
    private bool _suppressInputBoxTextChanged;
    private bool _skillsPickerOpen;

    private readonly CommandMenuPopup _commandMenu = new();
    // Set only while _commandMenu is open due to a leading "/" the user typed (as opposed to the
    // "+" button), so accepting/dismissing it can tell whether InputBox's own "/"-prefixed text
    // needs clearing — the button-opened case has no such text to clean up.
    private bool _commandMenuOpenedByTyping;

    private readonly MentionPopup _mentionPopup = new();
    // Rebuilt whenever _transcript.WorkingDirectory changes (ChangeWorkingDirectoryAsync,
    // /resume, /branch, /new) — mirrors PickSkillAsync's "new SkillDiscovery(...)" comment: a
    // DI-registered singleton would be frozen to the process's launch-time CWD, but the GUI's
    // working directory is a mutable, session-scoped concept.
    private FileMentionIndex _mentionIndex = new(Directory.GetCurrentDirectory());
    // The index of the "@" that started the mention currently being typed, so accepting a
    // suggestion knows exactly what span of InputBox.Text to splice (see SpliceMention).
    private int _activeMentionStart = -1;

    public MainWindow() : this(null!, Directory.GetCurrentDirectory())
    {
        // Parameterless constructor required by the Avalonia XAML previewer/loader.
    }

    public MainWindow(MainWindowSession session, string workingDirectory)
    {
        _session = session;
        _transcript = Transcript.CreateNew(workingDirectory);
        _mentionIndex = new FileMentionIndex(workingDirectory);
        InitializeComponent();

        WorkingDirectoryText.Text = _transcript.WorkingDirectory;
        UpdateProviderModelText();
        RefreshContextUsage();

        SendButton.Click += async (_, _) =>
        {
            if (_turnCts is { IsCancellationRequested: false } cts)
                cts.Cancel();
            else
                await SubmitAsync();
        };
        InputBox.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (_commandMenu.HandleFilterKeyDown(e.Key) || _mentionPopup.HandleFilterKeyDown(e.Key))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Alt) != 0)
            {
                e.Handled = true;
                SendFollowUp();
            }
            else if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                // Let the TextBox handle it natively (AcceptsReturn="True" inserts a newline).
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitAsync();
            }
            else if (e.Key == Key.Escape && _turnCts is { IsCancellationRequested: false } cts)
                cts.Cancel();
        }, RoutingStrategies.Tunnel);
        InputBox.TextChanged += async (_, _) =>
        {
            UpdateCommandMenuFromTypedText();
            UpdateMentionPopupFromTypedText();
            await MaybeTriggerSkillsPickerAsync();
        };
        InputBox.AddHandler(TextBox.PastingFromClipboardEvent, async (_, e) => await OnPastingFromClipboardAsync(e));
        ChangeDirectoryButton.Click += async (_, _) => await ChangeWorkingDirectoryAsync();
        ProviderButton.Click += async (_, _) => await HandleProviderAsync(argument: null);
        ModelButton.Click += async (_, _) => await HandleModelAsync(argument: null);
        ContextUsagePanel.DoubleTapped += async (_, _) => await ShowContextBreakdownAsync();
        CommandMenuButton.Click += (_, _) => OpenCommandMenu(openedByTyping: false);
        _commandMenu.Accepted += entry => _ = OnCommandMenuAcceptedAsync(entry);
        _mentionPopup.Accepted += entry => OnMentionAccepted(entry);

        // Fire-and-forget, not awaited/blocked on: the window must close instantly regardless of
        // how long MCP child-process teardown takes (previously this blocked the UI thread for up
        // to 5s via Task.Run(...).Wait(), freezing the whole app on every close whenever a server
        // was connected — worse with more servers enabled, since McpToolProvider.ShutdownAsync
        // disposes them one at a time). A lingering child process for a few seconds after the
        // window is gone is harmless — mirrors how most desktop apps close.
        Closing += (_, _) => _ = _session.McpToolProvider.ShutdownAsync();
    }

    private void UpdateProviderModelText()
    {
        ProviderText.Text = _session.ProviderName;
        ModelText.Text = _session.Model;
    }

    private static readonly IBrush ContextUsageNormalBrush = DimTextBrush;
    private static readonly IBrush ContextUsageWarningBrush = new SolidColorBrush(Color.Parse("#D7BA7D"));
    private static readonly IBrush ContextUsageCriticalBrush = new SolidColorBrush(Color.Parse("#F48771"));

    // Called alongside UpdateProviderModelText() (provider/model switch, startup) and after
    // /new, /resume, and each completed turn — anywhere _transcript or _session.ContextLength
    // can change — so the meter never shows a stale percentage from the previous model/session.
    private void RefreshContextUsage()
    {
        var snapshot = ContextUsage.Compute(_transcript, _session.ContextLength);
        if (snapshot is null)
        {
            ContextUsageBar.Value = 0;
            ContextUsageText.Text = "—";
            ToolTip.SetTip(ContextUsagePanel, "Context used: no usage reported yet — double-click for details");
            return;
        }

        ContextUsageBar.Value = Math.Min(1.0, snapshot.Fraction);
        ContextUsageText.Text = $"{snapshot.Fraction * 100:0}%";
        var brush = snapshot.Level switch
        {
            ContextUsageLevel.Critical => ContextUsageCriticalBrush,
            ContextUsageLevel.Warning => ContextUsageWarningBrush,
            _ => ContextUsageNormalBrush,
        };
        ContextUsageText.Foreground = brush;
        ContextUsageBar.Foreground = brush;
        ToolTip.SetTip(ContextUsagePanel, $"Context used: {snapshot.UsedTokens:N0} / {snapshot.ContextLength:N0} tokens — double-click for details");
    }

    /// <summary>
    /// Rebuilds the system prompt on demand (same provider AgentLoop uses per-turn, see
    /// MainWindowSession.SystemPromptProvider) and computes a fresh ContextBreakdown snapshot
    /// from the live transcript, then shows it in a modal. A live, current-turn-only snapshot —
    /// left open during an in-flight turn, it won't update; the user can just double-click again.
    /// </summary>
    private async Task ShowContextBreakdownAsync()
    {
        var systemPrompt = await _session.SystemPromptProvider.BuildAsync(_session.ToolRegistry, _transcript.WorkingDirectory, CancellationToken.None);
        var snapshot = ContextBreakdown.Compute(_transcript, systemPrompt, _session.ToolRegistry.Schemas);
        await ViewContextWindow.ShowAsync(this, snapshot, _session.ContextLength);
    }

    private async Task ChangeWorkingDirectoryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select working directory",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(_transcript.WorkingDirectory!)),
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder?.TryGetLocalPath() is not { } path)
            return;

        _transcript = Transcript.CreateNew(path);
        Environment.CurrentDirectory = path;
        WorkingDirectoryText.Text = path;
        _mentionIndex = new FileMentionIndex(path);

        _session.Config = _session.Config with { LastWorkingDirectory = path };
        _session.Config.Save();
    }

    /// <summary>
    /// Pure decision behind what plain Enter (no Alt) should do with typed text, given whether a
    /// turn is currently running — mirrors Litos.Console's ComposerState.OnEnter (Steer branch)
    /// but scoped to the Gui's simpler always-there-is-an-InputBox model: an idle turn always
    /// submits, a running turn steers unless the text is a slash command (which must wait — see
    /// SubmitAsync's transcript-race comment) in which case the caller should reject it instead.
    /// </summary>
    internal static EnterAction DecideEnterAction(bool turnInFlight, string text) =>
        turnInFlight
            ? (text.StartsWith('/') ? EnterAction.RejectSlashCommandMidTurn : EnterAction.Steer)
            : EnterAction.Submit;

    internal enum EnterAction { Submit, Steer, RejectSlashCommandMidTurn }

    private async Task SubmitAsync()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _session is null)
            return;

        // A turn in flight is already reading _transcript.Messages (ContextAccountant.BuildRequest
        // holds the live list by reference while streaming). Slash commands that mutate the
        // transcript directly rather than going through AgentLoop — /compact's ApplyCompaction
        // above all — would race that read if dispatched here mid-turn, so every slash command
        // still waits for the turn to finish rather than just the ones known to mutate today.
        // Plain-text input, though, steers the in-flight turn (mirrors Litos.Console's Enter
        // semantics) instead of being rejected outright.
        if (_turnCts is { IsCancellationRequested: false })
        {
            switch (DecideEnterAction(turnInFlight: true, text))
            {
                case EnterAction.RejectSlashCommandMidTurn:
                    AddToolLine("A turn is already running — cancel it first (Escape) or wait for it to finish.");
                    break;
                case EnterAction.Steer:
                    SendSteeringMessage(text, SteeringMode.Steer);
                    break;
            }
            return;
        }

        InputBox.Text = "";

        var (expandedText, loadedSkillNames) = await ExpandSkillTokensAsync(text);
        text = expandedText;
        if (string.IsNullOrEmpty(text))
        {
            // Message was nothing but skill token(s), e.g. "/skill/foo" — mirrors /skill's own
            // behavior (queue-only, no turn started) rather than sending an empty turn.
            if (_pendingAttachments.Count > 0)
                AddToolLine("Skill(s) loaded — will be included in your next message.");
            return;
        }

        if (text.StartsWith('/'))
        {
            AddToolLine($"$ {text}");
            await TryHandleSlashCommandAsync(text);
            return;
        }

        await ResolveMentionsAsync(text);

        var attachedNames = _stagedChips.Select(c => c.DisplayName).ToList();
        AddUserBubble(text, attachedNames, loadedSkillNames);
        await RunTurnFromTextAsync(text);
    }

    /// <summary>
    /// Runs a turn from outgoing text that's already been decided and rendered as a user bubble
    /// — shared by SubmitAsync's plain-text path and HandleMcpPromptAsync (an MCP prompt's
    /// fetched, converted text stands in for what the user would otherwise have typed, per
    /// Claude Code's own prompt UX; the prompt path never bypasses the LLM the way a tool
    /// invocation would). Extracted verbatim from SubmitAsync's tail so both callers share
    /// identical turn-execution/cancellation/UI-state machinery.
    /// </summary>
    private async Task RunTurnFromTextAsync(string text)
    {
        ShowWorkingIndicator();

        _turnStarted = true;
        ChangeDirectoryButton.IsEnabled = false;
        SendButton.Content = "Cancel";
        StatusBarWorkingIndicator.IsVisible = true;

        _currentAssistantText = null;
        _currentAssistantBubble = null;
        _currentAssistantMarkdown.Clear();
        _turnCts = new CancellationTokenSource();
        var steering = Channel.CreateUnbounded<SteeringMessage>();
        _currentSteeringWriter = steering.Writer;
        var turnContent = BuildTurnContent(text);

        // Rebuilt on every send (not just on /provider, unlike the otherwise-identical two lines
        // in HandleProviderAsync) so a server added/enabled/disabled/removed via /mcp is reflected
        // starting with the very next turn, with no restart — a turn already in flight keeps
        // whatever AgentLoop/ToolRegistry it captured here, since only one turn ever runs at a time.
        _session.ToolRegistry = _session.ToolRegistryFactory.Create();
        _session.Loop = _session.LoopFactory.Create(_session.ChatProvider, _session.ToolRegistry);

        try
        {
            await foreach (var evt in _session.Loop.RunTurnAsync(
                SessionOwner.Local, _sessionId, _transcript, _session.Model, turnContent, _turnCts.Token, steering.Reader))
            {
                switch (evt)
                {
                    case TextDelta delta:
                        HideWorkingIndicator();
                        AppendAssistantText(delta.Text);
                        break;
                    case ToolCallCompleted tool:
                        HideWorkingIndicator();
                        FinalizeAssistantText();
                        AddToolCallRow(tool.CallId, tool.ToolName, tool.Arguments);
                        break;
                    case ToolCallResult result:
                        CompleteToolCallRow(result.CallId, result.Result);
                        break;
                    case ToolCallSkipped skipped:
                        // A skipped call was never invoked, so it never got a ToolCallCompleted/
                        // AddToolCallRow row in the first place — nothing to clean up here, even
                        // if an earlier call sharing this CallId (see AgentLoop's tolerance for
                        // duplicate CallIds) still has a row queued.
                        AddToolLine($"⏩ skipped {skipped.CallId}: {skipped.Reason}");
                        break;
                    case ErrorOccurred error:
                        HideWorkingIndicator();
                        FinalizeAssistantText();
                        AddBubble(error.Exception.Message, HorizontalAlignment.Left, ErrorBubbleBrush);
                        break;
                    case CompactionOccurred:
                        AddToolLine("⚏ context compacted");
                        break;
                    case MessageCompleted:
                        FinalizeAssistantText();
                        RefreshContextUsage();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            AddToolLine("[turn aborted]");
        }
        catch (Exception ex)
        {
            // Providers normally report failures via a yielded ErrorOccurred event (handled
            // above), but not every failure surfaces that way — e.g. OpenRouterChatProvider's
            // EnsureSuccessStatusCode() throws HttpRequestException straight out of the streaming
            // iterator on a non-2xx response (rate limits, auth errors, etc.). Left uncaught, that
            // exception unwinds past this async void-ish UI event handler and crashes the whole
            // process. Render it the same way as ErrorOccurred instead of letting it escape.
            FinalizeAssistantText();
            AddBubble(ex.Message, HorizontalAlignment.Left, ErrorBubbleBrush);
        }
        finally
        {
            HideWorkingIndicator();
            steering.Writer.TryComplete();
            _currentSteeringWriter = null;
            ChangeDirectoryButton.IsEnabled = true;
            SendButton.Content = "Send";
            StatusBarWorkingIndicator.IsVisible = false;
            _turnCts?.Dispose();
            _turnCts = null;
            _toolRows.Clear();
        }
    }

    // Mirrors Litos.Console's Composer.Steered/FollowedUp — writes into the in-flight turn's
    // steering channel (see AgentLoop.RunTurnAsync) instead of starting a new turn. Silently a
    // no-op if the channel was already completed (turn finished between the keystroke and here).
    private void SendSteeringMessage(string text, SteeringMode mode)
    {
        if (_currentSteeringWriter is not { } writer || !writer.TryWrite(new SteeringMessage(text, mode)))
            return;

        InputBox.Text = "";
        AddToolLine(mode == SteeringMode.Steer ? $"↪ steering: {text}" : $"↪ follow-up queued: {text}");
    }

    private void SendFollowUp()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _turnCts is not { IsCancellationRequested: false })
            return;

        SendSteeringMessage(text, SteeringMode.FollowUp);
    }

    // Matches /skill/<name> tokens inserted by the /skills picker (InsertAtCaret,
    // MaybeTriggerSkillsPickerAsync). Bounded by whitespace or string start/end on both sides so
    // it can't fire inside an unrelated longer token or URL-like text; the name itself stops at
    // the next whitespace.
    private static readonly Regex SkillTokenPattern = new(@"(?<=^|\s)/skill/(\S+)", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespacePattern = new(@"[ \t]{2,}", RegexOptions.Compiled);

    // Expands every /skill/<name> token in the submitted message before it's sent: loads each
    // skill's body into _pendingAttachments (same as the whole-line "/skill <name>" command) and
    // strips the token from the visible message text, so the model sees the skill content as a
    // proper attachment rather than a literal "/skill/foo" substring it would have to guess the
    // meaning of. Runs ahead of the "starts with /" slash-command check in SubmitAsync so a
    // message that is *only* a skill token (e.g. "/skill/foo") is expanded to its content instead
    // of being misrouted to TryHandleSlashCommandAsync as an unknown command. Returns the
    // successfully-loaded skill names alongside the cleaned text so the caller can surface them
    // in the user bubble the same way /attach file names already are (attachedNames in
    // AddUserBubble) — otherwise a skill invoked this way leaves no visible trace at all.
    private async Task<(string Text, List<string> LoadedSkillNames)> ExpandSkillTokensAsync(string text)
    {
        var matches = SkillTokenPattern.Matches(text);
        if (matches.Count == 0)
            return (text, []);

        var loaded = new List<string>();
        foreach (Match match in matches)
        {
            var name = match.Groups[1].Value;
            var error = await LoadSkillAsync(name, args: null);
            if (error is null)
                loaded.Add(name);
            else
                AddToolLine($"{error} Run /skills to see what's available.");
        }

        var cleaned = CollapseWhitespacePattern.Replace(SkillTokenPattern.Replace(text, ""), " ").Trim();
        return (cleaned, loaded);
    }

    // Resolves every "@path" mention in the final submitted text (the popup only ever inserts
    // text into InputBox — see OnMentionAccepted — nothing is attached until send time) into the
    // same _pendingAttachments/_stagedChips pipeline /attach already uses, mirroring
    // Litos.Console.Program's ResolveMentionsAsync. Unlike /skill tokens, mention text is left in
    // the visible message (a mention reads naturally as part of the sentence, e.g. "explain
    // @src/Foo.cs"), so only the attachment side-effect is added here — the text itself is
    // untouched.
    private async Task ResolveMentionsAsync(string text)
    {
        foreach (var rawMention in MentionParser.ExtractPaths(text))
        {
            // The raw capture can include trailing sentence words ("@Plant Resource.png please
            // describe it") since spaces are allowed in filenames — try the longest candidate
            // first and shrink until something on disk actually matches.
            var mentionPath = MentionParser.ExpandCandidates(rawMention)
                .FirstOrDefault(candidate => File.Exists(candidate) || Directory.Exists(candidate));

            if (mentionPath is null)
            {
                AddToolLine($"@{rawMention}: no such file or directory, ignoring mention.");
                continue;
            }

            if (Directory.Exists(mentionPath))
            {
                var entries = Directory.EnumerateFileSystemEntries(mentionPath)
                    .Select(entry => Directory.Exists(entry) ? $"{Path.GetFileName(entry)}/" : Path.GetFileName(entry))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
                var doc = new DocumentMarkdown(mentionPath, string.Join('\n', entries), []);
                _pendingAttachments.Add(doc);
                _stagedChips.Add(new StagedAttachment(mentionPath, ImageBytes: null, () => _pendingAttachments.Remove(doc)));
                continue;
            }

            try
            {
                var result = await _session.AttachHandler.AttachPathAsync(mentionPath, CancellationToken.None);
                ApplyAttachResult(result, Path.GetFileName(mentionPath));
            }
            catch (Exception ex)
            {
                AddToolLine($"Could not read @{mentionPath}: {ex.Message}");
            }
        }
    }

    private IReadOnlyList<ContentBlock> BuildTurnContent(string input)
    {
        var turnText = _pendingAttachments.Count == 0
            ? input
            : string.Join("\n\n", _pendingAttachments
                .Select(a => $"### Attachment: {a.Title}\n\n{a.Markdown}")
                .Append(input));
        _pendingAttachments.Clear();

        var turnContent = new List<ContentBlock>(_pendingImages.Count + 1) { new MessageTextBlock(turnText) };
        turnContent.AddRange(_pendingImages);
        _pendingImages.Clear();
        _stagedChips.Clear();
        RefreshStagingStrip();
        return turnContent;
    }

    private void ApplyAttachResult(AttachResult result, string displayName)
    {
        if (result.Document is { } doc)
        {
            _pendingAttachments.Add(doc);
            _stagedChips.Add(new StagedAttachment(displayName, ImageBytes: null, () => _pendingAttachments.Remove(doc)));
        }
        if (result.Image is { } img)
        {
            _pendingImages.Add(img);
            _stagedChips.Add(new StagedAttachment(displayName, img.Data, () => _pendingImages.Remove(img)));
        }

        // Only warnings surface in the transcript; the routine "Attached '...'" confirmation
        // lines are dropped now that the staging strip shows the same information visually.
        foreach (var line in result.Lines)
        {
            if (line.StartsWith("Warning:", StringComparison.Ordinal))
                AddToolLine(line);
        }

        RefreshStagingStrip();
    }

    private void RefreshStagingStrip() =>
        Dispatcher.UIThread.Post(() =>
        {
            StagingPanel.Children.Clear();
            foreach (var chip in _stagedChips)
                StagingPanel.Children.Add(BuildChip(chip));
            StagingStrip.IsVisible = _stagedChips.Count > 0;
        });

    private Border BuildChip(StagedAttachment chip)
    {
        Control icon = chip.ImageBytes is { } bytes
            ? new Image
            {
                Source = new Bitmap(new MemoryStream(bytes)),
                Width = 32,
                Height = 32,
                Stretch = Stretch.UniformToFill,
            }
            : new TextBlock
            {
                Text = "📄",
                FontSize = 20,
                Width = 32,
                Height = 32,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        var label = new TextBlock
        {
            Text = chip.DisplayName,
            MaxWidth = 100,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Foreground = Brushes.WhiteSmoke,
        };

        var removeButton = new Button { Content = "✕", Padding = new Thickness(4, 0), Margin = new Thickness(4, 0, 0, 0) };
        removeButton.Click += (_, _) =>
        {
            chip.Remove();
            _stagedChips.Remove(chip);
            RefreshStagingStrip();
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#2D2D30")),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { icon, label, removeButton },
            },
        };
    }

    private async Task TryHandleSlashCommandAsync(string commandLine)
    {
        var parts = commandLine.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0];
        var argument = parts.Length > 1 ? parts[1] : null;

        switch (command)
        {
            case "/new":
                _sessionId = Guid.NewGuid().ToString("n");
                // Environment.CurrentDirectory, not _transcript.WorkingDirectory: the old
                // transcript's WorkingDirectory can be null (an older /resume-d session
                // predating this field — see HandleResumeAsync), in which case falling back to
                // it would have silently jumped the new session to Directory.GetCurrentDirectory()
                // at that moment, which is not necessarily where the user was actually working.
                // Environment.CurrentDirectory is kept in sync with wherever the user is actually
                // working by every path that changes it (ChangeWorkingDirectoryAsync,
                // HandleResumeAsync, Program.cs at startup), so /new staying there without
                // touching it is precisely "stay in the current directory unless the user
                // explicitly changes it."
                _transcript = Transcript.CreateNew(Environment.CurrentDirectory);
                _mentionIndex = new FileMentionIndex(Environment.CurrentDirectory);
                // A fresh Transcript has zero messages, so this clears TranscriptPanel/_toolRows
                // (the same reset /resume and /branch rely on) and simply renders nothing — the
                // old conversation's bubbles/tool rows no longer linger on screen under the new
                // session's first turn.
                RenderTranscriptHistory(_transcript);
                RefreshContextUsage();
                AddToolLine($"Started new session {_sessionId}.");
                return;

            case "/resume":
                await HandleResumeAsync(argument);
                return;

            case "/attach":
                await HandleAttachAsync(argument);
                return;

            case "/provider":
                await HandleProviderAsync(argument);
                return;

            case "/model":
                await HandleModelAsync(argument);
                return;

            case "/skills":
                await HandleSkillsAsync();
                return;

            case "/skill":
                await HandleSkillAsync(argument);
                return;

            case "/branch":
                await HandleBranchAsync();
                return;

            case "/compact":
                await HandleCompactAsync();
                return;

            case "/reflect":
                await HandleReflectAsync();
                return;

            case "/mcp":
                await McpServersWindow.ShowAsync(this, _session.McpConfigStore, _session.McpToolProvider);
                return;

            case "/keys":
                await HandleKeysAsync();
                return;

            default:
                var promptEntry = _session.McpToolProvider.Prompts.FirstOrDefault(p => $"/{p.FullName}" == command);
                if (promptEntry is null)
                {
                    AddToolLine($"Unknown command: {command}");
                    return;
                }
                await HandleMcpPromptAsync(promptEntry, argument);
                return;
        }
    }

    /// <summary>
    /// Fetches and runs an MCP prompt command (/mcp__server__prompt [args]) — the GUI-only
    /// equivalent of a normal typed message, per Claude Code's own prompt UX: the server-
    /// returned prompt text stands in for what the user would otherwise have typed, then runs
    /// through the exact same RunTurnFromTextAsync path SubmitAsync uses for plain text (never a
    /// bypassed/direct invocation the way a tool call would be). Called from
    /// TryHandleSlashCommandAsync's default case once a command line resolves to a known
    /// mcp__server__prompt name.
    /// </summary>
    private async Task HandleMcpPromptAsync(McpPromptEntry entry, string? argument)
    {
        if (entry.Connection.Status != McpConnectionStatus.Connected)
        {
            AddToolLine($"MCP server '{entry.ServerName}' is not connected — cannot run /{entry.FullName}.");
            return;
        }

        var tokens = McpPromptArguments.Tokenize(argument ?? "");
        var (arguments, bindError) = McpPromptArguments.Bind(tokens, entry.Prompt.ProtocolPrompt.Arguments);
        if (bindError is not null)
        {
            AddToolLine(bindError.Kind == McpPromptArguments.BindErrorKind.MissingRequired
                ? $"Missing required argument(s) for /{entry.FullName}: {string.Join(", ", bindError.Names)}."
                : $"Too many arguments for /{entry.FullName} (expected at most {entry.Prompt.ProtocolPrompt.Arguments?.Count ?? 0}).");
            return;
        }

        ModelContextProtocol.Protocol.GetPromptResult result;
        try
        {
            result = await entry.Connection.GetPromptAsync(entry.Prompt, arguments, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddToolLine($"Failed to fetch prompt '{entry.FullName}': {ex.Message}");
            return;
        }

        var converted = McpPromptContentConverter.Convert(result);
        if (converted.SkippedBlockIndexes.Count > 0)
            AddToolLine($"Note: {converted.SkippedBlockIndexes.Count} non-text block(s) in this prompt's response were skipped.");
        if (string.IsNullOrWhiteSpace(converted.Text))
        {
            AddToolLine($"Prompt '{entry.FullName}' returned no usable text content.");
            return;
        }

        await ResolveMentionsAsync(converted.Text);
        AddUserBubble(converted.Text, attachedNames: [], skillNames: []);
        await RunTurnFromTextAsync(converted.Text);
    }

    /// <summary>
    /// Lets the user rewind the current session to a point right before an earlier message
    /// they sent, and continue from there — a new session, so the original is left untouched
    /// (matches ITranscriptStore.BranchAsync's copy-on-branch semantics, same as Console's
    /// /branch). Unlike Console's /branch, the "point" here is picked from a list of the user's
    /// own past messages rather than typed as a raw transcript-entry index: entries are read
    /// fresh from the store (not from _transcript.Messages) so the picked entry's position lines
    /// up exactly with the index BranchAsync expects, and filtered to genuine user turns using
    /// the same "Role.User with no ToolResultBlock" test JsonlTranscriptStore.ListSessionsAsync
    /// already uses for FirstUserMessagePreview — a tool result is technically Role.User too
    /// (ChatMessage.ToolResult) but was never something the user typed, so it isn't a sensible
    /// branch point to offer.
    /// </summary>
    private async Task HandleBranchAsync()
    {
        var entries = new List<TranscriptEntry>();
        await foreach (var entry in _session.TranscriptStore.ReadAsync(SessionOwner.Local, _sessionId, CancellationToken.None))
            entries.Add(entry);

        var userTurns = FindBranchPoints(entries);
        if (userTurns.Count == 0)
        {
            AddToolLine("Nothing to branch from yet — send a message first.");
            return;
        }

        var picked = await ListPickerWindow.PickAsync(this, "Branch from message", userTurns, BranchPointLabel);
        if (picked is null)
            return;

        // +1: BranchAsync's uptoEntryIndex is exclusive ("keep the first N entries"), but picking
        // a message means "branch right after what I said here" — the picked user turn itself
        // must stay in the new session, not just everything strictly before it.
        var newSessionId = await _session.TranscriptStore.BranchAsync(SessionOwner.Local, _sessionId, picked.EntryIndex + 1, CancellationToken.None);
        _sessionId = newSessionId;
        _transcript = await Transcript.LoadAsync(_session.TranscriptStore, SessionOwner.Local, _sessionId, CancellationToken.None);
        RenderTranscriptHistory(_transcript);
        RefreshContextUsage();
        AddToolLine($"Branched into new session {_sessionId} ({_transcript.Messages.Count} messages kept).");
    }

    internal sealed record BranchPoint(int EntryIndex, DateTimeOffset Timestamp, string Text);

    /// <summary>
    /// Pure candidate-list logic behind /branch, split out from HandleBranchAsync so it's
    /// testable without an Avalonia window (same reasoning as Program.ResolveStartupWorkingDirectory
    /// in Litos.Gui.Tests). Filters entries down to genuine user turns using the same
    /// "Role.User with no ToolResultBlock" test JsonlTranscriptStore.ListSessionsAsync already
    /// uses for FirstUserMessagePreview — a tool result is technically Role.User too
    /// (ChatMessage.ToolResult) but was never something the user typed, so it isn't a sensible
    /// branch point to offer. EntryIndex is the entry's position in the passed-in list, which
    /// must line up with the index ITranscriptStore.BranchAsync expects (i.e. entries should come
    /// from ReadAsync, not a filtered/compacted Transcript.Messages).
    /// </summary>
    internal static List<BranchPoint> FindBranchPoints(IReadOnlyList<TranscriptEntry> entries) =>
        entries
            .Select((entry, index) => (entry, index))
            .Where(t => t.entry.Message is { Role: Litos.Agent.Messages.Role.User } msg
                && msg.Content.OfType<Litos.Agent.Messages.ToolResultBlock>().Any() is false)
            .Select(t => new BranchPoint(
                t.index,
                t.entry.Timestamp,
                t.entry.Message!.Content.OfType<MessageTextBlock>().FirstOrDefault()?.Text ?? "(no text)"))
            .ToList();

    private static string BranchPointLabel(BranchPoint point)
    {
        var preview = point.Text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        if (preview.Length > SessionPreviewMaxLength)
            preview = preview[..SessionPreviewMaxLength] + "…";
        return $"{point.Timestamp.ToLocalTime():g}\n{preview}";
    }

    /// <summary>
    /// Manually triggers the same compaction AgentLoop otherwise only runs automatically once the
    /// context window is nearly full (see Compactor/CompactionPlanner) — ForceCompactAsync skips
    /// that threshold check since the user asked for this regardless of current usage, but still
    /// safely no-ops if there isn't yet enough history old enough to cut (e.g. a fresh session).
    /// </summary>
    private async Task HandleCompactAsync()
    {
        StatusBarWorkingIndicator.IsVisible = true;
        try
        {
            var compacted = await _session.Compactor.ForceCompactAsync(_transcript, _session.ChatProvider, _session.Model, CancellationToken.None);
            if (!compacted)
            {
                AddToolLine("Nothing to compact yet.");
                return;
            }

            // Mirrors AgentLoop.RunTurnAsync's own persistence of an automatic compaction: only the
            // synthetic summary message is appended (JSONL is append-only — the original messages
            // stay on disk, replaying on the next /resume the same way auto-compaction's already do).
            await _session.TranscriptStore.AppendAsync(SessionOwner.Local, _sessionId, TranscriptEntry.FromMessage(_transcript.Messages[0]), CancellationToken.None);

            RenderTranscriptHistory(_transcript);
            RefreshContextUsage();
            AddToolLine("⚏ context compacted");
        }
        finally
        {
            StatusBarWorkingIndicator.IsVisible = false;
        }
    }

    /// <summary>
    /// Distills the current session's full transcript into AGENTS.md at the project root — the
    /// write-side counterpart to ProjectInstructionsDiscovery's read side, which already
    /// discovers and injects AGENTS.md into the system prompt on every turn. Always targets
    /// {WorkingDirectory}/AGENTS.md directly (no ancestor walk, no multi-file picker), matching
    /// /new's existing WorkingDirectory-with-CurrentDirectory-fallback idiom. Shows an editable-
    /// text-plus-diff confirmation modal before writing anything — never silently overwrites the
    /// user's file. Unlike /compact, this never mutates _transcript; its only side effect is the
    /// AGENTS.md file write, gated entirely behind the modal's Confirm button.
    /// </summary>
    private async Task HandleReflectAsync()
    {
        StatusBarWorkingIndicator.IsVisible = true;
        try
        {
            var workingDirectory = _transcript.WorkingDirectory ?? Environment.CurrentDirectory;
            var agentsMdPath = Path.Combine(workingDirectory, "AGENTS.md");
            var existing = File.Exists(agentsMdPath) ? await File.ReadAllTextAsync(agentsMdPath) : null;

            var proposed = await _session.Reflector.ReflectAsync(
                _transcript.Messages, existing, _session.ChatProvider, _session.Model, CancellationToken.None);

            var edited = await ReflectWindow.ShowAsync(this, existing ?? "", proposed, agentsMdPath);
            if (edited is null)
            {
                AddToolLine("Reflect cancelled — AGENTS.md not changed.");
                return;
            }

            await File.WriteAllTextAsync(agentsMdPath, edited);
            AddToolLine($"⚏ AGENTS.md updated ({agentsMdPath}).");
        }
        finally
        {
            StatusBarWorkingIndicator.IsVisible = false;
        }
    }

    private async Task HandleKeysAsync()
    {
        var saved = await ApiKeysWindow.ShowAsync(this);
        if (saved)
            AddToolLine("API keys updated. Restart Litos for the change to take effect.");
    }

    private async Task HandleResumeAsync(string? argument)
    {
        var sessions = await _session.TranscriptStore.ListSessionsAsync(SessionOwner.Local, CancellationToken.None);

        var pickedId = argument;
        if (pickedId is null)
        {
            var picked = await ListPickerWindow.PickAsync(
                this,
                "Resume session",
                sessions,
                SessionCardLabel);
            pickedId = picked?.SessionId;
        }

        if (pickedId is null)
        {
            AddToolLine("Usage: /resume <sessionId>");
            return;
        }

        _sessionId = pickedId;
        _transcript = await Transcript.LoadAsync(_session.TranscriptStore, SessionOwner.Local, _sessionId, CancellationToken.None);
        RenderTranscriptHistory(_transcript);
        RefreshContextUsage();
        AddToolLine($"Resumed session {_sessionId} ({_transcript.Messages.Count} messages).");

        // A resumed session's own recorded WorkingDirectory only takes effect if it's usable
        // (non-null and still exists on this machine); otherwise the app stays exactly where it
        // already was (Environment.CurrentDirectory — the live, always-current source of truth
        // that /new also reads, as opposed to _transcript.WorkingDirectory which can be null for
        // an older session predating this field). WorkingDirectoryText/_mentionIndex are always
        // refreshed from that same resolved directory, even on the "stay put" path — before this
        // fix, an early return here left both silently pointed at whatever directory was active
        // before the resume, and a subsequent /new (which reads _transcript.WorkingDirectory,
        // still null on this session) fell back to Directory.GetCurrentDirectory() — the
        // process's own launch directory — rather than staying in the directory the user was
        // actually working in.
        var resolution = ResolveResumeWorkingDirectory(_transcript.WorkingDirectory, Directory.Exists, () => Environment.CurrentDirectory);
        if (resolution.Warning is { } warning)
            AddToolLine(warning);

        Environment.CurrentDirectory = resolution.Directory;
        WorkingDirectoryText.Text = resolution.Directory;
        _mentionIndex = new FileMentionIndex(resolution.Directory);
    }

    internal readonly record struct ResumeWorkingDirectoryResolution(string Directory, string? Warning);

    /// <summary>
    /// Pure decision logic behind HandleResumeAsync's working-directory handling, split out so
    /// it's testable without an Avalonia window or a real filesystem (same shape as
    /// Program.ResolveStartupWorkingDirectory). A resumed session's recorded WorkingDirectory
    /// wins only if it's non-null and still exists; otherwise the caller must stay in whatever
    /// directory the app is already in (getCurrentDirectory) rather than falling back to
    /// anything else, so a null WorkingDirectory (an older session predating this field) can
    /// never cause a silent jump to a directory the user never chose.
    /// </summary>
    internal static ResumeWorkingDirectoryResolution ResolveResumeWorkingDirectory(
        string? recordedWorkingDirectory, Func<string, bool> directoryExists, Func<string> getCurrentDirectory)
    {
        if (recordedWorkingDirectory is { } dir && directoryExists(dir))
            return new ResumeWorkingDirectoryResolution(dir, Warning: null);

        var current = getCurrentDirectory();
        var warning = recordedWorkingDirectory is { } missing
            ? $"Warning: working directory '{missing}' no longer exists — staying in '{current}'."
            : null;
        return new ResumeWorkingDirectoryResolution(current, warning);
    }

    private const int SessionPreviewMaxLength = 100;

    // Three-line card: date/time, first user message (what the session was actually about),
    // then the session id last since it's the least useful thing for recognizing a session at a
    // glance but still needed to disambiguate/copy. Collapse embedded newlines in the preview so
    // one message can't stretch the card past three lines.
    private static string SessionCardLabel(SessionSummary s)
    {
        var preview = s.FirstUserMessagePreview?.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        if (preview is { Length: > SessionPreviewMaxLength })
            preview = preview[..SessionPreviewMaxLength] + "…";

        return $"{s.LastUpdatedAt.ToLocalTime():g}\n{preview ?? "(no user message)"}\n{s.SessionId}";
    }

    // Intercepts Ctrl+V (and context-menu/Edit-menu paste) on InputBox: if the clipboard holds an
    // image — e.g. a screenshot copied via Snipping Tool/Cmd+Shift+4/etc. — stage it the same way
    // /attach stages an image file, instead of letting TextBox insert clipboard text. Marks the
    // event handled only once an image is actually found, so a text-only clipboard falls through
    // to TextBox's normal paste behavior untouched.
    private async Task OnPastingFromClipboardAsync(RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        Bitmap? bitmap;
        try
        {
            bitmap = await clipboard.TryGetBitmapAsync();
        }
        catch (Exception ex)
        {
            AddToolLine($"Could not read image from clipboard: {ex.Message}");
            return;
        }

        if (bitmap is null)
            return;

        e.Handled = true;
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        var image = new ImageBlock("image/png", ms.ToArray());
        ApplyAttachResult(new AttachResult([], Document: null, Image: image), "Pasted image");
    }

    private async Task HandleAttachAsync(string? argument)
    {
        var isUrl = Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        if (isUrl)
        {
            try
            {
                var result = await _session.AttachHandler.AttachArgumentAsync(argument!, CancellationToken.None);
                ApplyAttachResult(result, result.Document?.Title ?? argument!);
            }
            catch (Exception ex)
            {
                AddToolLine($"Could not convert attachment: {ex.Message}");
            }
            return;
        }

        var startFolder = argument is not null && Directory.Exists(argument)
            ? argument
            : _transcript.WorkingDirectory ?? Directory.GetCurrentDirectory();

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach file(s)",
            AllowMultiple = true,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(startFolder)),
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path)
                continue;

            try
            {
                var result = await _session.AttachHandler.AttachPathAsync(path, CancellationToken.None);
                ApplyAttachResult(result, Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                AddToolLine($"Could not convert attachment '{path}': {ex.Message}");
            }
        }
    }

    private async Task HandleProviderAsync(string? argument)
    {
        var pickedProvider = argument;
        if (pickedProvider is null)
        {
            pickedProvider = await ListPickerWindow.PickAsync(
                this,
                "Switch provider",
                _session.AvailableProviders,
                p => p,
                _session.ProviderName);
        }

        if (pickedProvider is null || !_session.AvailableProviders.Contains(pickedProvider))
        {
            AddToolLine($"Usage: /provider <{string.Join('|', _session.AvailableProviders)}>");
            return;
        }

        // Held back in locals until ListModelsAsync succeeds — assigning straight into _session
        // first would leave ProviderName/ChatProvider/Loop pointing at the new provider while the
        // status bar still shows the old one if the call below throws (e.g. a rate-limited
        // provider), a state desync on top of the crash.
        var newChatProvider = _session.ProviderFactory.Resolve(pickedProvider);
        IReadOnlyList<ModelInfo> newProviderModels;
        try
        {
            newProviderModels = await newChatProvider.ListModelsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddToolLine($"Could not switch to provider '{pickedProvider}': {ex.Message}");
            return;
        }

        _session.ProviderName = pickedProvider;
        _session.ChatProvider = newChatProvider;
        _session.Loop = _session.LoopFactory.Create(newChatProvider, _session.ToolRegistry);
        var newModel = newProviderModels.FirstOrDefault(m => m.IsDefault) ?? newProviderModels.FirstOrDefault();
        _session.Model = newModel?.Id ?? _session.Model;
        _session.ContextLength = newModel?.ContextLength ?? ModelContextWindows.Resolve(_session.Model);

        UpdateProviderModelText();
        RefreshContextUsage();
        SaveLastUsedProviderAndModel();
        AddToolLine($"Switched to provider '{_session.ProviderName}', model '{_session.Model}'.");
    }

    private async Task HandleModelAsync(string? argument)
    {
        if (argument is not null)
        {
            // No ListModelsAsync round-trip for this path (matches the original fast/direct
            // behavior below) — ContextLength is resolved from the static table only, which
            // may be less accurate than the OpenRouter-catalog-backed value ListModelsAsync
            // would return, but avoids a network call just to switch models by id.
            _session.Model = argument;
            _session.ContextLength = ModelContextWindows.Resolve(argument);
            UpdateProviderModelText();
            RefreshContextUsage();
            SaveLastUsedProviderAndModel();
            AddToolLine($"Switched to model '{_session.Model}'.");
            return;
        }

        IReadOnlyList<ModelInfo> models;
        try
        {
            models = await _session.ChatProvider.ListModelsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddToolLine($"Could not list models: {ex.Message}");
            return;
        }

        if (models.Count == 0)
        {
            AddToolLine("No models available to pick from.");
            return;
        }

        var picked = await ListPickerWindow.PickAsync(
            this,
            "Switch model",
            models,
            m => m.IsDefault ? $"{m.DisplayName} (default)" : m.DisplayName);

        if (picked is null)
            return;

        _session.Model = picked.Id;
        _session.ContextLength = picked.ContextLength ?? ModelContextWindows.Resolve(picked.Id);
        UpdateProviderModelText();
        RefreshContextUsage();
        SaveLastUsedProviderAndModel();
        AddToolLine($"Switched to model '{_session.Model}'.");
    }

    private void SaveLastUsedProviderAndModel()
    {
        _session.Config = _session.Config with { DefaultProvider = _session.ProviderName, DefaultModel = _session.Model };
        _session.Config.Save();
    }

    // Repurposed from a static text listing into the same picker used by the inline "/skills "
    // trigger (MaybeTriggerSkillsPickerAsync): a user who types the whole line "/skills" and
    // presses Enter gets the identical picker, just inserting into an already-cleared InputBox
    // instead of splicing at a mid-message caret position.
    private async Task HandleSkillsAsync()
    {
        var picked = await PickSkillAsync();
        if (picked is null)
            return;

        InsertAtCaret($"/skill/{picked.Name} ");
    }

    // The DI-registered ISkillDiscovery is a singleton frozen to the process's launch-time CWD
    // (Litos.Host.LitosHostBuilder), which never changes for Console but does for the GUI
    // (working directory is a mutable, session-scoped concept — see "Change…" and /resume).
    // Discover fresh against the live session directory instead of the stale singleton so
    // project-level .litos/skills/ is found under the directory actually shown in the status bar.
    private async Task<SkillMetadata?> PickSkillAsync()
    {
        var discovery = new SkillDiscovery(_transcript.WorkingDirectory);
        var skills = await discovery.DiscoverAsync(CancellationToken.None);
        if (skills.Count == 0)
        {
            AddToolLine("No skills found under .litos/skills/, ~/.litos/skills/, or ~/.claude/skills/.");
            return null;
        }

        return await ListPickerWindow.PickAsync(
            this,
            "Insert skill",
            skills,
            s => $"{s.Name} — {s.Description}");
    }

    // Inserts text at InputBox's current caret position and leaves the caret just past it,
    // rather than appending to the end — lets /skills-triggered picks land exactly where the
    // user invoked them mid-message instead of always jumping to the end of the line.
    private void InsertAtCaret(string text)
    {
        var caret = InputBox.CaretIndex;
        var current = InputBox.Text ?? "";

        _suppressInputBoxTextChanged = true;
        InputBox.Text = current[..caret] + text + current[caret..];
        _suppressInputBoxTextChanged = false;

        InputBox.CaretIndex = caret + text.Length;
        InputBox.Focus();
    }

    // Detects "/skills " typed anywhere in the message (not just at the start — the user may be
    // composing free-form text and want to drop a skill reference mid-sentence) and swaps it for
    // an interactive pick, so /skills behaves as a live trigger rather than a submit-only
    // command. Re-entrancy guards: _suppressInputBoxTextChanged ignores TextChanged events raised
    // by our own edits (InsertAtCaret, the removal below), and _skillsPickerOpen prevents a
    // second trigger firing while the modal picker from the first one is still open (Avalonia
    // still delivers TextChanged for edits made while a modal owned by the same window is up).
    private async Task MaybeTriggerSkillsPickerAsync()
    {
        if (_suppressInputBoxTextChanged || _skillsPickerOpen)
            return;

        var text = InputBox.Text ?? "";
        var caret = InputBox.CaretIndex;

        var triggerStart = text[..Math.Min(caret, text.Length)].LastIndexOf(SkillsTrigger, StringComparison.Ordinal);
        if (triggerStart < 0)
            return;

        var triggerEnd = triggerStart + SkillsTrigger.Length;
        if (triggerEnd > caret)
            return; // caret hasn't moved past the trigger's trailing space yet

        // Word boundary: "/skills " must start the message or be preceded by whitespace, so it
        // can't fire inside an unrelated longer token.
        if (triggerStart > 0 && !char.IsWhiteSpace(text[triggerStart - 1]))
            return;

        _suppressInputBoxTextChanged = true;
        InputBox.Text = text.Remove(triggerStart, SkillsTrigger.Length);
        InputBox.CaretIndex = triggerStart;
        _suppressInputBoxTextChanged = false;

        _skillsPickerOpen = true;
        try
        {
            var picked = await PickSkillAsync();
            if (picked is not null)
                InsertAtCaret($"/skill/{picked.Name} ");
            else
                InputBox.Focus();
        }
        finally
        {
            _skillsPickerOpen = false;
        }
    }

    // Opens the "+"/leading-"/" command menu (CommandMenuPopup) anchored above InputBox.
    // openedByTyping distinguishes a leading-"/" trigger (where accepting/dismissing must also
    // clean up the "/" text already sitting in InputBox) from the "+" button (which opens the
    // menu with InputBox untouched, so there's nothing there to clean up).
    private void OpenCommandMenu(bool openedByTyping)
    {
        _commandMenuOpenedByTyping = openedByTyping;
        _commandMenu.SetPrompts(_session.McpToolProvider.Prompts);
        _commandMenu.Open(InputBox);
        if (!openedByTyping)
            _commandMenu.UpdateFilter("");
    }

    // Mirrors MaybeTriggerSkillsPickerAsync's shape but generalized to any leading "/" rather than
    // one literal trigger string: fires as soon as the message is just "/" plus zero or more
    // non-whitespace characters at the very start (so free-form text containing a slash elsewhere,
    // e.g. "a/b", never opens the menu), and re-filters live as the user keeps typing. Whitespace
    // after the token (a space) closes the menu instead of filtering it to nothing, since at that
    // point the user has moved on to typing a command's argument or plain text.
    private static readonly Regex LeadingSlashTokenPattern = new(@"^/(\S*)$", RegexOptions.Compiled);

    private void UpdateCommandMenuFromTypedText()
    {
        if (_suppressInputBoxTextChanged)
            return;

        var text = InputBox.Text ?? "";
        var match = LeadingSlashTokenPattern.Match(text);
        if (!match.Success)
        {
            if (_commandMenu.IsOpen && _commandMenuOpenedByTyping)
                _commandMenu.Close();
            return;
        }

        if (!_commandMenu.IsOpen)
            OpenCommandMenu(openedByTyping: true);
        _commandMenu.UpdateFilter(match.Groups[1].Value);
    }

    // Handles a pick from CommandMenuPopup, whether it was opened by the "+" button or by typing
    // a leading "/". Every entry runs immediately on pick except an MCP prompt (see the Prompt
    // branch below, which splices instead) — so commands that take an argument (/resume,
    // /attach, /provider, /model, /skill) are dispatched with no argument, which each already
    // falls back to its own ListPickerWindow (or, for /attach, the native file picker) rather
    // than requiring the user to type one.
    private async Task OnCommandMenuAcceptedAsync(CommandMenuPopup.MenuEntry entry)
    {
        if (_commandMenuOpenedByTyping)
        {
            // Clear the in-progress "/foo" the user typed to trigger this menu — regardless of
            // which action they picked, that partial token isn't meant to remain in InputBox.
            _suppressInputBoxTextChanged = true;
            InputBox.Text = "";
            _suppressInputBoxTextChanged = false;
        }

        if (entry.Kind == CommandMenuPopup.MenuEntryKind.Attach)
        {
            await HandleAttachAsync(argument: null);
            return;
        }

        if (entry.Kind == CommandMenuPopup.MenuEntryKind.Prompt)
        {
            // Unlike every other entry (which dispatches immediately with no argument), an MCP
            // prompt is spliced into InputBox and left for the user to type its arguments —
            // mirrors HandleSkillsAsync's InsertAtCaret($"/skill/{name} ") exactly, and works
            // naturally with LeadingSlashTokenPattern: the trailing space this inserts is what
            // already makes UpdateCommandMenuFromTypedText close the menu on its own.
            var promptEntry = entry.PromptEntry!;
            InsertAtCaret($"/{promptEntry.FullName} ");
            var hint = McpPromptArguments.FormatArgumentHint(promptEntry.Prompt.ProtocolPrompt.Arguments);
            if (hint.Length > 0)
                AddToolLine($"Arguments for /{promptEntry.FullName}: {hint}");
            return;
        }

        var command = entry.Command!;
        AddToolLine($"$ {command.Name}");
        await TryHandleSlashCommandAsync(command.Name);
    }

    // Detects an in-progress "@token" ending exactly at the caret: an "@" preceded by start-of-
    // text or whitespace (so "foo@bar" can never trigger the popup — deliberately stricter than
    // Litos.Console's ComposerState.FindActiveMention, matching CommandMenuPopup's own leading-
    // token word-boundary convention instead), with no whitespace between the "@" and the caret.
    // Typing a space after the "@" therefore ends the in-progress mention (returns null), closing
    // the popup the same way UpdateCommandMenuFromTypedText closes on a non-matching leading-"/".
    internal static int FindActiveMentionStart(string text, int caret)
    {
        var at = text.LastIndexOf('@', Math.Max(0, caret - 1));
        if (at < 0)
            return -1;
        if (at > 0 && !char.IsWhiteSpace(text[at - 1]))
            return -1;

        for (var i = at + 1; i < caret; i++)
        {
            if (char.IsWhiteSpace(text[i]))
                return -1;
        }

        return at;
    }

    // Mirrors UpdateCommandMenuFromTypedText's shape for the "@"-mention popup: re-derives the
    // active mention (if any) from InputBox's current text/caret on every keystroke, opening/
    // filtering/closing MentionPopup accordingly. _activeMentionStart is cached from this pass so
    // OnMentionAccepted knows exactly what span to splice without re-deriving it (the popup's
    // Accepted event fires after the user has potentially moved the caret via arrow keys while
    // browsing suggestions, so re-deriving there could pick the wrong "@").
    private void UpdateMentionPopupFromTypedText()
    {
        if (_suppressInputBoxTextChanged)
            return;

        var text = InputBox.Text ?? "";
        var caret = InputBox.CaretIndex;
        var mentionStart = FindActiveMentionStart(text, caret);
        if (mentionStart < 0)
        {
            _activeMentionStart = -1;
            if (_mentionPopup.IsOpen)
                _mentionPopup.Close();
            return;
        }

        _activeMentionStart = mentionStart;
        var token = text[(mentionStart + 1)..caret];
        if (!_mentionPopup.IsOpen)
            _mentionPopup.Open(InputBox);
        _mentionPopup.UpdateFilter(_mentionIndex.GetOrBuild(), token);
    }

    // Splices the picked relative path into InputBox in place of the partial token that follows
    // the active "@", WITHOUT deleting the "@" itself — Litos.Console's SpliceMention documents
    // this as the fix for a historical dropped-"@" bug: removing from the "@" index itself
    // (rather than mentionStart + 1) would delete the "@" along with the partial token, so
    // MentionParser (which only recognizes text starting with "@") would never see the accepted
    // suggestion as a mention at send time.
    //
    // Re-derives the active mention from InputBox's current text/caret rather than trusting the
    // cached _activeMentionStart: MentionPopup.HandleFilterKeyDown only intercepts Down/Up/Enter/
    // Tab/Escape, so caret-moving keys (Left/Right/Home/End) or a mouse click can move the caret
    // out of the token — with no TextChanged event to re-run UpdateMentionPopupFromTypedText and
    // refresh/close the popup — while it's still showing suggestions for the stale token. Without
    // this re-check, accepting at that point would splice against a caret position the popup's
    // suggestions no longer correspond to.
    private void OnMentionAccepted(MentionPopup.MentionEntry entry)
    {
        var caret = InputBox.CaretIndex;
        var current = InputBox.Text ?? "";
        if (FindActiveMentionStart(current, caret) != _activeMentionStart)
        {
            _activeMentionStart = -1;
            return;
        }

        var removeFrom = _activeMentionStart + 1;
        var removeCount = caret - removeFrom;

        _suppressInputBoxTextChanged = true;
        InputBox.Text = current[..removeFrom] + entry.Path + current[(removeFrom + removeCount)..];
        _suppressInputBoxTextChanged = false;

        InputBox.CaretIndex = removeFrom + entry.Path.Length;
        InputBox.Focus();
        _activeMentionStart = -1;
    }

    /// <summary>
    /// Force-loads a skill's full SKILL.md body, the way pi's `/skill:name [args]` does: the
    /// model isn't always reliable about deciding on its own to call the `skill` tool for a
    /// matching task (pi's own docs note this), so this bypasses that judgment call entirely.
    /// Queues the body as a pending attachment — same mechanism /attach already uses — so it's
    /// folded into whatever the user sends as their next message, exactly like pi appending the
    /// skill content plus "User: &lt;args&gt;" ahead of the next turn, rather than faking a tool
    /// call the model never actually made.
    /// </summary>
    private async Task HandleSkillAsync(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            AddToolLine("Usage: /skill <name> [args]");
            return;
        }

        var parts = argument.Split(' ', 2, StringSplitOptions.TrimEntries);
        var name = parts[0];
        var args = parts.Length > 1 ? parts[1] : null;

        var error = await LoadSkillAsync(name, args);
        if (error is not null)
        {
            AddToolLine($"{error} Run /skills to see what's available.");
            return;
        }

        AddToolLine($"Loaded skill '{name}' — will be included in your next message.");
    }

    // SkillTool.InvokeAsync already does discovery-by-name + SKILL.md frontmatter stripping
    // (SkillFrontmatter is internal to Litos.Tools, so this reuses that logic instead of
    // duplicating a second copy of the parser here). Shared by both the whole-line "/skill
    // <name> [args]" command and the inline "/skill/<name>" tokens expanded at send time
    // (ExpandSkillTokens) — same load-and-queue behavior, just two different ways to name the
    // skill. Returns null on success, or an error message to surface to the user.
    private async Task<string?> LoadSkillAsync(string name, string? args)
    {
        var skillTool = new SkillTool(new SkillDiscovery(_transcript.WorkingDirectory));
        var toolArgs = JsonSerializer.SerializeToElement(new { name });
        var result = await skillTool.InvokeAsync(toolArgs, CancellationToken.None);
        if (result.IsError)
            return result.Text;

        var markdown = args is null ? result.Text : $"{result.Text}\n\nUser: {args}";
        _pendingAttachments.Add(new DocumentMarkdown(name, markdown, []));
        return null;
    }

    // KNOWN ISSUE (see ReadMe_AgentDesign.md §7.7): on very long content, the ScrollViewer's
    // scrollbar/clipping doesn't recompute correctly until something forces a fresh layout pass
    // — matches AvaloniaUI/Avalonia#3707/#4011/#3791. InvalidateMeasure()/InvalidateArrange()
    // alone don't reproduce that fix. What does: adding/removing a child of TranscriptPanel
    // (confirmed empirically — sending the next real message fixes the previous, already-clipped
    // content). NudgeTranscriptLayout() reproduces that trigger cheaply via an invisible
    // zero-size element instead of waiting for the user's next message.
    private void ScrollToEndAfterLayout() =>
        Dispatcher.UIThread.Post(() =>
        {
            TranscriptScroll.InvalidateMeasure();
            TranscriptScroll.InvalidateArrange();
            TranscriptScroll.ScrollToEnd();
        }, DispatcherPriority.Background);

    // The nudge itself needs to run strictly after the content it's fixing has already been
    // through one full layout pass — firing it in the same batch as the content that just got
    // added only "fixes" whatever was added *before* that batch (observed: it fixed the prior
    // assistant message, but the new user bubble in the same batch was still clipped). A short
    // delay via Task.Delay + Post gives Avalonia's layout system a real idle gap first.
    //
    // Reuses a single tracked phantom rather than creating a fresh one per call: this can be
    // called many times in quick succession (once per streamed token via AppendAssistantText),
    // and each call's own delayed remove used to race independently — a burst of calls could
    // leave more than one still-unremoved phantom parented in TranscriptPanel, which rendered as
    // a persistent stray horizontal bar in the transcript. Removing any previous phantom before
    // adding a new one guarantees at most one exists at a time.
    private async void NudgeTranscriptLayout()
    {
        if (_layoutNudgePhantom is not null)
        {
            TranscriptPanel.Children.Remove(_layoutNudgePhantom);
            _layoutNudgePhantom = null;
        }

        var phantom = new Border { Width = 0, Height = 0 };
        _layoutNudgePhantom = phantom;
        TranscriptPanel.Children.Add(phantom);

        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_layoutNudgePhantom, phantom))
                return; // superseded by a later call — that one already removed us
            TranscriptPanel.Children.Remove(phantom);
            _layoutNudgePhantom = null;
        });
    }

    private void ShowWorkingIndicator() =>
        Dispatcher.UIThread.Post(() =>
        {
            _workingIndicator = new Border
            {
                Padding = new Thickness(12, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new ProgressBar { Width = 32, Height = 4, IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock
                        {
                            Text = "Thinking…", Foreground = DimTextBrush, FontStyle = FontStyle.Italic,
                            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
            };
            TranscriptPanel.Children.Add(_workingIndicator);
            ScrollToEndAfterLayout();
        });

    private void HideWorkingIndicator() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_workingIndicator is null)
                return;
            TranscriptPanel.Children.Remove(_workingIndicator);
            _workingIndicator = null;
        });

    // Streams into a plain TextBlock rather than re-parsing MarkdownViewer.Markdown on every
    // token, since re-parsing the whole response dozens of times per second while it's still
    // growing is wasted work. FinalizeAssistantText swaps in a MarkdownViewer once, after
    // streaming ends, with Markdown set exactly one time against the complete text.
    private void AppendAssistantText(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_currentAssistantText is null)
            {
                var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.WhiteSmoke };
                _currentAssistantText = block;
                _currentAssistantBubble = AddBubbleContent(block, HorizontalAlignment.Left, AssistantBubbleBrush);
            }

            _currentAssistantMarkdown.Append(text);
            _currentAssistantText.Text = _currentAssistantMarkdown.ToString();
            ScrollToEndAfterLayout();
        });

    private void FinalizeAssistantText()
    {
        if (_currentAssistantBubble is null)
            return;

        var bubble = _currentAssistantBubble;
        var markdown = _currentAssistantMarkdown.ToString();
        Dispatcher.UIThread.Post(() => bubble.Child = NewAssistantBubbleContent(markdown, _transcript.WorkingDirectory));
        NudgeTranscriptLayout();

        _currentAssistantText = null;
        _currentAssistantBubble = null;
        _currentAssistantMarkdown.Clear();
    }

    // Wraps a completed assistant message's MarkdownViewer with a copy button beneath it, so the
    // raw markdown source (not the rendered/selected text) is always what's copied — simpler and
    // more reliable than relying on MarkdownViewer's SelectAll/GetSelectedText, and it works the
    // same way whether the bubble came from live streaming or history replay. Shared by both
    // construction sites (FinalizeAssistantText, RenderTranscriptHistory) so they can't drift.
    private static Control NewAssistantBubbleContent(string markdown, string? workingDirectory)
    {
        var viewer = NewMarkdownViewer(markdown, workingDirectory);

        var copyButton = new Button
        {
            Classes = { "CopyButton" },
            Content = "⧉ Copy",
            FontSize = 12,
        };
        CancellationTokenSource? resetCts = null;
        copyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copyButton)?.Clipboard is not { } clipboard)
                return;

            // Click is async void under the hood, so an uncaught exception here would unwind
            // past the UI event handler and crash the whole process (same hazard documented at
            // the turn loop's catch block above) — SetTextAsync can throw if another process
            // holds the clipboard open or it's otherwise unavailable.
            try
            {
                await clipboard.SetTextAsync(markdown);
            }
            catch (Exception)
            {
                return;
            }

            // Cancel any still-pending reset from an earlier click so a fast double-click can't
            // flicker the label back to "Copy" mid-way through the newest click's own window.
            resetCts?.Cancel();
            var cts = resetCts = new CancellationTokenSource();
            copyButton.Content = "✓ Copied";
            try
            {
                await Task.Delay(1500, cts.Token);
                copyButton.Content = "⧉ Copy";
            }
            catch (OperationCanceledException)
            {
            }
        };

        return new StackPanel { Children = { viewer, copyButton } };
    }

    // MarkdownViewer already renders [text](url) and bare autolinks as clickable and re-raises
    // its own LinkClicked event on click — it deliberately does not open anything itself (it has
    // no notion of "the OS default browser"), so every MarkdownViewer this window creates must
    // wire that event or a rendered link is inert. Centralized here so both construction sites
    // (streamed text in FinalizeAssistantText, restored history in the transcript-load switch)
    // can't drift out of sync.
    private static MarkdownViewer NewMarkdownViewer(string markdown, string? workingDirectory)
    {
        var viewer = new MarkdownViewer { Markdown = markdown };
        viewer.LinkClicked += (_, e) => OpenUrl(e.Url, workingDirectory);
        return viewer;
    }

    private static void OpenUrl(string url, string? workingDirectory)
    {
        if (IsOpenableHttpUrl(url, out var uri))
            TryLaunch(uri);
        else if (IsOpenableLocalFile(url, workingDirectory, out var path))
            TryLaunch(path);
    }

    // Only http/https is launched — markdown links are model-authored text, and Process.Start
    // with UseShellExecute hands the URI straight to the OS shell, so a hallucinated or
    // malicious non-http scheme (file://, a custom protocol handler, etc.) must be rejected
    // rather than launched. Pulled out of OpenUrl (same pattern as ResolveResumeWorkingDirectory)
    // so the validation rule is testable without a real Process.Start.
    internal static bool IsOpenableHttpUrl(string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    // A local-file link (file:// URI, bare path, or some other scheme the model invented to gesture
    // at "a local file" — e.g. observed in the wild as "sandbox:/C:/temp/diagram.png" — pointing at
    // a mermaid PNG the agent just rendered via ShellTool) is still model-authored text, so the same
    // hallucination/malice concern from IsOpenableHttpUrl applies. The scheme name itself isn't
    // trustworthy (unlike http/https, nothing constrains what a model calls a "local file" link), so
    // two other checks stand in: the resolved absolute path must fall under the session's own
    // working directory (nothing outside the sandbox the agent's tools actually write to), and it
    // must exist on disk (a hallucinated path never does). Only once both hold is
    // Process.Start(UseShellExecute: true) handed the path.
    internal static bool IsOpenableLocalFile(string url, string? workingDirectory, out string path)
    {
        path = null!;
        if (string.IsNullOrEmpty(workingDirectory))
            return false;

        // LocalPath resolves correctly for any scheme once the parser recognizes a drive-letter
        // authority (e.g. "sandbox:/C:/..." or "file:///C:/..."), not just for parsed.IsFile
        // ("file"/""/certain well-known schemes) — Uri itself is scheme-agnostic about this. Known
        // web/other schemes are excluded even though the later working-directory + File.Exists
        // checks would already neutralize them, so a non-file scheme reads as "rejected", not
        // "coincidentally resolved to nothing."
        // A bare relative filename (e.g. "dml-prevention.png", the form models most often emit for
        // a file they just wrote alongside the session) has no scheme and isn't rooted, so it used
        // to match neither branch above and the link was silently inert. It's resolved against
        // workingDirectory below, same as a rooted path is.
        string candidate;
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && !parsed.IsUnc &&
            parsed.Scheme is not ("http" or "https" or "ftp" or "ftps" or "mailto" or "javascript" or "data" or "ws" or "wss"))
            candidate = parsed.LocalPath;
        else if (Path.IsPathRooted(url) || Uri.TryCreate(url, UriKind.Relative, out _))
            candidate = url;
        else
            return false;

        string fullPath, fullWorkingDirectory;
        try
        {
            fullWorkingDirectory = Path.GetFullPath(workingDirectory);
            fullPath = Path.IsPathRooted(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(Path.Combine(fullWorkingDirectory, candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!fullPath.StartsWith(
                fullWorkingDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(
                fullWorkingDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(fullPath))
            return false;

        path = fullPath;
        return true;
    }

    private static void TryLaunch(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No browser/handler available for this URI on the host — nothing more Litos can do.
        }
    }

    private static void TryLaunch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No default handler for this file type on the host — nothing more Litos can do.
        }
    }

    private void AddBubble(string text, HorizontalAlignment alignment, IBrush background) =>
        Dispatcher.UIThread.Post(() =>
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.WhiteSmoke,
            };
            AddBubbleContent(block, alignment, background);
        });

    // Sent attachments/skills have no other trace in the transcript (the staging strip is
    // cleared on send, and skill tokens are stripped from the visible text by
    // ExpandSkillTokensAsync), so their names are rendered as small dim lines above the message
    // text — the only remaining record of what was folded into this turn. Attachments and skills
    // get distinct glyphs (📎 vs 🧩) and their own line so the two aren't visually conflated.
    private void AddUserBubble(string text, IReadOnlyList<string> attachedNames, IReadOnlyList<string> skillNames) =>
        Dispatcher.UIThread.Post(() =>
        {
            var textBlock = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.WhiteSmoke,
            };

            if (attachedNames.Count == 0 && skillNames.Count == 0)
            {
                AddBubbleContent(textBlock, HorizontalAlignment.Right, UserBubbleBrush);
                return;
            }

            var stack = new StackPanel();
            if (skillNames.Count > 0)
                stack.Children.Add(BuildBubbleLabel("🧩 " + string.Join(", ", skillNames)));
            if (attachedNames.Count > 0)
                stack.Children.Add(BuildBubbleLabel("📎 " + string.Join(", ", attachedNames)));
            stack.Children.Add(textBlock);

            AddBubbleContent(stack, HorizontalAlignment.Right, UserBubbleBrush);
        });

    private TextBlock BuildBubbleLabel(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        FontStyle = FontStyle.Italic,
        Foreground = DimTextBrush,
        Margin = new Thickness(0, 0, 0, 4),
    };

    // Must be called on the UI thread. Callers already inside a Dispatcher.UIThread.Post
    // (AppendAssistantText) call this directly; AddBubble wraps it in its own Post for callers
    // that aren't already on the UI thread.
    private Border AddBubbleContent(Control content, HorizontalAlignment alignment, IBrush background)
    {
        var border = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8),
            MaxWidth = 640,
            HorizontalAlignment = alignment,
            Child = content,
        };
        TranscriptPanel.Children.Add(border);
        ScrollToEndAfterLayout();
        NudgeTranscriptLayout();
        return border;
    }

    private void AddToolLine(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            TranscriptPanel.Children.Add(NewToolTextBlock(text));
            ScrollToEndAfterLayout();
        });

    /// <summary>
    /// Rebuilds TranscriptPanel from a loaded Transcript's messages — the piece /resume and
    /// /branch were missing: both reload _transcript for the model's own context, but neither
    /// used to put a single line of the prior conversation back on screen, so a resumed session
    /// looked empty until the next turn. Renders statically (no streaming, no working indicator)
    /// since every message here already completed; tool_use/tool_result pairs are matched by
    /// CallId same as the live path, but in one pass instead of ToolCallRow's queue-per-CallId
    /// (nothing here is still "running"). _toolRows is cleared first since it's keyed by CallId
    /// and a stale entry from whatever session was live before this resume/branch could otherwise
    /// be mismatched against a live turn's own CallIds afterward. Known limitation: attachment/
    /// skill name labels (the 📎/🧩 lines AddUserBubble draws) don't reappear here — that
    /// metadata is folded into the persisted text/never written to the transcript at all
    /// (BuildTurnContent), so there's nothing left on disk to reconstruct them from.
    /// </summary>
    private void RenderTranscriptHistory(Transcript transcript) =>
        Dispatcher.UIThread.Post(() =>
        {
            TranscriptPanel.Children.Clear();
            _toolRows.Clear();

            // GroupBy+ToDictionary(g => g.Last()) rather than a straight ToDictionary: CallId is
            // only unique within one tool-call round in practice, and nothing stops a future
            // provider retry/replay from reusing one across a session's full history — a straight
            // ToDictionary would throw and take down the whole resume/branch on that transcript.
            var resultsByCallId = transcript.Messages
                .SelectMany(m => m.Content.OfType<Litos.Agent.Messages.ToolResultBlock>())
                .GroupBy(r => r.CallId)
                .ToDictionary(g => g.Key, g => new ToolResult(g.Last().Text, g.Last().IsError));

            foreach (var message in transcript.Messages)
            {
                foreach (var block in message.Content)
                {
                    switch (block)
                    {
                        case MessageTextBlock text when message.Role == Litos.Agent.Messages.Role.User:
                            AddBubbleContent(
                                new TextBlock { Text = text.Text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.WhiteSmoke },
                                HorizontalAlignment.Right, UserBubbleBrush);
                            break;
                        case MessageTextBlock text:
                            AddBubbleContent(NewAssistantBubbleContent(text.Text, _transcript.WorkingDirectory), HorizontalAlignment.Left, AssistantBubbleBrush);
                            break;
                        case Litos.Agent.Messages.ToolUseBlock toolUse:
                            var row = ToolCallRow.Create(toolUse.ToolName, toolUse.Arguments, ToolTextBrush, ToolErrorBrush);
                            row.Toggled += () => { ScrollToEndAfterLayout(); NudgeTranscriptLayout(); };
                            if (resultsByCallId.TryGetValue(toolUse.CallId, out var result))
                                row.RenderCompleted(result);
                            else
                                row.RenderRunning();
                            TranscriptPanel.Children.Add(row.Root);
                            break;
                        case Litos.Agent.Messages.ToolResultBlock:
                            break; // already folded into its ToolUseBlock's row above
                        case Litos.Agent.Messages.CompactionSummaryBlock summary:
                            TranscriptPanel.Children.Add(NewToolTextBlock($"⚏ [history compacted: {summary.Summary}]"));
                            break;
                    }
                }
            }

            ScrollToEndAfterLayout();
            NudgeTranscriptLayout();
        });

    private static TextBlock NewToolTextBlock(string? text = null) => new()
    {
        Text = text,
        Foreground = ToolTextBrush,
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        Margin = new Thickness(4, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    private void AddToolCallRow(string callId, string toolName, JsonElement arguments) =>
        Dispatcher.UIThread.Post(() =>
        {
            var row = ToolCallRow.Create(toolName, arguments, ToolTextBrush, ToolErrorBrush);
            row.Toggled += () => { ScrollToEndAfterLayout(); NudgeTranscriptLayout(); };
            row.RenderRunning();
            // AgentLoop deliberately tolerates a provider emitting the same CallId more than
            // once in a round (see AgentLoopTests.RunTurnAsync_DuplicateToolCallIds_...), so a
            // plain Dictionary<CallId, Row> would let a second AddToolCallRow silently overwrite
            // the first row's entry — orphaning it mid-"running" and misattributing the later
            // result to the wrong row. A per-CallId queue keeps rows FIFO-ordered so
            // CompleteToolCallRow completes them in the same order AgentLoop invoked them.
            if (!_toolRows.TryGetValue(callId, out var queue))
                _toolRows[callId] = queue = new Queue<ToolCallRow>();
            queue.Enqueue(row);
            TranscriptPanel.Children.Add(row.Root);
            ScrollToEndAfterLayout();
        });

    private void CompleteToolCallRow(string callId, ToolResult result) =>
        Dispatcher.UIThread.Post(() =>
        {
            // A CallId this GUI instance never rendered a running row for (shouldn't happen
            // given AgentLoop's current sequential invocation, but avoids a crash if it ever did).
            if (!_toolRows.TryGetValue(callId, out var queue) || queue.Count == 0)
                return;

            queue.Dequeue().RenderCompleted(result);
            if (queue.Count == 0)
                _toolRows.Remove(callId);
        });
}
