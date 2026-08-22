using System.Runtime.InteropServices;

namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>
/// Class identifiers for the shell objects that expose Windows' virtual desktops.
/// </summary>
/// <remarks>
/// <see cref="ImmersiveShell"/> is the running shell itself — nothing is shipped, referenced or
/// loaded from disk to reach it, which is why this capability adds no runtime dependency (design
/// D8, "no runtime third-party surface"). The service behind
/// <see cref="VirtualDesktopManagerInternal"/> is queried from that live object.
/// </remarks>
internal static class ShellComGuids
{
    public static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");

    public static readonly Guid VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    /// <summary>The DOCUMENTED manager, created directly with CoCreateInstance rather than queried.</summary>
    public static readonly Guid VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
}

/// <summary>
/// <c>IServiceProvider</c> (servprov.h) — documented, stable, and the only supported way to reach a
/// service hosted by the shell object.
/// </summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IShellServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid riid, out IntPtr instance);
}

/// <summary>
/// <c>IObjectArray</c> (shobjidl_core.h) — documented. The collection type
/// <c>IVirtualDesktopManagerInternal.GetDesktops</c> hands back.
/// </summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out int count);

    void GetAt(int index, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object instance);
}

/// <summary>
/// One virtual desktop, as the shell exposes it on Windows 11 build 26100 and later.
/// </summary>
/// <remarks>
/// <para>
/// UNDOCUMENTED. Microsoft publishes only <c>IVirtualDesktopManager</c>, whose three methods
/// (<c>GetWindowDesktopId</c>, <c>IsWindowOnCurrentVirtualDesktop</c>, <c>MoveWindowToDesktop</c>)
/// cannot create, switch or enumerate desktops — and whose own remarks say applications "should
/// avoid automatically switching the user from one virtual desktop to another". Everything here is
/// therefore a description of shell internals, not a contract Microsoft owes us.
/// </para>
/// <para>
/// <b>The declaration order below IS the vtable.</b> These are the slots the runtime dispatches
/// through, so a method inserted, removed or reordered by a Windows update does not fail to
/// compile and does not throw — it silently calls the WRONG function. In an elevated process that
/// is worse than a crash, which is why nothing may call through these interfaces until
/// <see cref="VirtualDesktopProbe"/> has cross-checked the layout at runtime.
/// </para>
/// <para>
/// Members we do not model are declared with <see cref="IntPtr"/> placeholders purely to hold
/// their slot. They must never be invoked: the signature is wrong on purpose, and only the
/// position is meaningful. String-returning members are placeholders for a second reason — they
/// return <c>HSTRING</c>, and <c>UnmanagedType.HString</c> is not supported on this runtime.
/// </para>
/// </remarks>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    /// <summary>Slot holder — takes an application view we do not model. Never call.</summary>
    [PreserveSig]
    int IsViewVisible(IntPtr view, out int visible);

    Guid GetId();

    /// <summary>Slot holder — returns HSTRING. Never call.</summary>
    [PreserveSig]
    int GetName(out IntPtr name);

    /// <summary>Slot holder — returns HSTRING. Never call.</summary>
    [PreserveSig]
    int GetWallpaperPath(out IntPtr path);

    /// <summary>Slot holder. Never call.</summary>
    [PreserveSig]
    int IsRemote(out int remote);
}

/// <summary>
/// The shell's internal desktop manager on Windows 11 build 26100 and later. See
/// <see cref="IVirtualDesktop"/> for why the ORDER of these members is the whole contract.
/// </summary>
/// <remarks>
/// Only the members CosmicWin actually calls carry real signatures: the read slots, plus
/// <c>SwitchDesktop</c> and <c>CreateDesktop</c>, which earned theirs once
/// <c>VirtualDesktopVTableTests</c> proved the layout holds across MORE THAN ONE desktop. Everything
/// else stays a slot holder with a deliberately wrong signature — present to hold its position,
/// impossible to invoke by accident. <c>RemoveDesktop</c> is among them on purpose: deleting a
/// desktop drags its surviving windows to a fallback, which is not a decision to make through an
/// interface Microsoft never promised.
/// </remarks>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();

    /// <summary>Slot holder. Never call — moving a window is not part of this spike.</summary>
    [PreserveSig]
    int MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);

    /// <summary>Slot holder. Never call.</summary>
    [PreserveSig]
    int CanViewMoveDesktops(IntPtr view, out int can);

    IVirtualDesktop GetCurrentDesktop();

    void GetDesktops(out IObjectArray desktops);

    /// <summary>Slot holder. Never call.</summary>
    [PreserveSig]
    int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);

    /// <summary>
    /// Verified against this machine by <c>VirtualDesktopVTableTests</c> before being given a real
    /// signature: with a second desktop present, slots 0/3/4 still corroborated one another.
    /// </summary>
    void SwitchDesktop(IVirtualDesktop desktop);

    /// <summary>Slot holder. Never call.</summary>
    [PreserveSig]
    int SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);

    /// <summary>Appends a desktop at the END of the list -- which is what makes the positional model natural.</summary>
    IVirtualDesktop CreateDesktop();

    /// <summary>Slot holder. Never call.</summary>
    [PreserveSig]
    int MoveDesktop(IVirtualDesktop desktop, int index);

    /// <summary>Slot holder. Never call.</summary>
    [PreserveSig]
    int RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);

    /// <summary>Slot holder. Never call.</summary>
    [PreserveSig]
    int FindDesktop(ref Guid desktopId, out IVirtualDesktop desktop);
}

/// <summary>
/// <c>IVirtualDesktopManager</c> (shobjidl_core.h) — DOCUMENTED and supported since Windows 10.
/// </summary>
/// <remarks>
/// Worth stating plainly: moving a window between desktops needs none of the undocumented surface
/// above. This interface takes an HWND directly, Microsoft documents it, and its shape is a
/// contract rather than an observation. The internal manager is required only to CREATE and SWITCH,
/// which this one deliberately cannot do -- its own remarks say applications "should avoid
/// automatically switching the user from one virtual desktop to another".
/// </remarks>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}
