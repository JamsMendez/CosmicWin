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

    public bool MoveWindowTo(nint windowHandle, Guid desktopId)
    {
        if (_documentedManager is not { } manager)
        {
            return false;
        }

        try
        {
            var hr = manager.MoveWindowToDesktop(windowHandle, ref desktopId);
            if (hr >= 0)
            {
                LastError = null;
                return true;
            }

            // 0x80070005 is the documented-in-practice refusal: IVirtualDesktopManager moves only
            // windows owned by the CALLING process. A window manager owns none of the windows it
            // manages, so this path can never succeed for real -- see the note on MoveViewToDesktop.
            LastError = hr == unchecked((int)0x80070005)
                ? "MoveWindowToDesktop: E_ACCESSDENIED -- the documented API only moves windows owned by the calling process."
                : $"MoveWindowToDesktop: HRESULT 0x{hr:X8}";
            return false;
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            LastError = $"MoveWindowToDesktop: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
            return false;
        }
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
