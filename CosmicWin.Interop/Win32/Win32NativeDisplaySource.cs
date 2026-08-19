using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// Real, CsWin32-backed <see cref="INativeDisplaySource"/>: enumerates monitors via
/// <c>EnumDisplayMonitors</c>, reads geometry via <c>GetMonitorInfo</c>, and resolves the real
/// per-monitor scale factor via <c>GetDpiForMonitor(..., MDT_EFFECTIVE_DPI)</c> — the
/// PerMonitorV2-aware API, as opposed to a single system-wide DPI value.
/// </summary>
internal sealed unsafe class Win32NativeDisplaySource : INativeDisplaySource
{
    public IReadOnlyList<NativeDisplayInfo> EnumerateDisplays()
    {
        var handles = new List<nint>();

        PInvoke.EnumDisplayMonitors(
            HDC.Null,
            (RECT*)null,
            (hMonitor, _, _, _) =>
            {
                handles.Add(hMonitor);
                return true;
            },
            default(LPARAM));

        var infos = new List<NativeDisplayInfo>(handles.Count);
        foreach (var handle in handles)
        {
            if (TryGetDisplayInfo(handle, out var info))
            {
                infos.Add(info);
            }
        }

        return infos;
    }

    private static bool TryGetDisplayInfo(nint handle, out NativeDisplayInfo info)
    {
        HMONITOR hMonitor = new(handle);
        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);

        if (!PInvoke.GetMonitorInfo(hMonitor, ref mi))
        {
            info = default;
            return false;
        }

        double scaling = 1.0;
        if (PInvoke.GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded)
        {
            scaling = dpiX / 96.0;
        }

        bool isPrimary = (mi.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0;

        info = new NativeDisplayInfo(
            handle,
            new Rectangle(mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom),
            new Rectangle(mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom),
            scaling,
            isPrimary);
        return true;
    }
}
