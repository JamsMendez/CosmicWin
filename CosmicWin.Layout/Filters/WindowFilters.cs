namespace CosmicWin.Layout.Filters;

/// <summary>
/// WE-1 automatic exclusion heuristics, evaluated against a Win32-free <see cref="WindowDescriptor"/>
/// Interop's own trackability check (<c>Win32NativeWindowSource.IsTrackable</c>) only
/// decides whether a window is trackable at all (visible, unowned); it deliberately defers all
/// fine-grained tiling heuristics to this type, so a visible, unowned shell window is NOT excluded
/// by Interop alone — callers MUST also apply
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

        var hasMaximizeBox = (descriptor.Style & WindowStyleFlags.MaximizeBox) != 0;
        var hasMinimizeBox = (descriptor.Style & WindowStyleFlags.MinimizeBox) != 0;

        // Reported with NVIDIA Broadcast, which was never tiled and cleared Interop's trackability
        // gate outright -- visible, unowned, uncloaked, not a child -- only to die on the clause
        // below. Measured: style 0x14C60000, no WS_SYSMENU, a lone minimize box, and a RESIZE
        // BORDER. An Electron app with its own chrome that simply cannot be maximized, so it brings
        // half the caption-box evidence the custom-frame escape asks for and was refused for the
        // missing half.
        //
        // WS_THICKFRAME is the evidence that was going unread, and it is the most direct there is:
        // the window declares that the user may resize it, which is the only permission a tiling
        // manager ever needs. Everything this clause protects against -- splash screens, toasts,
        // dropdowns, popups -- is fixed-size by construction.
        //
        // Measured rather than reasoned into place: enumerated against the whole live desktop,
        // thirty NVIDIA and overlay windows among them, this bit newly admits EXACTLY ONE window,
        // and it is the one that was missing. The GeForce Overlay is visible and unowned too and
        // stays out, on the WS_EX_TOOLWINDOW it carries and this window does not.
        var isResizable = (descriptor.Style & WindowStyleFlags.ThickFrame) != 0;

        if ((descriptor.Style & WindowStyleFlags.SystemMenu) == 0
            && !(hasMaximizeBox && hasMinimizeBox)
            && !isResizable)
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

        // A window with no AREA is not a tile, whatever its style bits say it is. Measured with
        // Windows 11 Notepad: it creates a second, visible, unowned, uncloaked top-level window of
        // class InputNonClientPointerSource at 0x0 -- the OS's own input plumbing for a custom
        // title bar -- which passed every test above and took a full share of the work area. One
        // Notepad, two tiles, and seventeen reflows fighting a window that snapped back to nothing
        // every time it was given a size.
        //
        // Stated as area rather than as that class name on purpose: every WinUI app with a custom
        // title bar has one of these, and naming them one at a time is a list that is always one
        // release behind. Nothing the user can see or click has zero area.
        //
        // TRANSIENT, exactly like WS_MINIMIZE above, so it depends on the same re-admission: a
        // window that later gains a size arrives back through the bounds-changed path.
        if (descriptor.Width <= 0 || descriptor.Height <= 0)
        {
            return true;
        }

        if (descriptor.IsOwned && !hasMaximizeBox && !hasMinimizeBox)
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
