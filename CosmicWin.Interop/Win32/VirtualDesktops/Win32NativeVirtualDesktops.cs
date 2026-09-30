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
/// <para>
/// <b>The shell objects are proxies into <c>explorer.exe</c> and die with it.</b> After an Explorer
/// restart every call through the cached proxies fails with an RPC disconnect HRESULT (seen on
/// hardware: <c>0x800706BA</c>), which used to read as "zero desktops" until CosmicWin was
/// restarted. A call that fails that way now re-resolves the objects and is retried once; see
/// <see cref="ShellDisconnect"/>. The probe is deliberately NOT re-run: the same build's vtable does
/// not change across a restart.
/// </para>
/// </remarks>
internal sealed class Win32NativeVirtualDesktops : INativeVirtualDesktops
{
    /// <summary>The three shell objects one resolution yields. Replaced as a unit on reconnect.</summary>
    /// <param name="Internal">The undocumented desktop manager.</param>
    /// <param name="Documented">The documented manager, or <see langword="null"/> if it would not create.</param>
    /// <param name="Views">The view collection, or <see langword="null"/>; its absence disables moving only.</param>
    internal sealed record ShellManagers(
        IVirtualDesktopManagerInternal Internal,
        IVirtualDesktopManager? Documented,
        IApplicationViewCollection? Views);

    /// <summary>Resolves the shell objects afresh. Returns <see langword="null"/> with a reason on failure.</summary>
    internal delegate ShellManagers? ManagerResolver(out string? error);

    private readonly Func<bool> _probe;
    private readonly ManagerResolver _resolver;
    private ShellManagers? _managers;
    private bool? _available;

    public Win32NativeVirtualDesktops()
        : this(() => VirtualDesktopProbe.Run().Supported, ResolveFromShell)
    {
    }

    /// <param name="probe">
    /// The once-only vtable gate. Injected so tests need no live shell; production runs
    /// <see cref="VirtualDesktopProbe"/>. It is NEVER re-run on a reconnect: a restart of Explorer
    /// on the same build does not change the vtable.
    /// </param>
    /// <param name="resolver">How to obtain the shell objects, at first use and again on reconnect.</param>
    internal Win32NativeVirtualDesktops(Func<bool> probe, ManagerResolver resolver)
    {
        _probe = probe;
        _resolver = resolver;
    }

    /// <summary>Why the last call failed. Never thrown, always readable, cleared on success.</summary>
    public string? LastError { get; private set; }

    public bool IsAvailable => _available ??= _probe() && TryConnect();

    public IReadOnlyList<Guid> GetDesktopIds() =>
        TryInvoke<IReadOnlyList<Guid>>("GetDesktopIds", shell =>
        {
            shell.Internal.GetDesktops(out var desktops);
            desktops.GetCount(out var count);

            var ids = new List<Guid>(count);
            var iid = typeof(IVirtualDesktop).GUID;
            for (var index = 0; index < count; index++)
            {
                desktops.GetAt(index, ref iid, out var entry);
                ids.Add(((IVirtualDesktop)entry).GetId());
            }

            return ids;
        }, []);

    public Guid GetCurrentDesktopId() =>
        TryInvoke("GetCurrentDesktop", shell => shell.Internal.GetCurrentDesktop().GetId(), Guid.Empty);

    public void CreateDesktop() =>
        TryInvoke("CreateDesktop", shell =>
        {
            shell.Internal.CreateDesktop();
            return true;
        }, false);

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

    public void SwitchTo(Guid desktopId) =>
        TryInvoke("SwitchDesktop", shell =>
        {
            // Resolved through the enumeration rather than FindDesktop, which is still an unverified
            // slot holder -- one verified path is worth more than a shorter unverified one.
            var manager = shell.Internal;
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
                    return true;
                }
            }

