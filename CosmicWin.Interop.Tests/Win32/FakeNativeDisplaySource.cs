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

    public IReadOnlyList<NativeDisplayInfo> EnumerateDisplays() => _displays;
}
