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
    private readonly ComposerState _state = new();
    private bool _syncing;

    public event Action<string>? Submitted;
    public event Action<string>? Steered;
    public event Action<string>? FollowedUp;
    public event Action? EmptyEnterHintRequested;

    /// <summary>Aborted turn's leftover text (may be empty) — the caller restores it, matching pi's abort() semantics.</summary>
    public event Action<string?>? Aborted;

    public Composer(Func<string> workingDirectoryProvider)
    {
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
        Autocomplete.SuggestionGenerator = new MentionAutocomplete(workingDirectoryProvider);

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
}