            LastError = $"SwitchDesktop: {desktopId} was not in the enumeration of {count}.";
            return false;
        }, false);

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
        if (_managers is null)
        {
            LastError = "The shell's view collection was never resolved; moving windows is unavailable.";
            return false;
        }

        return TryInvoke("MoveViewToDesktop", shell =>
        {
            if (shell.Views is not { } views)
            {
                LastError = "The shell's view collection was never resolved; moving windows is unavailable.";
                return false;
            }

            var hr = views.GetViewForHwnd(windowHandle, out var view);

            // A dead shell answers through the return value here, not an exception: surface it as
            // one so the single reconnect path in TryInvoke sees both shapes.
            if (ShellDisconnect.IsDisconnect(hr))
            {
                throw new COMException($"GetViewForHwnd(0x{windowHandle:X})", hr);
            }

            if (hr < 0 || view is null)
            {
                LastError = $"GetViewForHwnd(0x{windowHandle:X}): HRESULT 0x{hr:X8}";
                return false;
            }

            if (!TryFindDesktop(shell.Internal, desktopId, out var desktop))
            {
                LastError = $"MoveViewToDesktop: {desktopId} was not in the enumeration.";
                return false;
            }

            shell.Internal.MoveViewToDesktop(view, desktop);
            LastError = null;
            return true;
        }, false);
    }

    /// <summary>
    /// Runs one shell call; on a disconnect-class failure drops the dead proxies, re-resolves them
    /// and retries THAT call once.
    /// </summary>
    /// <remarks>
    /// At most one reconnect and one retry per call: a shell that is still down must cost one failed
    /// resolve, not a loop. Only a disconnect HRESULT triggers it -- any other failure is a real
    /// answer from a live shell and is reported as before. Never throws; a failure that survives the
    /// retry is reported in <see cref="LastError"/> and says a reconnect was attempted.
    /// </remarks>
    private T TryInvoke<T>(string operation, Func<ShellManagers, T> call, T fallback)
    {
        if (_managers is not { } shell)
        {
            return fallback;
        }

        try
        {
            return call(shell);
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            if (!ShellDisconnect.IsDisconnect(ex.HResult))
            {
                LastError = $"{operation}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
                return fallback;
            }

            if (!TryConnect())
            {
                LastError = $"{operation}: {ex.GetType().Name} 0x{ex.HResult:X8}; reconnect failed: {LastError}";
                return fallback;
            }

            try
            {
                return call(_managers!);
            }
            catch (Exception retry) when (IsInteropFailure(retry))
            {
                LastError = $"{operation}: {retry.GetType().Name} 0x{retry.HResult:X8} {retry.Message} (after reconnect)";
                return fallback;
            }
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

    /// <summary>
    /// Swaps in freshly resolved managers. On failure the old ones are kept, so the next call that
    /// fails the same way tries again once Explorer is back.
    /// </summary>
    private bool TryConnect()
    {
        var resolved = _resolver(out var error);
        if (resolved is null)
        {
            LastError = error;
            return false;
        }

        _managers = resolved;
        return true;
    }

    private static ShellManagers? ResolveFromShell(out string? error)
    {
        error = null;
        try
        {
            var shellType = Type.GetTypeFromCLSID(ShellComGuids.ImmersiveShell, throwOnError: false);
            if (shellType is null || Activator.CreateInstance(shellType) is not IShellServiceProvider shell)
            {
                return null;
            }

            var service = ShellComGuids.VirtualDesktopManagerInternal;
            var iid = typeof(IVirtualDesktopManagerInternal).GUID;
            var hr = shell.QueryService(ref service, ref iid, out var instance);
            if (hr < 0 || instance == IntPtr.Zero)
            {
                error = $"QueryService(VirtualDesktopManagerInternal): 0x{hr:X8}";
                return null;
            }

            IVirtualDesktopManagerInternal internalManager;
            try
            {
                internalManager = (IVirtualDesktopManagerInternal)Marshal.GetObjectForIUnknown(instance);
            }
            finally
            {
                Marshal.Release(instance);
            }

            var documentedType = Type.GetTypeFromCLSID(ShellComGuids.VirtualDesktopManager, throwOnError: false);
            var documented = documentedType is null
                ? null
                : Activator.CreateInstance(documentedType) as IVirtualDesktopManager;

            // The view collection is a SERVICE on the same shell object, queried by its own
            // interface id. Its absence disables moving windows but leaves switching intact.
            IApplicationViewCollection? views = null;
            var viewsIid = typeof(IApplicationViewCollection).GUID;
            var viewsService = viewsIid;
            if (shell.QueryService(ref viewsService, ref viewsIid, out var viewsInstance) >= 0 && viewsInstance != IntPtr.Zero)
            {
                try
                {
                    views = (IApplicationViewCollection)Marshal.GetObjectForIUnknown(viewsInstance);
                }
                finally
                {
                    Marshal.Release(viewsInstance);
                }
            }

            return new ShellManagers(internalManager, documented, views);
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            error = $"resolve: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
            return null;
        }
    }

    private static bool IsInteropFailure(Exception ex) =>
        ex is COMException or InvalidCastException or NotSupportedException or ArgumentException;
}
