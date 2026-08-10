using System.Threading.Channels;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Console;
using Litos.Console.Terminal;
using Litos.Host;
using Litos.Tools.Attachments;
using Litos.Tools.Shell;
using Litos.Tools.Skills;
using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui.App;

var config = LitosConfig.Load();

static string? GetArgValue(string[] args, string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var requestedProvider = GetArgValue(args, "--provider");
var requestedModel = GetArgValue(args, "--model");

// Terminal.Gui needs a real interactive terminal; piped/redirected input (scripted/test usage)
// keeps working via a plain-ReadLine, plain-Console.WriteLine fallback path, mirroring the old
// canPrompt gate that governed MentionInputPrompt/SelectionPrompt usage under Spectre.Console.
var canPrompt = !System.Console.IsInputRedirected && !System.Console.IsOutputRedirected;

// Application.Create() is the current, non-obsolete entry point (the static Application.Init/
// Instance/Shutdown facade this used to go through is marked obsolete in 2.0.0-rc.64 — "the
// legacy static Application object is going away"). AppModel must be set before Init per its own
// doc comment.
IApplication? app = null;
if (canPrompt)
{
    app = Application.Create();
    app.AppModel = AppModel.Inline;
    app.Init(null);

    // Maximizing/resizing the terminal window (Windows Terminal in particular) can leave stale
    // rows from the old, smaller screen buffer behind, visually "duplicating" the transcript
    // below the live region — the driver grows/scrolls the buffer but doesn't always force a
    // full clear+redraw of the newly-exposed rectangle on its own. ClearScreenNextIteration
    // forces exactly that (screen clear + full View-hierarchy redraw) on the next main-loop
    // iteration, so it's a cheap, reliable belt-and-suspenders fix to request on every detected
    // size change rather than relying solely on the driver's internal handling.
    app.ScreenChanged += (_, _) => app.ClearScreenNextIteration = true;
}

var hasChatProviderKey = config.AvailableChatProviders.Count > 0;
if (!hasChatProviderKey && canPrompt)
    config = SetupWizardDialog.Run(app!);

var availableProviders = config.AvailableChatProviders.ToList();
if (availableProviders.Count == 0)
{
    System.Console.WriteLine("No API key found for any provider. Set ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, OPENROUTER_API_KEY, or LOCAL_BASE_URL and try again.");
    app?.Dispose();
    return 1;
}

if (!config.ApiKeys.ContainsKey("tavily"))
    System.Console.WriteLine("Web search disabled: set TAVILY_API_KEY to enable.");

var activeProviderName = requestedProvider ?? config.DefaultProvider;
if (!config.IsProviderConfigured(activeProviderName))
    activeProviderName = availableProviders[0];

if (requestedProvider is null && canPrompt && availableProviders.Count > 1)
    activeProviderName = ModelPickerDialog.PickProvider(app!, availableProviders, activeProviderName);

var services = new ServiceCollection().AddLitosAgent(config);
services.AddSingleton<IToolApprovalGate>(_ => canPrompt
    ? new ApprovalDialog(app!)
    : new NonInteractiveApprovalGate());
var serviceProvider = services.BuildServiceProvider();
var providerFactory = serviceProvider.GetRequiredService<IChatProviderFactory>();
var chatProvider = providerFactory.Resolve(activeProviderName);
// Built once at startup — Litos.Console doesn't run a per-turn loop the way Litos.Api's
// AgentWorker does, and dynamic MCP tool discovery is out of scope for this face, so one static
// snapshot for the process's lifetime matches today's existing behavior exactly.
var toolRegistry = serviceProvider.GetRequiredService<ToolRegistryFactory>().Create();
var loop = serviceProvider.GetRequiredService<AgentLoopFactory>().Create(chatProvider, toolRegistry);

var compactor = serviceProvider.GetRequiredService<Compactor>();
var transcriptStore = serviceProvider.GetRequiredService<ITranscriptStore>();
var attachmentConverter = serviceProvider.GetRequiredService<IAttachmentConverter>();
var skillDiscovery = serviceProvider.GetRequiredService<ISkillDiscovery>();
var transcript = Transcript.CreateNew(Directory.GetCurrentDirectory());
var sessionId = Guid.NewGuid().ToString("n");
var pendingAttachments = new List<DocumentMarkdown>();
var pendingImages = new List<ImageBlock>();
var attachHandler = new AttachHandler(attachmentConverter);

void ApplyAttachResult(AttachResult result, Action<string> printLine)
{
    if (result.Document is not null)
        pendingAttachments.Add(result.Document);
    if (result.Image is not null)
        pendingImages.Add(result.Image);
    foreach (var line in result.Lines)
        printLine(line);
}

string model;
if (requestedModel is not null)
{
    model = requestedModel;
}
else
{
    IReadOnlyList<ModelInfo> models;
    try
    {
        models = await chatProvider.ListModelsAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        System.Console.WriteLine($"Could not list models ({ex.Message}); falling back to config default.");
        models = [];
    }

    if (models.Count == 0)
    {
        model = config.DefaultModel ?? throw new InvalidOperationException("No models available and no default model configured.");
    }
    else if (canPrompt)
    {
        model = ModelPickerDialog.PickModel(app!, models).Id;
    }
    else
    {
        model = config.DefaultModel ?? models.First().Id;
    }
}

if (!canPrompt)
    return await RunNonInteractiveAsync();

return RunInteractive();

// ---- Non-interactive (piped input / redirected output) path ----------------------------------
async Task<int> RunNonInteractiveAsync()
{
    System.Console.WriteLine($"LitosAiAgent — provider: {activeProviderName}, model: {model}");

    while (true)
    {
        var input = System.Console.ReadLine();
        if (input is null)
            break;
        if (string.IsNullOrWhiteSpace(input))
            continue;

        if (input.StartsWith('/'))
        {
            var handled = await TryHandleSlashCommandAsync(input, System.Console.WriteLine, null);
            if (handled)
                continue;
        }

        await ResolveMentionsAsync(input, System.Console.WriteLine);

        var turnContent = BuildTurnContent(input);
        var steeringChannel = Channel.CreateUnbounded<SteeringMessage>();
        using var turnCts = new CancellationTokenSource();

        try
        {
            await foreach (var evt in loop.RunTurnAsync(SessionOwner.Local, sessionId, transcript, model, turnContent, turnCts.Token, steeringChannel.Reader))
            {
                switch (evt)
                {
                    case TextDelta delta:
                        System.Console.Write(delta.Text);
                        break;
                    case ToolCallCompleted toolCall:
                        System.Console.WriteLine();
                        System.Console.WriteLine($"[tool] {toolCall.ToolName}{DescribeToolArgs(toolCall)}");
                        break;
                    case ToolCallSkipped skipped:
                        System.Console.WriteLine($"[skipped {skipped.CallId}: {skipped.Reason}]");
                        break;
                    case MessageCompleted:
                        System.Console.WriteLine();
                        break;
                    case ErrorOccurred error:
                        System.Console.WriteLine($"Error: {error.Exception.Message}");
                        break;
                    case CompactionOccurred:
                        System.Console.WriteLine("— context compacted —");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
        {
            System.Console.WriteLine("Turn aborted.");
        }
        finally
        {
            steeringChannel.Writer.TryComplete();
        }
    }

    return 0;
}

// ---- Interactive Terminal.Gui path -------------------------------------------------------------
int RunInteractive()
{
    var litosApp = new LitosApp(() => transcript.WorkingDirectory ?? Directory.GetCurrentDirectory());

    // Called both from Terminal.Gui's own main-loop thread (e.g. directly inside a Composer
    // event handler) and from background thread-pool continuations resumed after an `await` on
    // real I/O (slash commands, attachment conversion, RunTurnAsync's event loop) — every path
    // here funnels through app.Invoke so the View mutation always lands on the UI thread. Per
    // IApplication.Invoke's documented semantics, calling it from the main thread itself just
    // runs the action immediately/synchronously, so this is a no-op cost on that path.
    void PrintLine(string text) => app!.Invoke(() => litosApp.Transcript.AppendCommitted(text));

    PrintLine($"LitosAiAgent — provider: {activeProviderName}, model: {model}");
    PrintLine("Type your message and press Enter. While a turn is running: Enter steers, Alt+Enter queues a follow-up, Esc aborts (unsent text is restored). Ctrl+C to quit.");

    string? restoredPromptText = null;
    CancellationTokenSource? currentTurnCts = null;
    Channel<SteeringMessage>? currentSteeringChannel = null;

    async Task RunTurnAsync(IReadOnlyList<ContentBlock> turnContent)
    {
        var steeringChannel = Channel.CreateUnbounded<SteeringMessage>();
        currentSteeringChannel = steeringChannel;
        using var turnCts = new CancellationTokenSource();
        currentTurnCts = turnCts;

        // AgentLoop.RunTurnAsync's IAsyncEnumerable resumes its continuations wherever the
        // awaited provider.StreamAsync/tool-execution work happens to complete — a thread-pool
        // thread, not Terminal.Gui's main UI loop thread (Terminal.Gui's console driver installs
        // no SynchronizationContext the way WinForms/WPF do). Every View mutation below (on
        // litosApp.Composer/Working/Transcript) must therefore be marshaled onto the UI thread
        // via app.Invoke, exactly like ApprovalDialog does for the same reason. app.Invoke is
        // confirmed (Terminal.Gui.xml) to run synchronously only when already called from the
        // main thread; otherwise it queues the action via AddTimeout(TimeSpan.Zero) and returns
        // immediately, executed on the next main loop iteration — fire-and-forget from here, but
        // FIFO-ordered, so issuing one Invoke per event still applies them in arrival order.
        app!.Invoke(() =>
        {
            litosApp.Composer.IsTurnInFlight = true;
            litosApp.Working.Start();
        });

        var firstEventSeen = false;

        // Accumulates ONLY the in-progress turn's streamed reply text, as TextDelta fragments
        // arrive. TranscriptView.SetLive expects just this live suffix — it glues the committed
        // transcript and the live suffix together itself (with a separating newline), inserting
        // it at render time via Refresh(). Passing litosApp.Transcript.Text (the ALREADY-RENDERED,
        // already-committed-inclusive full transcript) back into SetLive, as this used to do, fed
        // the whole transcript-so-far back in as if it were newly-typed live text: Refresh() then
        // prepended the real committed text again in front of it, so each delta re-duplicated
        // everything committed before it (compounding every subsequent delta and every subsequent
        // turn), and — since litosApp.Transcript.Text right after the prompt-echo AppendCommitted
        // carries no trailing newline — glued the first delta's text directly onto the echoed "> "
        // line with no separator ("> Hello how are you?Hello! I'm doing well...").
        var liveReplyText = string.Empty;

        try
        {
            await foreach (var evt in loop.RunTurnAsync(SessionOwner.Local, sessionId, transcript, model, turnContent, turnCts.Token, steeringChannel.Reader))
            {
                var stopWorking = !firstEventSeen;
                firstEventSeen = true;

                app!.Invoke(() =>
                {
                    if (stopWorking)
                        litosApp.Working.Stop();

                    switch (evt)
                    {
                        case TextDelta delta:
                            liveReplyText += delta.Text;
                            litosApp.Transcript.SetLive(liveReplyText);
                            break;

                        case ToolCallCompleted toolCall:
                            litosApp.Transcript.CommitLive();
                            liveReplyText = string.Empty;
                            litosApp.Transcript.AppendCommitted($"▶ {toolCall.ToolName}{DescribeToolArgs(toolCall)}");
                            litosApp.Working.Start();
                            break;

                        case ToolCallSkipped skipped:
                            litosApp.Transcript.CommitLive();
                            liveReplyText = string.Empty;
                            litosApp.Transcript.AppendCommitted($"⏭ skipped {skipped.CallId}: {skipped.Reason}");
                            break;

                        case MessageCompleted:
                            litosApp.Transcript.CommitLive();
                            liveReplyText = string.Empty;
                            litosApp.Title = litosApp.Transcript.DebugScrollState();
                            break;

                        case ErrorOccurred error:
                            litosApp.Transcript.CommitLive();
                            liveReplyText = string.Empty;
                            litosApp.Transcript.AppendCommitted($"Error: {error.Exception.Message}");
                            break;

                        case CompactionOccurred:
                            litosApp.Transcript.CommitLive();
                            liveReplyText = string.Empty;
                            litosApp.Transcript.AppendCommitted("— context compacted —");
                            break;
                    }
                });

                // A tool call restarts the working indicator for the gap until the next event —
                // mirrors the old firstEventSeen reset so StopWorking fires again next time.
                if (evt is ToolCallCompleted)
                    firstEventSeen = false;
            }
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
        {
            app!.Invoke(() =>
            {
                litosApp.Transcript.CommitLive();
                litosApp.Transcript.AppendCommitted("Turn aborted.");
            });
        }
        catch (Exception ex)
        {
            // Anything else (a provider/network fault the tool-call safety net in AgentLoop
            // itself doesn't cover, a transcript-store I/O failure, etc.) must not disappear as
            // an unobserved task exception — RunTurnAsync is always started fire-and-forget.
            app!.Invoke(() =>
            {
                litosApp.Transcript.CommitLive();
                litosApp.Transcript.AppendCommitted($"Error: {ex.Message}");
            });
        }
        finally
        {
            steeringChannel.Writer.TryComplete();
            currentTurnCts = null;
            currentSteeringChannel = null;

            var textToRestore = restoredPromptText;
            restoredPromptText = null;

            app!.Invoke(() =>
            {
                litosApp.Working.Stop();
                litosApp.Composer.IsTurnInFlight = false;

                if (textToRestore is not null)
                    litosApp.Composer.RestoreText(textToRestore);
            });
        }
    }

    async Task HandleSubmitAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return;

        // Fire-and-forget from Composer.Submitted (an event, so no caller awaits this): an
        // unhandled exception here would become an unobserved task exception — silent to the
        // user, with the app appearing to just do nothing on that submission — rather than the
        // slash-command/attachment/turn failure being visible anywhere. Report it into the
        // transcript instead of letting it disappear.
        try
        {
            if (input.StartsWith('/'))
            {
                var handled = await TryHandleSlashCommandAsync(input, PrintLine, app);
                if (handled)
                    return;
            }

            await ResolveMentionsAsync(input, PrintLine);

            var turnContent = BuildTurnContent(input);
            PrintLine($"> {input}");
            await RunTurnAsync(turnContent);
        }
        catch (Exception ex)
        {
            PrintLine($"Error: {ex.Message}");
        }
    }

    litosApp.Composer.Submitted += text => _ = HandleSubmitAsync(text);
    litosApp.Composer.Steered += text =>
    {
        if (currentSteeringChannel is not null && !currentSteeringChannel.Writer.TryWrite(new SteeringMessage(text, SteeringMode.Steer)))
            restoredPromptText = text;
    };
    litosApp.Composer.FollowedUp += text =>
    {
        if (currentSteeringChannel is not null && !currentSteeringChannel.Writer.TryWrite(new SteeringMessage(text, SteeringMode.FollowUp)))
            restoredPromptText = text;
    };
    litosApp.Composer.Aborted += leftover =>
    {
        restoredPromptText = leftover;
        currentTurnCts?.Cancel();
    };
    litosApp.Composer.EmptyEnterHintRequested += () =>
        PrintLine("Type your steering message first, then press Enter to send it (Alt+Enter to follow up instead).");

    app!.Run(litosApp, null);
    app.Dispose();
    return 0;
}

IReadOnlyList<ContentBlock> BuildTurnContent(string input)
{
    var turnText = pendingAttachments.Count == 0
        ? input
        : string.Join("\n\n", pendingAttachments
            .Select(a => $"### Attachment: {a.Title}\n\n{a.Markdown}")
            .Append(input));
    pendingAttachments.Clear();

    var turnContent = new List<ContentBlock>(pendingImages.Count + 1) { new TextBlock(turnText) };
    turnContent.AddRange(pendingImages);
    pendingImages.Clear();
    return turnContent;
}

async Task ResolveMentionsAsync(string input, Action<string> printLine)
{
    foreach (var rawMention in MentionParser.ExtractPaths(input))
    {
        // The raw capture can include trailing sentence words ("@Plant Resource.png please
        // describe it") since spaces are allowed in filenames — try the longest candidate
        // first and shrink until something on disk actually matches.
        var mentionPath = MentionParser.ExpandCandidates(rawMention)
            .FirstOrDefault(candidate => File.Exists(candidate) || Directory.Exists(candidate));

        if (mentionPath is null)
        {
            printLine($"@{rawMention}: no such file or directory, ignoring mention.");
            continue;
        }

        if (Directory.Exists(mentionPath))
        {
            var entries = Directory.EnumerateFileSystemEntries(mentionPath)
                .Select(entry => Directory.Exists(entry) ? $"{Path.GetFileName(entry)}/" : Path.GetFileName(entry))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            pendingAttachments.Add(new DocumentMarkdown(mentionPath, string.Join('\n', entries), []));
            continue;
        }

        try
        {
            ApplyAttachResult(await attachHandler.AttachPathAsync(mentionPath, CancellationToken.None), printLine);
        }
        catch (Exception ex)
        {
            printLine($"Could not read @{mentionPath}: {ex.Message}");
        }
    }
}

string DescribeToolArgs(ToolCallCompleted toolCall)
{
    if (toolCall.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object)
        return string.Empty;

    var propertyName = toolCall.ToolName switch
    {
        "read_file" or "write_file" or "edit_file" or "list_directory" => "path",
        "shell" => "command",
        "skill" => "name",
        _ => null,
    };

    if (propertyName is null)
        return string.Empty;

    return toolCall.Arguments.TryGetProperty(propertyName, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
        ? $"  {value.GetString()}"
        : string.Empty;
}

async Task<bool> TryHandleSlashCommandAsync(string commandLine, Action<string> printLine, IApplication? interactiveApp)
{
    var parts = commandLine.Split(' ', 2, StringSplitOptions.TrimEntries);
    var command = parts[0];
    var argument = parts.Length > 1 ? parts[1] : null;

    switch (command)
    {
        case "/new":
            sessionId = Guid.NewGuid().ToString("n");
            transcript = Transcript.CreateNew(Directory.GetCurrentDirectory());
            printLine($"Started new session {sessionId}.");
            return true;

        case "/resume":
        {
            var sessions = await transcriptStore.ListSessionsAsync(SessionOwner.Local, CancellationToken.None);
            var pickedId = argument ?? (interactiveApp is not null ? SessionPickerDialog.PickSessionId(interactiveApp, sessions) : null);
            if (pickedId is null)
            {
                SessionPickerDialog.RenderTable(sessions, printLine);
                printLine("Usage: /resume <sessionId>");
                return true;
            }

            sessionId = pickedId;
            transcript = await Transcript.LoadAsync(transcriptStore, SessionOwner.Local, sessionId, CancellationToken.None);
            printLine($"Resumed session {sessionId} ({transcript.Messages.Count} messages).");
            WarnIfWorkingDirectoryMissing(transcript.WorkingDirectory, printLine);
            return true;
        }

        case "/attach":
        {
            // URLs can't be browsed by a file picker, so they always attach directly — same
            // as before this command grew a picker. A plain path argument only seeds the
            // picker's starting location when running interactively; non-interactively (no
            // picker available) it's attached directly instead, same as before.
            var isUrl = Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            if (isUrl || interactiveApp is null)
            {
                if (argument is null)
                {
                    printLine("Usage: /attach <path-or-url>");
                    return true;
                }

                try
                {
                    ApplyAttachResult(await attachHandler.AttachArgumentAsync(argument, CancellationToken.None), printLine);
                }
                catch (Exception ex)
                {
                    printLine($"Could not convert attachment: {ex.Message}");
                }

                return true;
            }

            var picked = await AttachDialog.PickFilesAsync(interactiveApp, argument ?? Directory.GetCurrentDirectory());
            foreach (var path in picked)
            {
                try
                {
                    ApplyAttachResult(await attachHandler.AttachPathAsync(path, CancellationToken.None), printLine);
                    printLine($"Attached '{path}' — will be included in your next message.");
                }
                catch (Exception ex)
                {
                    printLine($"Could not convert attachment '{path}': {ex.Message}");
                }
            }

            return true;
        }

        case "/provider":
        {
            var pickedProvider = argument ?? (interactiveApp is not null ? ModelPickerDialog.PickProvider(interactiveApp, availableProviders, activeProviderName) : null);
            if (pickedProvider is null || !availableProviders.Contains(pickedProvider))
            {
                printLine($"Usage: /provider <{string.Join('|', availableProviders)}>");
                return true;
            }

            activeProviderName = pickedProvider;
            chatProvider = providerFactory.Resolve(activeProviderName);
            loop = serviceProvider.GetRequiredService<AgentLoopFactory>().Create(chatProvider, toolRegistry);

            // Model ids aren't portable across providers, so switching providers always
            // resets to that provider's own default rather than carrying the old id over.
            var newProviderModels = await chatProvider.ListModelsAsync(CancellationToken.None);
            model = newProviderModels.FirstOrDefault(m => m.IsDefault)?.Id ?? newProviderModels.FirstOrDefault()?.Id ?? model;
            printLine($"Switched to provider '{activeProviderName}', model '{model}'.");
            return true;
        }

        case "/model":
        {
            if (argument is not null)
            {
                model = argument;
                printLine($"Switched to model '{model}'.");
                return true;
            }

            if (interactiveApp is null)
            {
                printLine("Usage: /model <modelId>");
                return true;
            }

            var models = await chatProvider.ListModelsAsync(CancellationToken.None);
            if (models.Count == 0)
            {
                printLine("No models available to pick from.");
                return true;
            }

            model = ModelPickerDialog.PickModel(interactiveApp, models).Id;
            printLine($"Switched to model '{model}'.");
            return true;
        }

        case "/branch":
        {
            if (argument is null || !int.TryParse(argument, out var uptoEntryIndex))
            {
                printLine("Usage: /branch <messageIndex> — branches the current session, keeping the first N transcript entries (snapped backward if N would split a tool call from its result).");
                return true;
            }

            var newSessionId = await transcriptStore.BranchAsync(SessionOwner.Local, sessionId, uptoEntryIndex, CancellationToken.None);
            sessionId = newSessionId;
            transcript = await Transcript.LoadAsync(transcriptStore, SessionOwner.Local, sessionId, CancellationToken.None);
            printLine($"Branched into new session {sessionId} ({transcript.Messages.Count} messages kept).");
            WarnIfWorkingDirectoryMissing(transcript.WorkingDirectory, printLine);
            return true;
        }

        case "/compact":
        {
            var compacted = await compactor.ForceCompactAsync(transcript, chatProvider, model, CancellationToken.None);
            if (!compacted)
            {
                printLine("Nothing to compact yet.");
                return true;
            }

            // Mirrors the batch/interactive loops' own persistence of an automatic
            // CompactionOccurred (see below) — JSONL is append-only, so only the new synthetic
            // summary message is appended; the original messages stay on disk.
            await transcriptStore.AppendAsync(SessionOwner.Local, sessionId, TranscriptEntry.FromMessage(transcript.Messages[0]), CancellationToken.None);

            printLine("— context compacted —");
            return true;
        }

        case "/skills":
        {
            var skills = await skillDiscovery.DiscoverAsync(CancellationToken.None);
            if (skills.Count == 0)
            {
                printLine("No skills found under .litos/skills/, ~/.litos/skills/, or ~/.claude/skills/.");
                return true;
            }

            foreach (var skill in skills)
                printLine($"{skill.Name,-24} {skill.Description}  [{skill.DirectoryPath}]");
            return true;
        }

        case "/export":
        {
            if (argument is null)
            {
                printLine("Usage: /export <path>");
                return true;
            }

            await using var writer = new StreamWriter(argument, append: false);
            await foreach (var entry in transcriptStore.ReadAsync(SessionOwner.Local, sessionId, CancellationToken.None))
            {
                var text = entry.Message?.Content.OfType<TextBlock>().FirstOrDefault()?.Text;
                if (text is not null)
                    await writer.WriteLineAsync($"### {entry.Kind} ({entry.Timestamp:u})\n\n{text}\n");
            }

            printLine($"Exported session {sessionId} to {argument}.");
            return true;
        }

        default:
            printLine($"Unknown command: {command}");
            return true;
    }
}

void WarnIfWorkingDirectoryMissing(string? workingDirectory, Action<string> printLine)
{
    if (workingDirectory is null)
    {
        printLine("This session predates working-directory tracking; using the current directory instead.");
        return;
    }

    if (!Directory.Exists(workingDirectory))
        printLine($"This session's working directory no longer exists: {workingDirectory}");
    else if (!string.Equals(workingDirectory, Directory.GetCurrentDirectory(), StringComparison.OrdinalIgnoreCase))
        printLine($"Session working directory: {workingDirectory}");
}

/// <summary>
/// Non-interactive fallback for IToolApprovalGate when input/output is redirected (scripted/test
/// usage) — a modal Terminal.Gui Dialog can't be shown at all in that case. Mirrors the old
/// ConsoleApprovalGate's plain y/N/a ReadLine prompt.
/// </summary>
sealed class NonInteractiveApprovalGate : IToolApprovalGate
{
    private bool _approveAll;

    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        if (_approveAll)
            return Task.FromResult(ApprovalDecision.Approve);

        System.Console.WriteLine(preview.Summary);
        if (preview.DiffOrCommand is { } content)
            System.Console.WriteLine(content);

        System.Console.Write("Approve? [y/N/a=always] ");
        var decision = System.Console.ReadLine()?.Trim().ToLowerInvariant() switch
        {
            "y" => ApprovalDecision.Approve,
            "a" => ApprovalDecision.ApproveAlways,
            _ => ApprovalDecision.Deny,
        };

        if (decision == ApprovalDecision.ApproveAlways)
            _approveAll = true;

        return Task.FromResult(decision);
    }
}
