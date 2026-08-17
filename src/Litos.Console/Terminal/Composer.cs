using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using LineStyle = Terminal.Gui.Drawing.LineStyle;
using GuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace Litos.Console.Terminal;

/// <summary>
/// The always-present bottom input box. Unifies what were previously TWO separate input paths
/// under Spectre.Console — MentionInputPrompt (the primary '>' prompt between turns) and
/// SteeringKeyWatcher (a background poll loop during a turn) — into ONE component, per
/// ReadMe_AgentDesign.md §7.5: the same TextView instance is present throughout the app's
/// lifetime, and only whether Enter/Alt+Enter/Esc mean "submit/steer/follow-up/abort" changes,
/// based on <see cref="ComposerState.IsTurnInFlight"/>.
///
/// The actual decision logic lives in <see cref="ComposerState"/> (a plain, Terminal.Gui-free
/// class) so it's directly unit-testable; this View is a thin adapter that reads Terminal.Gui
/// KeyDown events, drives ComposerState, and raises events the host (Program.cs) subscribes to.
/// </summary>
public sealed class Composer : TextView
{
    private readonly IApplication _app;
    private readonly ComposerState _state = new();
    private readonly MentionAutocomplete _mentionAutocomplete;
    private bool _syncing;

    public event Action<string>? Submitted;
    public event Action<string>? Steered;
    public event Action<string>? FollowedUp;
    public event Action? EmptyEnterHintRequested;

    /// <summary>Aborted turn's leftover text (may be empty) — the caller restores it, matching pi's abort() semantics.</summary>
    public event Action<string?>? Aborted;

    /// <summary>
    /// Raised when Ctrl+V found a real image on the OS clipboard (PNG bytes) — the host
    /// (Program.cs) adds it to pendingImages the same way an @mention'd image file would be.
    /// Terminal.Gui's own Ctrl+V handling (plain-text paste) still runs unmodified whenever the
    /// clipboard held no image, so text paste is never regressed on any platform.
    /// </summary>
    public event Action<byte[]>? ImagePasted;

    /// <summary>
    /// Raised the first time a Linux Ctrl+V falls through to text paste specifically because
    /// neither wl-paste nor xclip is installed (as opposed to falling through because the
    /// clipboard just held text) — per Claude Code issue #29204, silently falling through with no
    /// explanation is itself a known rough edge; this surfaces a one-line hint instead of staying
    /// silent about the missing dependency. Fires at most once per process (mirrors
    /// ClipboardImageReader.NoLinuxClipboardToolFound's own "surface it once" latch).
    /// </summary>
    public event Action? ImagePasteToolMissing;

    public Composer(IApplication app, Func<string> workingDirectoryProvider)
    {
        _app = app;
        Multiline = true;
        WordWrap = true;
        // Enter must reach our KeyDown handler as a submit/steer signal, not insert a newline —
        // multi-line composition isn't a feature this composer needs to support today.
        EnterKeyAddsLine = false;

        // A distinctly-colored border keeps the composer visually identifiable as "the user's
        // input area" even when Terminal.Gui's inline-mode driver mis-redraws on a console resize
        // and the transcript/composer boundary appears to merge (see TranscriptView.cs's header
        // comment) — the colored frame survives that redraw glitch where a plain text boundary
        // wouldn't.
        BorderStyle = LineStyle.Single;
        // Border.View is the lazily-created View backing the Border adornment (null until
        // something needs View-level functionality) — GetOrCreateView() forces it so SetScheme
        // (a View member, not exposed on the lightweight Border settings object itself) has
        // something to call.
        Border.GetOrCreateView().SetScheme(new Scheme(new GuiAttribute(ColorName16.BrightCyan, ColorName16.Black)));

        // Deliberately NOT setting Autocomplete.HostControl here (and not replacing the default
        // Autocomplete instance TextView's own field initializer already created either — its
        // OnSuperViewChanged override only self-wires HostControl "if (Autocomplete.HostControl
        // == null)"). Composer has no SuperView yet at construction time (LitosApp.Add(...) runs
        // afterward): PopupAutocomplete.HostControl's setter captures HostControl.SuperView into
        // a private _top field, and only if _top is non-null does it either create its internal
        // popup View immediately or subscribe to _top.Initialized to create it later. Setting
        // HostControl while SuperView is still null leaves that popup field permanently null —
        // any later attempt to show suggestions (typing '@' -> ProcessAutocomplete ->
        // RenderOverlay -> ProcessKey -> Visible = true, which sets popup.Visible under the hood)
        // then throws a NullReferenceException from inside Terminal.Gui's own draw pipeline.
        // Confirmed empirically. Same root cause and fix shape as PickerDialog.cs's HostControl
        // comment — leaving HostControl null here lets TextView.OnSuperViewChanged wire it up
        // itself once Composer actually gains a SuperView.
        _mentionAutocomplete = new MentionAutocomplete(workingDirectoryProvider);
        Autocomplete.SuggestionGenerator = _mentionAutocomplete;

        KeyDown += OnKeyDown;
        // NOT TextChanged: TextView's own doc comment on its Text property says TextChanged
        // "is fired whenever [the Text] property is set... Text is not set by TextView as the
        // user types" — confirmed empirically (a headless Terminal.Gui.Testing.InputInjector
        // repro showed TextChanged never firing while typing, leaving ComposerState's buffer
        // permanently empty so OnEnter always saw an empty buffer and no-op'd). ContentsChanged
        // is the event TextView actually raises for interactive edits (its own doc comment:
        // "Unlike the TextChanged event, this event is raised whenever the user types").
        ContentsChanged += (_, _) => SyncStateFromText();
    }

