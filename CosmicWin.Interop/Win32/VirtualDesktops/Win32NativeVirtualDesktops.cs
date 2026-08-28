using System.Runtime.InteropServices;

namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>
/// The real shell behind <see cref="INativeVirtualDesktops"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsAvailable"/> is the gate the whole design hangs on: <see cref="VirtualDesktopProbe"/>
/// runs ONCE, cross-checking three independent vtable slots against each other, and nothing here
/// calls through the undocumented interface until it has agreed. A build whose layout does not
/// match reports unavailable and every operation becomes a no-op -- CosmicWin loses virtual
/// desktops, which is vastly better than <c>SwitchDesktop</c> reaching some other function in an
/// elevated process.
/// </para>
/// <para>
/// <b>Moving a window through the documented <see cref="IVirtualDesktopManager"/> does not work for
/// a window manager, and the first live run proved it.</b> That interface moves only windows owned
/// by the CALLING process, returning <c>E_ACCESSDENIED</c> (0x80070005) otherwise — and a window
/// manager owns none of the windows it manages. An earlier note here claimed this half of the
/// feature needed no undocumented surface; that was wrong. The working path is
/// <c>MoveViewToDesktop</c> on the internal manager, which needs an <c>IApplicationView</c> resolved
/// from an HWND through <c>IApplicationViewCollection</c> — another undocumented interface, and
/// therefore another vtable to verify before it may be called.
/// </para>
/// </remarks>
internal sealed class Win32NativeVirtualDesktops : INativeVirtualDesktops
{
    private IVirtualDesktopManagerInternal? _internalManager;
    private IVirtualDesktopManager? _documentedManager;
    private IApplicationViewCollection? _views;
    private bool? _available;

    /// <summary>Why the last call failed. Never thrown, always readable, cleared on success.</summary>
    public string? LastError { get; private set; }

    public bool IsAvailable => _available ??= VirtualDesktopProbe.Run().Supported && TryResolveManagers();

    public IReadOnlyList<Guid> GetDesktopIds()
    {
        if (_internalManager is not { } manager)
        {
            return [];
        }

        try
        {
            manager.GetDesktops(out var desktops);
            desktops.GetCount(out var count);

            var ids = new List<Guid>(count);
            var iid = typeof(IVirtualDesktop).GUID;
            for (var index = 0; index < count; index++)
            {
                desktops.GetAt(index, ref iid, out var entry);
                ids.Add(((IVirtualDesktop)entry).GetId());
            }

            return ids;
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return [];
        }
    }

    public Guid GetCurrentDesktopId()
    {
        if (_internalManager is not { } manager)
        {
            return Guid.Empty;
        }

        try
        {
            return manager.GetCurrentDesktop().GetId();
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return Guid.Empty;
        }
    }

    public void CreateDesktop()
    {
        try
        {
            _internalManager?.CreateDesktop();
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            LastError = $"CreateDesktop: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
        }
    }

    /// <summary>
    /// Windows' own <c>Win+Ctrl+F4</c>, not the internal <c>RemoveDesktop</c> slot.
    /// </summary>
    /// <remarks>
    /// That slot is deliberately held at a wrong signature here and stays that way: it is unverified
    /// on this vtable, and deleting a desktop drags its surviving windows to a fallback the caller
    /// chooses -- a decision nobody should make through an interface Microsoft never promised. The
    /// documented shortcut has honoured the same meaning for a decade and cannot silently change
    /// under an update, which is the whole reason <see cref="ShellDesktopShortcuts"/> exists.
    /// <para>
    /// Costs a keystroke of real synthetic input on the user's desktop, and there is no cheaper
    /// honest way to do this. It is also fire-and-forget: nothing here can confirm the close, so
    /// nothing here pretends to.
    /// </para>
    /// </remarks>
    public void CloseCurrentDesktop()
    {
        LastError = null;
        ShellDesktopShortcuts.SendCloseDesktop();
    }

    public void SwitchTo(Guid desktopId)
    {
        if (_internalManager is not { } manager)
        {
            return;
        }

        try
        {
            // Resolved through the enumeration rather than FindDesktop, which is still an unverified
            // slot holder -- one verified path is worth more than a shorter unverified one.
            var iid = typeof(IVirtualDesktop).GUID;
            manager.GetDesktops(out var desktops);
            desktops.GetCount(out var count);

            for (var index = 0; index < count; index++)
            {
                desktops.GetAt(index, ref iid, out var entry);
                var desktop = (IVirtualDesktop)entry;
                if (desktop.GetId() == desktopId)
                {
                    manager.SwitchDesktop(desktop);
                    LastError = null;
                    return;
                }
            }

            LastError = $"SwitchDesktop: {desktopId} was not in the enumeration of {count}.";
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            LastError = $"SwitchDesktop: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
        }
    }

