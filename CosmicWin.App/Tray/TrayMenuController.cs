namespace CosmicWin.App.Tray;

/// <summary>
/// Tasks 3.15/3.16/3.17/3.18/3.36/3.37 (WU11): pure, Win32-free tray menu behavior. Mirrors the
/// <c>Func&lt;Rect&gt; workArea</c>/<c>Func&lt;ExceptionList&gt; exceptions</c> single-source-of-truth
/// idiom already established by <see cref="WorkspaceSessionAdapter"/> -- the controller owns no
/// state of its own beyond what the injected delegates report, so it is fully unit-testable with
/// local closures, no fake tray and no live desktop (see <see cref="TrayIconHost"/> for the thin
/// WinForms wrapper that owns the actual notification-area surface).
/// </summary>
public sealed class TrayMenuController(
    Func<bool> getPaused, Action<bool> setPaused, Action reload, Action exit)
{
    /// <summary>Spec TC-2: reflects the injected getter directly -- no internal state of its own.</summary>
    public bool IsPaused => getPaused();

    /// <summary>Spec TC-2 (Pausar/Reanudar): flips the paused flag and returns the new state.</summary>
    public bool TogglePause()
    {
        var next = !getPaused();
        setPaused(next);
        return next;
    }

    /// <summary>WE-3: re-invokes the injected reload trigger (closes verify-report #21 V10-W4).</summary>
    public void Reload() => reload();

    /// <summary>Spec TC-3 (Salir): invokes the injected exit trigger.</summary>
    public void Exit() => exit();
}
