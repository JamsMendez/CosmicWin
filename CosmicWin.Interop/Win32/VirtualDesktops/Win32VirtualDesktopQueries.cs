namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>
/// Read-only questions about where a window lives, answered through the DOCUMENTED
/// <see cref="IVirtualDesktopManager"/>.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="Win32VirtualDesktopService"/> because it answers a different kind of
/// question and carries a different risk. Reading a window's desktop needs no undocumented surface
/// at all — but "documented" was already shown not to mean "works cross-process" on this very
/// interface, since <c>MoveWindowToDesktop</c> refuses windows the caller does not own. So this is
/// measured before anything is built on it, not assumed.
/// </remarks>
internal static class Win32VirtualDesktopQueries
{
    private static IVirtualDesktopManager? _manager;

    private static IVirtualDesktopManager? Manager
    {
        get
        {
            if (_manager is not null)
            {
                return _manager;
            }

            var type = Type.GetTypeFromCLSID(ShellComGuids.VirtualDesktopManager, throwOnError: false);
            return _manager = type is null ? null : Activator.CreateInstance(type) as IVirtualDesktopManager;
        }
    }

    /// <summary>
    /// The desktop <paramref name="windowHandle"/> sits on. <see langword="false"/> with a reason
    /// when the shell will not say -- callers must treat that as "unknown", never as "the current
    /// one", or a window would be filed under the wrong desktop the moment the shell hesitates.
    /// </summary>
    public static bool TryGetWindowDesktopId(nint windowHandle, out Guid desktopId, out string? error)
    {
        desktopId = Guid.Empty;
        error = null;

        if (Manager is not { } manager)
        {
            error = "IVirtualDesktopManager could not be created.";
            return false;
        }

        try
        {
            var hr = manager.GetWindowDesktopId(windowHandle, out desktopId);
            if (hr < 0)
            {
                error = $"GetWindowDesktopId: HRESULT 0x{hr:X8}";
                return false;
            }

            // A window that is minimized or mid-creation can answer Guid.Empty rather than failing.
            if (desktopId == Guid.Empty)
            {
                error = "GetWindowDesktopId succeeded but reported an empty desktop id.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            error = $"GetWindowDesktopId: {ex.GetType().Name} 0x{ex.HResult:X8}";
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="windowHandle"/> is on the desktop being viewed right now.
    /// <see langword="false"/> with a reason when the shell will not say -- and "will not say" is
    /// not "no", which is exactly why the answer and the failure are separate outputs here.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT derived from <see cref="TryGetWindowDesktopId"/> and
    /// <see cref="GetCurrentDesktopId"/>. Comparing those two GUIDs would answer the same question
    /// through the UNDOCUMENTED internal manager, whose vtable this codebase re-verifies at runtime
    /// precisely because a Windows update can silently move a slot. <c>IVirtualDesktopManager</c>
    /// answers it directly, is documented, and -- unlike its <c>MoveWindowToDesktop</c> sibling,
    /// which returns <c>E_ACCESSDENIED</c> for windows the caller does not own -- reading works
    /// cross-process, which is the only mode a window manager ever operates in.
    /// </remarks>
    public static bool TryIsWindowOnCurrentDesktop(nint windowHandle, out bool onCurrentDesktop, out string? error)
    {
        onCurrentDesktop = false;
        error = null;

        if (Manager is not { } manager)
        {
            error = "IVirtualDesktopManager could not be created.";
            return false;
        }

        try
        {
            var hr = manager.IsWindowOnCurrentVirtualDesktop(windowHandle, out var onCurrent);
            if (hr < 0)
            {
                error = $"IsWindowOnCurrentVirtualDesktop: HRESULT 0x{hr:X8}";
                return false;
            }

            onCurrentDesktop = onCurrent != 0;
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            error = $"IsWindowOnCurrentVirtualDesktop: {ex.GetType().Name} 0x{ex.HResult:X8}";
            return false;
        }
    }

    /// <summary>The desktop the user is currently looking at, or <see cref="Guid.Empty"/> if unknown.</summary>
    public static Guid GetCurrentDesktopId() =>
        new Win32NativeVirtualDesktops() is { IsAvailable: true } native ? native.GetCurrentDesktopId() : Guid.Empty;
}