    /// <summary>
    /// Moves a window between desktops through the INTERNAL manager.
    /// </summary>
    /// <remarks>
    /// The documented <see cref="IVirtualDesktopManager.MoveWindowToDesktop"/> was tried first and
    /// measured returning <c>E_ACCESSDENIED</c>: it moves only windows owned by the calling
    /// process, and a window manager owns none of the windows it manages. Resolving the HWND to an
    /// application view and going through <c>MoveViewToDesktop</c> is the only path that works, at
    /// the cost of one more undocumented interface.
    /// </remarks>
    public bool MoveWindowTo(nint windowHandle, Guid desktopId)
    {
        if (_internalManager is not { } manager || _views is not { } views)
        {
            LastError = "The shell's view collection was never resolved; moving windows is unavailable.";
            return false;
        }

        try
        {
            var hr = views.GetViewForHwnd(windowHandle, out var view);
            if (hr < 0 || view is null)
            {
                LastError = $"GetViewForHwnd(0x{windowHandle:X}): HRESULT 0x{hr:X8}";
                return false;
            }

            if (!TryFindDesktop(manager, desktopId, out var desktop))
            {
                LastError = $"MoveViewToDesktop: {desktopId} was not in the enumeration.";
                return false;
            }

            manager.MoveViewToDesktop(view, desktop);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            LastError = $"MoveViewToDesktop: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Resolves a desktop id through the enumeration rather than <c>FindDesktop</c>, which is still
    /// an unverified slot holder. One verified path beats a shorter unverified one.
    /// </summary>
    private static bool TryFindDesktop(IVirtualDesktopManagerInternal manager, Guid desktopId, out IVirtualDesktop desktop)
    {
        desktop = null!;
        var iid = typeof(IVirtualDesktop).GUID;
        manager.GetDesktops(out var desktops);
        desktops.GetCount(out var count);

        for (var index = 0; index < count; index++)
        {
            desktops.GetAt(index, ref iid, out var entry);
            var candidate = (IVirtualDesktop)entry;
            if (candidate.GetId() == desktopId)
            {
                desktop = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryResolveManagers()
    {
        try
        {
            var shellType = Type.GetTypeFromCLSID(ShellComGuids.ImmersiveShell, throwOnError: false);
            if (shellType is null || Activator.CreateInstance(shellType) is not IShellServiceProvider shell)
            {
                return false;
            }

            var service = ShellComGuids.VirtualDesktopManagerInternal;
            var iid = typeof(IVirtualDesktopManagerInternal).GUID;
            var hr = shell.QueryService(ref service, ref iid, out var instance);
            if (hr < 0 || instance == IntPtr.Zero)
            {
                LastError = $"QueryService(VirtualDesktopManagerInternal): 0x{hr:X8}";
                return false;
            }

            try
            {
                _internalManager = (IVirtualDesktopManagerInternal)Marshal.GetObjectForIUnknown(instance);
            }
            finally
            {
                Marshal.Release(instance);
            }

            var documentedType = Type.GetTypeFromCLSID(ShellComGuids.VirtualDesktopManager, throwOnError: false);
            _documentedManager = documentedType is null
                ? null
                : Activator.CreateInstance(documentedType) as IVirtualDesktopManager;

            // The view collection is a SERVICE on the same shell object, queried by its own
            // interface id. Its absence disables moving windows but leaves switching intact.
            var viewsIid = typeof(IApplicationViewCollection).GUID;
            var viewsService = viewsIid;
            if (shell.QueryService(ref viewsService, ref viewsIid, out var viewsInstance) >= 0 && viewsInstance != IntPtr.Zero)
            {
                try
                {
                    _views = (IApplicationViewCollection)Marshal.GetObjectForIUnknown(viewsInstance);
                }
                finally
                {
                    Marshal.Release(viewsInstance);
                }
            }

            return _internalManager is not null;
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            LastError = $"resolve: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
            return false;
        }
    }

    private static bool IsInteropFailure(Exception ex) =>
        ex is COMException or InvalidCastException or NotSupportedException or ArgumentException;
}
