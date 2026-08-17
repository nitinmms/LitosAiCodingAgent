using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// A generic "type to filter, pick one of N items" modal dialog, replacing Spectre.Console's
/// <c>SelectionPrompt&lt;T&gt;.EnableSearch()</c> (used by the old ModelPicker/SessionPicker).
///
/// Per ReadMe_AgentDesign.md §7.5/§7.6, this leans on Terminal.Gui's own BUILT-IN
/// type-to-filter component rather than hand-rolling keystroke-by-keystroke filtering logic:
/// a TextField with its real Autocomplete popup (TextFieldAutocomplete + a custom
/// ISuggestionGenerator) — the same mechanism MentionAutocomplete already uses for @-mentions.
/// Terminal.Gui 2.0.0-rc.64 does not ship a ComboBox or a "searchable ListView" component (both
/// were checked for and are absent from the installed assembly — see the migration report), so
/// TextField+Autocomplete is the closest built-in fit to the old EnableSearch() UX: typing
/// narrows a live suggestion popup, Up/Down/Tab/Enter (Autocomplete's own key handling) picks a
/// suggestion into the field. OK/Cancel buttons (or plain Enter with a fully-typed match)
/// confirm the pick.
/// </summary>
public sealed class PickerDialog<T> : Dialog<T> where T : notnull
{
    private readonly IReadOnlyList<T> _items;
    private readonly Func<T, string> _labelSelector;
    private readonly TextField _field = new() { Width = Dim.Fill() };

