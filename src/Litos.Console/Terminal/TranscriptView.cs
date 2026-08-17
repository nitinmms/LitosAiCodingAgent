using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// The scrolling transcript region, filling the available height above the pinned composer.
/// Replaces Rendering/StreamingRenderer.cs and Rendering/ToolCallPanel.cs's "running" state.
///
/// Implementation note versus ReadMe_AgentDesign.md §7.5's original phrasing: the design doc
/// describes completed turns being written directly to the terminal's native scrollback above a
/// Toplevel scoped to only the in-progress turn, with the live region "resetting" each turn.
/// Terminal.Gui 2.0.0-rc.64's public API (confirmed by inspecting the installed assembly, see
/// the migration report) does not expose a documented primitive for "commit this Toplevel's
/// current content to native scrollback and keep drawing below it" — AppModel.Inline grows the
/// driver's own Screen rectangle downward and scrolls it as content exceeds the terminal height,
/// but that growth/scroll is internal to the driver, not a seam a View can drive itself.
///
/// This view instead holds the FULL session transcript (every turn, not just the in-progress
/// one) inside one read-only, scrollable TextView, sized with Dim.Fill() above the composer.
/// AppModel.Inline's own growth-and-scroll behavior (verified via the driver's inline-mode
/// Screen/scrollback handling) then reproduces the same visual result the design doc wants —
/// finished turns scroll up and out of view exactly like native terminal scrollback — without
/// depending on an unexposed "commit to scrollback" API. Behaviorally equivalent; only the
/// mechanism differs from the original phrasing.
/// </summary>
public sealed class TranscriptView : TextView
{
    private readonly System.Text.StringBuilder _committed = new();
    private string _liveSuffix = string.Empty;

    public TranscriptView()
    {
        ReadOnly = true;
        Multiline = true;
        WordWrap = true;
        CanFocus = false;
        // Deliberately NOT setting ScrollBars = true: AppModel.Inline already grows/scrolls the
        // terminal's own native scrollback as content exceeds the screen height (this class's own
        // doc comment above), so TextView's own scrollbar duplicates it — two scrollbars side by
        // side for the same content, confirmed empirically to look redundant/confusing rather
        // than helpful.
    }

    /// <summary>Appends text that is final and will never change again (a completed turn, a tool line, an error, etc.).</summary>
    public void AppendCommitted(string text)
    {
        if (_committed.Length > 0 && _committed[^1] != '\n')
            _committed.Append('\n');
        _committed.Append(text);
        _liveSuffix = string.Empty;
        Refresh();
    }

    /// <summary>
    /// Replaces the in-progress (not-yet-committed) tail of the transcript — the current turn's
    /// streaming reply. Call repeatedly as TextDelta fragments accumulate; call
    /// <see cref="CommitLive"/> once the turn/tool-call line is done.
    /// </summary>
    public void SetLive(string text)
    {
        _liveSuffix = text;
        Refresh();
    }

    /// <summary>Moves the current live tail into committed, permanent transcript content.</summary>
    public void CommitLive()
    {
        if (_liveSuffix.Length == 0)
            return;
        AppendCommitted(_liveSuffix);
    }

    private void Refresh()
    {
        var full = _liveSuffix.Length == 0
            ? _committed.ToString()
            : _committed.ToString() + (_committed.Length > 0 ? "\n" : string.Empty) + _liveSuffix;

        Text = full;
        // WordWrap re-wraps the new Text during layout, not immediately on assignment — without
        // forcing layout first, MoveEnd() scrolls to the last line using the PREVIOUS wrap
        // geometry, landing one visual row short and leaving the newest line's tail clipped
        // below the viewport until the next redraw. Force layout so MoveEnd() sees current wrap.
        Layout();
        // TextView.DoNeededAction() (driven by MoveEnd() below) bounds its scroll target against
        // GetContentSize(), but that cached size is only refreshed by TextView's own private
        // UpdateContentSize() — called from OnSubViewsLaidOut (a full window-level relayout pass,
        // e.g. on terminal resize) but NOT from the plain Layout() call above, which only runs
        // SetRelativeLayout+LayoutSubViews on this view directly. Confirmed empirically: after a
        // reply finishes, the transcript's own last line was reachable only via a full terminal
        // resize (which goes through OnSubViewsLaidOut) — a mouse scroll away and back re-hid it,
        // ruling out a one-time missed-repaint and confirming GetContentSize() itself was stale
        // (one line short) until the resize path recomputed it. UpdateContentSize() itself is
        // private, so this mirrors its formula (Viewport.Width+1 x Lines, matching WordWrap's
        // "num + 1" content-size padding) via the public SetContentSize/Lines it wraps, keeping
        // content size in sync on every Refresh() instead of only on resize.
        SetContentSize(new System.Drawing.Size(Viewport.Width + 1, Lines));
        MoveEnd();
        // MoveEnd() only calls SetNeedsDraw() when its OWN pre-scroll Viewport bounds say the
        // target row/column falls outside them. Never depend on that conditional internal call —
        // request a redraw directly so the corrected scroll position always repaints.
        SetNeedsDraw();
    }
}
