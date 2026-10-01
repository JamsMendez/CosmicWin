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
    private static Func<IVirtualDesktopManager?> _factory = CreateManager;
    private static IVirtualDesktopManager? _manager;

    private static IVirtualDesktopManager? Manager => _manager ??= _factory();

    private static IVirtualDesktopManager? CreateManager()
    {
        var type = Type.GetTypeFromCLSID(ShellComGuids.VirtualDesktopManager, throwOnError: false);
        return type is null ? null : Activator.CreateInstance(type) as IVirtualDesktopManager;
    }

    /// <summary>
    /// Replaces how the manager is created and drops the cached one. Tests only: it is the seam that
    /// lets a fake stand in for the shell, and passing <see langword="null"/> restores the real one.
    /// </summary>
    internal static void UseFactoryForTests(Func<IVirtualDesktopManager?>? factory)
    {
        _factory = factory ?? CreateManager;
        _manager = null;
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

        Guid found = Guid.Empty;
        if (!Invoke("GetWindowDesktopId", manager => manager.GetWindowDesktopId(windowHandle, out found), out error))
        {
            return false;
        }

        // A window that is minimized or mid-creation can answer Guid.Empty rather than failing.
        if (found == Guid.Empty)
        {
            error = "GetWindowDesktopId succeeded but reported an empty desktop id.";
            return false;
        }

        desktopId = found;
        return true;
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

        var onCurrent = 0;
        if (!Invoke("IsWindowOnCurrentVirtualDesktop", manager => manager.IsWindowOnCurrentVirtualDesktop(windowHandle, out onCurrent), out error))
        {
            return false;
        }

        onCurrentDesktop = onCurrent != 0;
        return true;
    }

    /// <summary>
    /// Runs one call against the cached manager; on a disconnect-class failure drops the dead proxy,
    /// creates a fresh one and retries THAT call once.
    /// </summary>
    /// <remarks>
    /// The manager is a proxy into <c>explorer.exe</c>, so an Explorer restart leaves it dead for
    /// good (see <see cref="ShellDisconnect"/>). The failure arrives either thrown or as the
    /// <c>hr &lt; 0</c> a <c>PreserveSig</c> method returns; both are handled here. At most one
    /// reconnect per call, and only for a disconnect -- any other HRESULT is a real answer from a
    /// live shell. Never throws: a failure that survives the retry says a reconnect was attempted.
    /// </remarks>
    private static bool Invoke(string operation, Func<IVirtualDesktopManager, int> call, out string? error)
    {
        error = null;
        for (var attempt = 0; ; attempt++)
        {
            if (Manager is not { } manager)
            {
                error = attempt == 0
                    ? "IVirtualDesktopManager could not be created."
                    : $"{operation}: reconnect failed, IVirtualDesktopManager could not be created.";
                return false;
            }

            int hr;
            string? thrown = null;
            try
            {
                hr = call(manager);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                hr = ex.HResult;
                thrown = $"{operation}: {ex.GetType().Name} 0x{hr:X8}";
            }

            if (thrown is null && hr >= 0)
            {
                return true;
            }

            if (attempt == 0 && ShellDisconnect.IsDisconnect(hr))
            {
                _manager = null;
                continue;
            }

            error = (thrown ?? $"{operation}: HRESULT 0x{hr:X8}") + (attempt > 0 ? " (after reconnect)" : string.Empty);
            return false;
        }
    }

    /// <summary>The desktop the user is currently looking at, or <see cref="Guid.Empty"/> if unknown.</summary>
    public static Guid GetCurrentDesktopId() =>
        new Win32NativeVirtualDesktops() is { IsAvailable: true } native ? native.GetCurrentDesktopId() : Guid.Empty;
}