    public PickerDialog(string title, IReadOnlyList<T> items, Func<T, string> labelSelector, string? initialText = null)
    {
        Title = title;
        _items = items;
        _labelSelector = labelSelector;

        Width = Dim.Percent(70);
        // NOT a fixed Height = 5: Dialog<T>'s own Dim.Auto height already reserves rows for the
        // border and the button row (added below via AddButton), leaving Height=5 with a content
        // Viewport.Height of 0 -- _field lays out fine (Frame.Y=0, Height=1) but sits entirely
        // outside the dialog's own zero-height content Viewport, so it's laid out, focusable, and
        // fully functional (typed text/suggestions update correctly) yet never actually painted:
        // confirmed empirically (dialog.Viewport was {Width=53,Height=0} while _field.Frame was
        // {Y=0,Height=1}) -- the exact "dialog looks empty but selection still works" symptom.
        // Explicit Height leaves room for the field's own row plus several suggestion-popup rows
        // below it (PopupAutocomplete.MaxHeight defaults to 6), in addition to what Dialog<T>
        // reserves for borders/buttons.
        Height = 12;

        // Add _field to the dialog BEFORE wiring Autocomplete/setting Text. PopupAutocomplete's
        // HostControl setter (Terminal.Gui.Views.PopupAutocomplete, wrapping our
        // TextFieldAutocomplete) captures HostControl.SuperView into a private _top field and,
        // only if _top is non-null, either creates its popup View immediately (_top.IsInitialized)
        // or subscribes to _top.Initialized to create it later. If _field has no SuperView yet
        // (i.e. it hasn't been Added to the dialog), _top is captured as null and the popup is
        // NEVER created (no _top means no _top.Initialized subscription either) -- leaving
        // PopupAutocomplete's internal popup field permanently null. Any later attempt to show
        // suggestions (e.g. RenderOverlay -> ProcessKey -> Visible = true, which sets
        // popup.Visible under the hood) then throws a NullReferenceException from inside
        // Terminal.Gui's own draw pipeline. Setting Text (which can synchronously generate
        // suggestions and mark the popup visible) before Add() reproduced this exactly.
        Add(_field);

        var generator = new LabelSuggestionGenerator(() => _items.Select(labelSelector).ToList());
        _field.Autocomplete = new TextFieldAutocomplete
        {
            SuggestionGenerator = generator,
            HostControl = _field,
            // TextField's own constructor creates a default Autocomplete and, in its Initialized
            // handler, sets PopupInsideContainer = false -- but only when Autocomplete.HostControl
            // was still null at that point. Because we preset HostControl here (required for the
            // popup-null-crash fix above), that handler's guard (Autocomplete?.HostControl == null)
            // is already false by the time it runs, so it never flips PopupInsideContainer, and it
            // stays at PopupAutocomplete's own default of true. With PopupInsideContainer = true,
            // RenderOverlay clips/positions the popup within HostControl.Viewport itself -- but
            // _field is only 1 row tall (TextField's Height is Dim.Auto(DimAutoStyle.Text, 1)), so
            // the popup has nowhere to render but directly on/overlapping the field's own text row
            // (confirmed empirically: typed "model" rendered as "modeldelta-mode", the suggestion
            // text concatenated onto the same row instead of appearing below). Setting this
            // explicitly to false makes RenderOverlay use _top (the dialog)'s Viewport instead,
            // placing the popup below the field like TextField's own default usage.
            PopupInsideContainer = false,
            // AutocompleteBase.MaxWidth defaults to 10 columns. PopupAutocomplete.RenderOverlay
            // computes each row's width as Math.Min(MaxWidth, longest-suggestion-Title.Length) and
            // then TextFormatter.ClipOrPad()s every row's Title to that width -- so with the
            // default of 10, any suggestions sharing a common prefix longer than 10 chars (e.g.
            // OpenRouter's real "Z.ai: GLM-4.6" / "Z.ai: GLM-4.6 (free)" / "Z.ai: GLM-5 Turbo",
            // which all share "Z.ai: GLM-" as their first 10 chars) all render as the same clipped
            // "Z.ai: GLM " text -- confirmed empirically (5 real distinct GLM DisplayNames from
            // OpenRouter's live model list all rendered identically in the popup once truncated to
            // 10 chars). Widen it to fit the longest label actually being offered. The upper bound
            // tracks the dialog's own Width (Dim.Percent(70) of the screen) rather than a flat
            // constant: a flat 60-column cap silently clipped SessionPickerDialog's labels (session
            // id + timestamp + msg count alone run ~65 chars before the message preview even
            // starts), truncating every row right at "msgs)" with no visible ellipsis -- confirmed
            // empirically (a real /resume session list rendered with the preview text entirely
            // clipped off). GetContentSize().Width isn't available until the dialog is laid out, so
            // approximate from the same screen percentage Width uses, minus room for borders/padding.
            MaxWidth = Math.Max(10, items.Select(labelSelector).DefaultIfEmpty(string.Empty).Max(l => l.Length)),
        };
        _field.Text = initialText ?? string.Empty;
        _field.KeyDown += OnFieldKeyDown;

        var okButton = new Button { Text = "OK", IsDefault = true };
        okButton.Accepting += (_, _) => Accept();
        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Accepting += (_, _) => RequestStop();
        // Dialog<T>.AddButton unconditionally sets IsDefault = true on whichever button was added
        // MOST RECENTLY (and clears it on every earlier button) -- its own doc confirms "The last
        // button added becomes the default." Adding okButton first and cancelButton second (the
        // previous order here) silently made Cancel the real default button despite okButton's own
        // `IsDefault = true` initializer, regardless of which button visually looks focused. Since
        // Terminal.Gui routes any unhandled Enter (e.g. bubbling up from the field once Autocomplete
        // has nothing left to intercept -- see OnFieldKeyDown/LabelSuggestionGenerator below) to the
        // dialog's default button, that made a plain "type a full match, press Enter" flow silently
        // invoke Cancel instead of OK -- confirmed empirically via real key injection against the
        // Terminal.Gui 2.0.0-rc.64 assembly (Result stayed null even though the typed text matched an
        // item). Add cancelButton first so okButton, added last, is the one left as IsDefault.
        AddButton(cancelButton);
        AddButton(okButton);

        // Explicitly focus the field rather than relying on Terminal.Gui's default focus-advance
        // (which empirically does land on _field here, but AddButton's IsDefault/DefaultAcceptView
        // wiring makes the button row a plausible alternate target -- same defensive precedent as
        // Composer.SetFocus() in LitosApp.cs).
        _field.SetFocus();
    }

