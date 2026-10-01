namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>
/// The read-only desktop questions and the cached <see cref="IVirtualDesktopManager"/> they ask
/// through, as an INSTANCE so the manager factory is injected rather than swapped in static state.
/// </summary>
/// <remarks>
/// Production uses one process-wide instance behind <see cref="Win32VirtualDesktopQueries"/>; tests
/// build their own with a fake factory. The first design swapped a static factory, and xUnit runs test
/// classes in parallel, so any other class reaching the static path (directly, or through
/// <c>Win32VirtualDesktopService.ResolveWindowDesktop</c> and the native window source) could observe
/// a fake or have its cached manager dropped mid-test. Removing the shared mutable seam is a smaller
/// guarantee to maintain than a collection that every present and future user of the static must
/// remember to join.
/// </remarks>
internal sealed class VirtualDesktopQueryClient(Func<IVirtualDesktopManager?> factory)
{
    private IVirtualDesktopManager? _manager;

    private IVirtualDesktopManager? Manager => _manager ??= factory();

    /// <summary>Creates the real shell manager, or <see langword="null"/> when the CLSID is absent.</summary>
    internal static IVirtualDesktopManager? CreateManager()
    {
        var type = Type.GetTypeFromCLSID(ShellComGuids.VirtualDesktopManager, throwOnError: false);
        return type is null ? null : Activator.CreateInstance(type) as IVirtualDesktopManager;
    }

    /// <summary>
    /// The desktop <paramref name="windowHandle"/> sits on. <see langword="false"/> with a reason
    /// when the shell will not say -- callers must treat that as "unknown", never as "the current
    /// one", or a window would be filed under the wrong desktop the moment the shell hesitates.
    /// </summary>
    public bool TryGetWindowDesktopId(nint windowHandle, out Guid desktopId, out string? error)
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
    public bool TryIsWindowOnCurrentDesktop(nint windowHandle, out bool onCurrentDesktop, out string? error)
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
    private bool Invoke(string operation, Func<IVirtualDesktopManager, int> call, out string? error)
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
}
