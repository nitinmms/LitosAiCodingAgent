using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// Spinner shown from turn-start until the first AgentEvent arrives — replaces
/// Rendering/TurnStatus.cs's manual timer + PinnedFooter cursor math. Terminal.Gui's real
/// SpinnerView owns its own tick loop (AutoSpin/SpinDelay), so there is no hand-rolled
/// Task.Delay polling loop here at all: starting/stopping the indicator is just toggling
/// Visible/AutoSpin. Uses SpinnerStyle.Dots (the same Braille frame set — ⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏ — the
/// old TurnStatus used) so the visual doesn't change, only how it's driven.
/// </summary>
public sealed class WorkingIndicator : View
{
    private readonly SpinnerView _spinner = new()
    {
        Style = new SpinnerStyle.Dots(),
        AutoSpin = false,
        Visible = false,
    };

    private readonly Label _label = new() { Text = " Working…" };

    public WorkingIndicator()
    {
        Height = 1;
        Width = Dim.Fill();
        CanFocus = false;

        _label.X = Pos.Right(_spinner);
        Add(_spinner, _label);
        _spinner.Visible = false;
        _label.Visible = false;
    }

    public bool IsRunning => _spinner.AutoSpin;

    public void Start()
    {
        if (_spinner.AutoSpin)
            return;

        _spinner.Visible = true;
        _label.Visible = true;
        _spinner.AutoSpin = true;
        SetNeedsDraw();
        RelayoutSiblingsDependingOnIsRunning();
    }

    public void Stop()
    {
        if (!_spinner.AutoSpin)
            return;

        _spinner.AutoSpin = false;
        _spinner.Visible = false;
        _label.Visible = false;
        SetNeedsDraw();
        RelayoutSiblingsDependingOnIsRunning();
    }

    /// <summary>
    /// LitosApp.cs sizes TranscriptView via Dim.Fill(Dim.Func(_ => Working.IsRunning ? 2 : 1)) —
    /// a computed Dim that reads IsRunning, evaluated only when ITS OWN view (Transcript) next
    /// relayouts. Flipping _spinner.AutoSpin above marks only THIS view (WorkingIndicator) dirty;
    /// it does nothing to invalidate Transcript's cached layout, so Transcript keeps using its
    /// stale pre-toggle height until something unrelated forces a full window relayout later.
    /// Confirmed empirically: after a turn's final MessageCompleted commits the last reply line
    /// (TranscriptView.Refresh(), called from Program.cs's RunTurnAsync before this Stop() runs —
    /// a separate, later app.Invoke in the `finally` block), that last line lands in the row the
    /// Working indicator was still occupying at commit time; the row is only reclaimed by
    /// Transcript AFTER this Stop() call flips IsRunning, one full redraw cycle too late — so the
    /// last line silently sits off-screen until some unrelated event (the next keystroke, e.g.)
    /// forces a fresh layout pass that finally re-evaluates Transcript's Dim.Func with the correct
    /// IsRunning value. SuperView.SetNeedsLayout()+SetNeedsDraw() forces exactly that immediately,
    /// rather than waiting for happenstance. Guarded on SuperView being non-null since Start()/
    /// Stop() can theoretically run before this view is ever Added (not reachable in practice —
    /// Program.cs's RunInteractive always Adds before starting the first turn — but a null check
    /// costs nothing and avoids a hard dependency on that ordering).
    /// </summary>
    private void RelayoutSiblingsDependingOnIsRunning()
    {
        if (SuperView is not { } superView)
            return;

        superView.SetNeedsLayout();
        superView.SetNeedsDraw();
    }
}