    private void OnFieldKeyDown(object? sender, Key key)
    {
        // Autocomplete's own ProcessKey (wired by TextField internally, and run via TextField's
        // OnKeyDown BEFORE this KeyDown event subscriber) consumes Enter to accept a highlighted
        // suggestion when the popup is open; when it's closed, Enter here confirms whatever text
        // is currently in the field.
        //
        // This depends on LabelSuggestionGenerator (below) never reporting Enter itself as a
        // "word char" (PopupAutocomplete.ProcessKey checks SuggestionGenerator.IsWordChar(key)
        // FIRST, before checking for the SelectionKey/Enter case -- a generator that answers
        // true for every grapheme, including Enter's, would make Autocomplete swallow Enter
        // as a "still typing" keystroke and never reach its own Select() branch, forcing Visible
        // to stay true and this guard to never fire at all) and on it not re-offering the
        // already-fully-typed label as a "new" suggestion (which would make PopupAutocomplete's
        // Select() re-insert the same text forever, keeping Suggestions non-empty and Visible
        // true, so Enter never bubbles here) -- confirmed empirically against the real
        // Terminal.Gui 2.0.0-rc.64 assembly.
        if (key == Key.Enter && !(_field.Autocomplete?.Visible ?? false))
        {
            key.Handled = true;
            Accept();
        }
    }

