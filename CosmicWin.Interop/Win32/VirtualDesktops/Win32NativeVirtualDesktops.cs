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
/// Moving a window deliberately does NOT go through that interface at all. It uses the documented
/// <see cref="IVirtualDesktopManager"/>, which takes an HWND and is a contract Microsoft supports.
/// </para>
/// </remarks>
internal sealed class Win32NativeVirtualDesktops : INativeVirtualDesktops
{
    private IVirtualDesktopManagerInternal? _internalManager;
    private IVirtualDesktopManager? _documentedManager;
    private bool? _available;

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
            // Caller detects the refusal by the set not growing, so there is nothing to report here.
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
                    return;
                }
            }
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
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
            return manager.MoveWindowToDesktop(windowHandle, ref desktopId) >= 0;
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
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
            if (shell.QueryService(ref service, ref iid, out var instance) < 0 || instance == IntPtr.Zero)
            {
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
            return false;
        }
    }

    private static bool IsInteropFailure(Exception ex) =>
        ex is COMException or InvalidCastException or NotSupportedException or ArgumentException;
}
