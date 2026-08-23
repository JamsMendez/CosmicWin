namespace CosmicWin.Layout.Filters;

/// <summary>
/// WE-1 automatic exclusion heuristics, evaluated against a Win32-free <see cref="WindowDescriptor"/>
/// Interop's own trackability check (<c>Win32NativeWindowSource.IsTrackable</c>) only
/// decides whether a window is trackable at all (visible, unowned); it deliberately defers all
/// fine-grained tiling heuristics to this type, so a visible, unowned, non-sysmenu window (e.g. a
/// shell tray window) is NOT excluded by Interop alone — callers MUST also apply
/// <see cref="IsAutoExcluded"/> (and, once wired, <see cref="IsExcluded"/>) before tiling a window.
/// </summary>
public static class WindowFilters
{
    /// <summary>Evaluates only the WE-1 automatic heuristics, ignoring the manual exception list.</summary>
    public static bool IsAutoExcluded(WindowDescriptor descriptor)
    {
        if ((descriptor.ExStyle & WindowStyleFlags.ExToolWindow) != 0)
        {
            return true;
        }

        if ((descriptor.Style & WindowStyleFlags.SystemMenu) == 0)
        {
            return true;
        }

        // A minimized window occupies no screen space, so giving it a tile hands a share of the
        // work area to something nothing is drawn into -- measured as "the terminal only
        // expanded to half the screen", with a minimized browser holding the other half.
        if ((descriptor.Style & WindowStyleFlags.Minimized) != 0)
        {
            return true;
        }

        var lacksMaximizeBox = (descriptor.Style & WindowStyleFlags.MaximizeBox) == 0;
        var lacksMinimizeBox = (descriptor.Style & WindowStyleFlags.MinimizeBox) == 0;
        if (descriptor.IsOwned && lacksMaximizeBox && lacksMinimizeBox)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// WE-1 automatic heuristics OR WE-2 manual exception-list match (manual exceptions apply
    /// "in addition to" WE-1, per spec — never narrower than the automatic set).
    /// </summary>
    public static bool IsExcluded(WindowDescriptor descriptor, ExceptionList exceptions)
        => IsAutoExcluded(descriptor) || exceptions.Matches(descriptor);

    /// <summary>
    /// A modal dialog: the window that is centred and left floating when it opens, rather than
    /// tiled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the OWNED branch of <see cref="IsAutoExcluded"/> read forwards. That branch already
    /// decided such a window must never be tiled; this one says what to do with it instead. The
    /// guards above it are repeated deliberately rather than inherited, because they are what keeps
    /// the predicate narrow: the event source that delivers a dialog also delivers every tooltip,
    /// dropdown, context menu and IME candidate list on the desktop, and centring one of those
    /// would yank it out from under the pointer that summoned it.
    /// </para>
    /// <para>
    /// <c>WS_SYSMENU</c> is what separates a dialog the user must answer from a transient popup: a
    /// dialog has a close button, a menu and a tooltip do not.
    /// </para>
    /// </remarks>
    public static bool IsModalDialog(WindowDescriptor descriptor) =>
        descriptor.IsOwned
        && (descriptor.ExStyle & WindowStyleFlags.ExToolWindow) == 0
        && (descriptor.Style & WindowStyleFlags.SystemMenu) != 0
        && (descriptor.Style & WindowStyleFlags.Minimized) == 0
        && (descriptor.Style & WindowStyleFlags.MaximizeBox) == 0
        && (descriptor.Style & WindowStyleFlags.MinimizeBox) == 0;
}