    private void Accept()
    {
        var typed = _field.Text?.ToString() ?? string.Empty;
        var match = _items.FirstOrDefault(i => string.Equals(_labelSelector(i), typed, StringComparison.OrdinalIgnoreCase))
            ?? _items.FirstOrDefault(i => _labelSelector(i).Contains(typed, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return;

        Result = match;
        RequestStop();
    }

    /// <summary>
    /// Runs the dialog modally and returns the picked item, or default(T) if canceled.
    ///
    /// Callers may invoke this from Terminal.Gui's own main UI thread (e.g. startup, before
    /// Application.Run begins) OR from a background thread-pool continuation resumed after an
    /// `await` inside a slash-command handler (AgentLoop/attachment/provider calls don't resume
    /// on the UI thread — there's no SynchronizationContext installed, same reasoning as
    /// ApprovalDialog's doc comment).
    ///
    /// On-thread callers (nothing else running yet, e.g. startup) just run the dialog inline via
    /// IApplication.Run -- there's no outer loop to conflict with.
    ///
    /// Off-thread callers (every slash-command invocation: /model, /provider, /resume all run
    /// while LitosApp's own IApplication.Run(mainToplevel) is already active on the main thread)
    /// must NOT call IApplication.Run(dialog, ...) from inside the IApplication.Invoke callback
    /// that bridges onto the UI thread. Empirically confirmed (standalone repro against the real
    /// Terminal.Gui 2.0.0-rc.64 assembly, with real key injection and rendered-buffer inspection):
    /// Run(dialog, ...) starts a SECOND, nested, blocking iteration loop on the same thread that's
    /// already inside the OUTER loop's own iteration (Invoke's queued callback runs from inside
    /// TimedEvents.RunTimers(), itself called from the outer loop's IterationImpl()). Once nested
    /// this way, EVERY subsequent IApplication.Invoke/AddTimeout call -- from ANY thread, including
    /// ones already queued before nesting began -- stops being processed for as long as the nested
    /// Run is active: the dialog draws its initial chrome once and then never redraws again, even
    /// though real keyboard input (which bypasses Invoke/TimedEvents, going through the normal
    /// input-processor/driver event pipeline instead) still reaches the TextField and updates its
    /// state correctly. That exactly matches the reported symptom: the dialog LOOKS frozen/empty
    /// while open, yet the picked result still reaches the caller correctly once it closes.
    ///
    /// The fix: don't call Run(dialog) at all for the off-thread path. Use IApplication.Begin(dialog)
    /// instead, which pushes the dialog onto the SAME session stack the already-running outer loop
    /// is driving -- IApplication.SessionStack's own doc confirms "all other sessions on the stack
    /// continue to be laid out, drawn, and receive iteration events" even though only the topmost
    /// session gets input. This lets the EXISTING outer loop's iterations draw the dialog and pump
    /// timers/Invoke normally; there's no second nested loop to break anything. Since bypassing Run
    /// means nobody's RunLoop is polling StopRequested/calling End for this dialog, we do that
    /// ourselves via the app's own Iteration event (confirmed via the same repro to fire on every
    /// outer-loop iteration) and detach once the dialog asks to stop.
    ///
    /// CAUTION (found while building AttachDialog, a sibling of this class): the "off-thread"
    /// framing above is really "app.MainThreadId != CurrentManagedThreadId", which is also FALSE
    /// for a slash-command handler that reaches this call with NO preceding `await` (e.g.
    /// /provider or /model with no argument — still on the thread that raised the originating key
    /// event). Confirmed empirically: /provider and /model's pickers still respond to
    /// selection/Enter/click fine when that mistakenly takes the on-thread Run(dialog, ...) branch
    /// below, but printLine calls issued right after Pick() returns silently stop reaching the
    /// transcript, and typed slash commands stop being recognized afterward — this class's
    /// original "off-thread" bug, just reached via a path this doc comment didn't anticipate.
    /// Switching this branch to check "is an outer Run loop already active" instead of thread
    /// identity (app.TopRunnableView is IRunnable { IsRunning: true }) was tried as the fix, but
    /// that makes EVERY slash-command Pick() call (mid-keydown, no preceding await) take the
    /// Begin+blocking-wait branch below — which deadlocks the entire app: Begin's Iteration event
    /// can only fire from inside Coordinator.RunIteration(), AFTER the current key-dispatch call
    /// (the one we'd be blocking synchronously inside) returns. Confirmed empirically (mouse/
    /// keyboard input stopped responding entirely). So: the on-thread branch's "silently loses
    /// printLine output afterward" bug is real and NOT YET FIXED, but is strictly less bad than
    /// the alternative (hard freeze) — reverted to the original MainThreadId-based dispatch until
    /// a genuinely non-blocking (not blocking-wait, not nested-Run) fix is found and verified for
    /// this call shape specifically. See AttachDialog.cs, which hits the identical problem for
    /// /attach and is mid-investigation of a fully async (non-blocking Task-returning) approach.
    ///
    /// SECOND CAUTION (Console Parity Plan hardening): the MainThreadId check has a genuine hole
    /// at STARTUP specifically, before any outer Run loop exists at all — confirmed via real
    /// diagnostic logging against Program.cs's own two startup pickers. PickProvider (no preceding
    /// `await` since Init) matches MainThreadId and correctly takes the on-thread branch. PickModel
    /// (called after `await chatProvider.ListModelsAsync(...)`) resumes on a DIFFERENT thread-pool
    /// thread — no SynchronizationContext is installed, so the continuation is not guaranteed to
    /// land back on the thread Init captured — and so incorrectly takes the off-thread Begin/
    /// Iteration/blocking-wait branch below. Unlike the mid-keydown slash-command case the FIRST
    /// caution above describes, there is no outer Run loop active yet at all here, so nothing can
    /// ever raise app.Iteration to satisfy OnIteration's StopRequested poll, or ever pump the
    /// app.Invoke(...) callback this whole branch is queued inside — a genuine, permanent deadlock
    /// (confirmed: the process didn't even respond to Ctrl+C, since the original calling thread is
    /// blocked forever inside tcs.Task.GetAwaiter().GetResult()). TopRunnableView is null before
    /// any Run/Begin call has ever happened (unlike the slash-command case in the FIRST caution,
    /// where a real outer loop genuinely is running), so gating specifically on "is anything
    /// running at all" — not "is an outer loop active from THIS call's perspective" — distinguishes
    /// the two cases without reintroducing the previously-tried fix's regression.
    /// </summary>
    public static T? Pick(IApplication app, string title, IReadOnlyList<T> items, Func<T, string> labelSelector, string? initialText = null)
    {
        // No session has ever been Run/Begin-started yet (TopRunnableView is null) — true only at
        // startup, before RunInteractive's outer Run(litosApp, ...) loop exists. In that specific
        // state, no outer loop can conflict with app.Run(dialog, null) regardless of which thread
        // physically resumed this call (see SECOND CAUTION above) — safe to always take the
        // on-thread branch here even if a post-await continuation landed on a different thread.
        var noOuterLoopRunningYet = app.TopRunnableView is null;
        if (app.MainThreadId == Environment.CurrentManagedThreadId || noOuterLoopRunningYet)
        {
            var dialog = new PickerDialog<T>(title, items, labelSelector, initialText);
            app.Run(dialog, null);
            return dialog.Result;
        }

        var tcs = new TaskCompletionSource<T?>();
        app.Invoke(() =>
        {
            try
            {
                var dialog = new PickerDialog<T>(title, items, labelSelector, initialText);
                var token = app.Begin(dialog);
                if (token is null)
                {
                    tcs.SetResult(default);
                    return;
                }

                void OnIteration(object? sender, EventArgs e)
                {
                    if (!dialog.StopRequested)
                        return;

                    app.Iteration -= OnIteration;
                    app.End(token);
                    tcs.SetResult(dialog.Result);
                }

                app.Iteration += OnIteration;
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>Suggests every label matching the field's current text as a whole-field replacement.</summary>
    private sealed class LabelSuggestionGenerator(Func<IReadOnlyList<string>> labelsProvider) : ISuggestionGenerator
    {
        public IEnumerable<Suggestion> GenerateSuggestions(AutocompleteContext context)
        {
            var typed = string.Concat(context.CurrentLine.Select(c => c.Grapheme));
            var labels = labelsProvider();

            if (string.IsNullOrEmpty(typed))
                return labels.Take(10).Select(label => new Suggestion(0, label, label));

            // Once the typed text already exactly matches a label (case-insensitively), there's
            // nothing left to complete -- continuing to offer that same label back as a
            // "suggestion" is what let PopupAutocomplete.Select()/InsertText() re-insert the
            // identical text and regenerate the identical single suggestion every time Enter was
            // pressed, keeping Suggestions non-empty and Visible permanently true so Enter never
            // reached OnFieldKeyDown/Accept() at all (confirmed empirically: real key injection
            // against the Terminal.Gui 2.0.0-rc.64 assembly hung indefinitely on repeated Enters
            // once this generator started reporting a real IsWordChar and Autocomplete began
            // actually invoking its own Select() for Enter). Suppressing suggestions once there's
            // an exact match lets the popup close so Enter reaches Accept() normally.
            if (labels.Any(l => string.Equals(l, typed, StringComparison.OrdinalIgnoreCase)))
                return [];

            var matches = labels
                .Where(l => l.Contains(typed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => !l.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .ThenBy(l => l.Length);

            return matches.Take(10).Select(label => new Suggestion(typed.Length, label, label));
        }

        // AutocompleteBase's real contract (see SingleWordSuggestionGenerator, Terminal.Gui's own
        // built-in implementation) is "is this rune part of a word" (Rune.IsLetterOrDigit), used
        // by PopupAutocomplete.ProcessKey to decide whether a keystroke continues typing (keep the
        // popup open) versus is a control/navigation key. Returning true unconditionally --
        // including for Key.Enter's own AsRune -- made ProcessKey misclassify Enter as "still
        // typing" on ITS OWN FIRST CHECK, before ever reaching the `key == SelectionKey` branch:
        // Enter got treated as a word char, Visible got forced back to true, and OnFieldKeyDown's
        // "popup closed" guard above then never fired for ANY Enter press, no matter how long the
        // user kept pressing it. Confirmed empirically (real key injection against the real
        // assembly): with this always returning true, typing a full match and pressing Enter
        // bubbled to the dialog's default button instead of ever reaching Accept().
        public bool IsWordChar(string grapheme) =>
            !string.IsNullOrEmpty(grapheme) && System.Text.Rune.IsLetterOrDigit(grapheme.EnumerateRunes().First());
    }
}
