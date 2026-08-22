using System.Runtime.InteropServices;

namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>What a <see cref="VirtualDesktopProbe"/> run learned about this machine's shell.</summary>
/// <param name="Supported">
/// <see langword="true"/> only when every check below agreed. Anything else means the vtable this
/// build ships does not match the declared layout, and NOTHING may be called through it.
/// </param>
/// <param name="Count">Desktops the shell reported, or -1 if the call never got that far.</param>
/// <param name="CurrentDesktopId">The desktop the user is on, or <see cref="Guid.Empty"/>.</param>
/// <param name="EnumeratedIds">Every desktop id read back from the enumeration.</param>
/// <param name="Failure">Why it stopped, or <see langword="null"/> when it did not.</param>
internal sealed record VirtualDesktopProbeResult(
    bool Supported,
    int Count,
    Guid CurrentDesktopId,
    IReadOnlyList<Guid> EnumeratedIds,
    string? Failure);

/// <summary>
/// Decides at RUNTIME whether this Windows build's virtual-desktop vtable matches the layout
/// declared in <see cref="IVirtualDesktopManagerInternal"/>, before anything is allowed to call
/// through it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of how the undocumented interface fails. Its declaration order IS the
/// vtable, so a Windows update that inserts or reorders a method produces no compile error and no
/// exception — every later call simply dispatches to a different function. Calling
/// <c>SwitchDesktop</c> and reaching something else, in a process running elevated, is a far worse
/// outcome than not supporting the feature at all.
/// </para>
/// <para>
/// The check is deliberately CROSS-cutting rather than a smoke test. Three separate vtable slots
/// have to agree with each other:
/// <list type="number">
/// <item><c>GetCount</c> (slot 0) must return a plausible desktop count;</item>
/// <item><c>GetDesktops</c> (slot 4) must yield exactly that many entries;</item>
/// <item><c>GetCurrentDesktop</c> (slot 3) must return an id that appears among them.</item>
/// </list>
/// A shifted vtable can accidentally satisfy any ONE of those; making three independent slots
/// corroborate one another is what turns a silent misdispatch into a loud refusal.
/// </para>
/// <para>
/// Read-only by construction: it never creates, removes, switches or moves anything. Every mutating
/// member of the interface is a slot holder with a deliberately wrong signature.
/// </para>
/// </remarks>
internal static class VirtualDesktopProbe
{
    /// <summary>More desktops than this means the count is garbage, not a power user.</summary>
    private const int ImplausibleDesktopCount = 64;

    public static VirtualDesktopProbeResult Run()
    {
        try
        {
            if (!TryGetManager(out var manager, out var failure))
            {
                return new VirtualDesktopProbeResult(false, -1, Guid.Empty, [], failure);
            }

            var count = manager.GetCount();
            if (count < 1 || count > ImplausibleDesktopCount)
            {
                return new VirtualDesktopProbeResult(
                    false, count, Guid.Empty, [],
                    $"GetCount returned {count}, which is not a plausible desktop count -- the vtable does not match.");
            }

            manager.GetDesktops(out var desktops);
            desktops.GetCount(out var enumeratedCount);

            var ids = new List<Guid>(enumeratedCount);
            var desktopIid = typeof(IVirtualDesktop).GUID;
            for (var index = 0; index < enumeratedCount; index++)
            {
                desktops.GetAt(index, ref desktopIid, out var entry);
                ids.Add(((IVirtualDesktop)entry).GetId());
            }

            var currentId = manager.GetCurrentDesktop().GetId();

            if (enumeratedCount != count)
            {
                return new VirtualDesktopProbeResult(
                    false, count, currentId, ids,
                    $"GetCount said {count} but GetDesktops yielded {enumeratedCount} -- two vtable slots disagree.");
            }

            if (!ids.Contains(currentId))
            {
                return new VirtualDesktopProbeResult(
                    false, count, currentId, ids,
                    "The current desktop's id is absent from the enumerated set -- GetCurrentDesktop and GetDesktops disagree.");
            }

            return new VirtualDesktopProbeResult(true, count, currentId, ids, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or ArgumentException)
        {
            // A refusal, not a crash: an unsupported build must degrade to "no virtual desktops"
            // rather than take the window manager down with it.
            return new VirtualDesktopProbeResult(false, -1, Guid.Empty, [], $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryGetManager(out IVirtualDesktopManagerInternal manager, out string? failure)
    {
        manager = null!;

        var shellType = Type.GetTypeFromCLSID(ShellComGuids.ImmersiveShell, throwOnError: false);
        if (shellType is null || Activator.CreateInstance(shellType) is not IShellServiceProvider shell)
        {
            failure = "Could not reach the immersive shell -- no virtual-desktop service to query.";
            return false;
        }

        var service = ShellComGuids.VirtualDesktopManagerInternal;
        var iid = typeof(IVirtualDesktopManagerInternal).GUID;
        var hr = shell.QueryService(ref service, ref iid, out var instance);
        if (hr < 0 || instance == IntPtr.Zero)
        {
            failure = $"QueryService for the internal desktop manager failed (0x{hr:X8}) -- this build does not expose the declared interface id.";
            return false;
        }

        try
        {
            manager = (IVirtualDesktopManagerInternal)Marshal.GetObjectForIUnknown(instance);
            failure = null;
            return true;
        }
        finally
        {
            Marshal.Release(instance);
        }
    }
}
