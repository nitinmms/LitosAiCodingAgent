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
    }

    public void Stop()
    {
        if (!_spinner.AutoSpin)
            return;

        _spinner.AutoSpin = false;
        _spinner.Visible = false;
        _label.Visible = false;
        SetNeedsDraw();
    }
}
