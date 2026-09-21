using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// In-memory <see cref="INativeDisplaySource"/> used to test <see cref="Win32DisplayManager"/>'s
/// aggregation/selection logic (WT-2) without a real desktop session or physical monitors.
/// </summary>
internal sealed class FakeNativeDisplaySource : INativeDisplaySource
{
    private readonly List<NativeDisplayInfo> _displays = new();

    public FakeNativeDisplaySource AddDisplay(nint handle, Rectangle bounds, Rectangle workArea, double scaling, bool isPrimary)
    {
        _displays.Add(new NativeDisplayInfo(handle, bounds, workArea, scaling, isPrimary));
        return this;
    }

    /// <summary>Rewrites what the "OS" reports for one monitor from now on -- the taskbar moving, hiding, or being resized.</summary>
    public FakeNativeDisplaySource Change(nint handle, Func<NativeDisplayInfo, NativeDisplayInfo> change)
    {
        var index = _displays.FindIndex(d => d.Handle == handle);
        _displays[index] = change(_displays[index]);
        return this;
    }

    /// <summary>A monitor that stops being reported, as when it is unplugged mid-session.</summary>
    public FakeNativeDisplaySource Remove(nint handle)
    {
        _displays.RemoveAll(d => d.Handle == handle);
        return this;
    }

    public IReadOnlyList<NativeDisplayInfo> EnumerateDisplays() => _displays.ToList();
}
