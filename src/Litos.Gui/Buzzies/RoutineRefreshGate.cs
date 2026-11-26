namespace Litos.Gui.Buzzies;

/// <summary>
/// Keeps the Routines window from rebuilding its cards while the user is still in the middle of a
/// gesture on one of them (Implement_Buzzies.md, Phase 1).
///
/// Rebuilding clears and re-creates every card, so a rebuild that lands while a button's Click
/// handler is still running destroys that button underneath the click. Found live on 2026-11-26:
/// pressing "Run now" raised <c>Changed</c> from inside the handler, and the rebuild it posted
/// removed the control the user was still pressing.
///
/// Holds nest, and the rebuild happens once, when the outermost hold ends — so a burst of changes
/// costs one rebuild rather than one each, and nothing is lost, because the rebuild at the end sees
/// whatever the action left behind.
/// </summary>
internal sealed class RoutineRefreshGate
{
    private int _holds;

    /// <summary>True while a card action is running.</summary>
    public bool IsHeld => _holds > 0;

    public void Hold() => _holds++;

    /// <summary>A change arrived. True means rebuild now; false means the action will rebuild.</summary>
    public bool Request() => _holds == 0;

    /// <summary>
    /// A hold ended. True means this was the outermost one, so the caller should rebuild once.
    /// A release with nothing held does nothing, so a stray call cannot cause a rebuild.
    /// </summary>
    public bool Release()
    {
        if (_holds == 0)
            return false;

        _holds--;
        return _holds == 0;
    }
}