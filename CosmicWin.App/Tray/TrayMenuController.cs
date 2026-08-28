namespace CosmicWin.App.Tray;

/// <summary>
/// Pure, Win32-free tray menu behavior. Mirrors the
/// <c>Func&lt;Rect&gt; workArea</c>/<c>Func&lt;ExceptionList&gt; exceptions</c> single-source-of-truth
/// idiom already established by <see cref="WorkspaceSessionAdapter"/> -- the controller owns no
/// state of its own beyond what the injected delegates report, so it is fully unit-testable with
/// local closures, no fake tray and no live desktop (see <see cref="TrayIconHost"/> for the thin
/// WinForms wrapper that owns the actual notification-area surface).
/// </summary>
public sealed class TrayMenuController(
    Func<bool> getPaused, Action<bool> setPaused,
    Func<bool> getFocusBorder, Action<bool> setFocusBorder,
    Action reload, Action exit,
    Func<uint?>? getBorderColor = null, Action<uint?>? setBorderColor = null)
{
    /// <summary>Spec TC-2: reflects the injected getter directly -- no internal state of its own.</summary>
    public bool IsPaused => getPaused();

    /// <summary>
    /// Whether CosmicWin draws its own focus border. Read through the injected getter, never
    /// remembered, because the setting is persisted to disk and something other than this menu can
    /// change it.
    /// </summary>
    public bool IsFocusBorderEnabled => getFocusBorder();

    /// <summary>Spec TC-2 (Pausar/Reanudar): flips the paused flag and returns the new state.</summary>
    public bool TogglePause()
    {
        var next = !getPaused();
        setPaused(next);
        return next;
    }

    /// <summary>
    /// Turns CosmicWin's own focus border on or off, returning the new state so the caller can
    /// render the tick beside it. Off leaves the window with the thin border DWM draws itself.
    /// </summary>
    public bool ToggleFocusBorder()
    {
        var next = !getFocusBorder();
        setFocusBorder(next);
        return next;
    }

    /// <summary>
    /// The colour that border is drawn in as <c>0xRRGGBB</c>, or <see langword="null"/> for
    /// Windows' own accent. Read through the getter for the same reason the toggle above is: it is
    /// persisted, and the settings file can be edited by hand while the menu is closed.
    /// </summary>
    /// <remarks>
    /// Answers the accent when no getter was wired. The delegates are optional because a tray built
    /// before the colour existed passes six arguments, and a menu click must never throw.
    /// </remarks>
    public uint? BorderColor => getBorderColor?.Invoke();

    /// <summary>
    /// Repaints the border in <paramref name="rgb"/>, or hands it back to the system accent with
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Forwarded, never interpreted. This class does not know what a colour looks like on screen,
    /// and a controller that started deciding which ones are allowed would be a second opinion
    /// nobody asked for -- the picker already showed the user exactly what they chose.
    /// </remarks>
    public void SetBorderColor(uint? rgb) => setBorderColor?.Invoke(rgb);

    /// <summary>WE-3: re-invokes the injected reload trigger.</summary>
    public void Reload() => reload();

    /// <summary>Spec TC-3 (Salir): invokes the injected exit trigger.</summary>
    public void Exit() => exit();
}