    /// <summary>Whether a turn is currently running — governs what Enter/Alt+Enter/Esc do.</summary>
    public bool IsTurnInFlight
    {
        get => _state.IsTurnInFlight;
        set => _state.IsTurnInFlight = value;
    }

    /// <summary>Restores previously-unsent text (e.g. after an Esc-aborted turn) into the composer.</summary>
    public void RestoreText(string? text)
    {
        _syncing = true;
        try
        {
            Text = text ?? string.Empty;
            _state.SetText(text ?? string.Empty);
            MoveEnd();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Clears the composer (used after a successful submit/steer/follow-up).</summary>
    public void ClearInput() => RestoreText(null);

    /// <summary>Forces the @-mention file index to rebuild on next use — call when the working directory changes (/new, /resume, /branch).</summary>
    public void InvalidateMentionCache() => _mentionAutocomplete.InvalidateCache();

    private void SyncStateFromText()
    {
        // TextView is the source of truth for the buffer text once the user is typing (it owns
        // real multi-line/wrap editing); ComposerState only needs to track the text/cursor for
        // FindActiveMention's mention-detection math, not re-implement editing itself.
        if (_syncing)
            return;

        _state.SetText(Text.ToString().TrimEnd('\n'));
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key == Key.V.WithCtrl)
        {
            // Always mark handled immediately: Terminal.Gui's own Ctrl+V (Command.Paste,
            // TextView.Paste()) must not also fire for the same keystroke — the async image
            // check below calls Paste() itself as the fallback once it knows the clipboard held
            // no image, rather than letting both paths race.
            key.Handled = true;
            _ = TryPasteImageAsync();
            return;
        }

        if (key == Key.Enter || key == Key.Enter.WithAlt)
        {
            var action = _state.OnEnter(altHeld: key.IsAlt);
            switch (action)
            {
                case ComposerAction.Submit:
                    key.Handled = true;
                    var submitText = _state.Text;
                    ClearInput();
                    Submitted?.Invoke(submitText);
                    return;

                case ComposerAction.Steer:
                    key.Handled = true;
                    var steerText = _state.Text;
                    ClearInput();
                    Steered?.Invoke(steerText);
                    return;

                case ComposerAction.FollowUp:
                    key.Handled = true;
                    var followUpText = _state.Text;
                    ClearInput();
                    FollowedUp?.Invoke(followUpText);
                    return;

                case ComposerAction.EmptyEnterHint:
                    key.Handled = true;
                    EmptyEnterHintRequested?.Invoke();
                    return;

                case ComposerAction.None:
                default:
                    // Idle + empty buffer: let Enter be a no-op rather than inserting a newline.
                    key.Handled = true;
                    return;
            }
        }

        if (key == Key.Esc)
        {
            // Always mark handled, even when idle: Esc is Terminal.Gui's default Command.Quit
            // binding (Application.GetDefaultKey(Command.Quit)), so leaving it unhandled here
            // lets an idle Esc fall through and quit the whole app instead of being the no-op
            // ComposerState.OnEscapeAbort() already signals (returns null) when no turn is running.
            key.Handled = true;

            var leftover = _state.OnEscapeAbort();
            if (IsTurnInFlight)
            {
                ClearInput();
                Aborted?.Invoke(leftover);
            }
        }
    }

    /// <summary>
    /// ClipboardImageReader's async platform read resumes on a thread-pool continuation, not
    /// Terminal.Gui's main UI thread (no SynchronizationContext is installed — same reasoning as
    /// every other async-dialog comment in this codebase), so both branches below marshal back
    /// via _app.Invoke before touching this View or raising ImagePasted.
    /// </summary>
    private async Task TryPasteImageAsync()
    {
        var toolMissingBefore = ClipboardImageReader.NoLinuxClipboardToolFound;
        var png = await ClipboardImageReader.TryReadPngAsync(CancellationToken.None);
        var toolMissingJustDetected = !toolMissingBefore && ClipboardImageReader.NoLinuxClipboardToolFound;

        _app.Invoke(() =>
        {
            if (png is not null)
            {
                ImagePasted?.Invoke(png);
                return;
            }

            // No image on the clipboard (or this platform/format isn't supported) — fall back to
            // Terminal.Gui's own normal text paste, so Ctrl+V never regresses to a no-op.
            Paste();
            if (toolMissingJustDetected)
                ImagePasteToolMissing?.Invoke();
        });
    }
}
