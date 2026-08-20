namespace CosmicWin.Interop.Win32;

/// <summary>
/// Info snapshot for one native monitor, as read by <see cref="INativeDisplaySource"/>.
/// <see cref="Scaling"/> is the already-resolved real scale factor (1.0 == 100%, 1.5 == 150%,
/// ...), not a raw DPI value — the DPI→scale conversion belongs to the native source, not to
/// <see cref="Win32DisplayManager"/>/<see cref="Win32Display"/>.
/// </summary>
internal readonly record struct NativeDisplayInfo(nint Handle, Rectangle Bounds, Rectangle WorkArea, double Scaling, bool IsPrimary);

/// <summary>
/// Abstracts the native Win32 primitives <see cref="Win32DisplayManager"/> depends on for WT-2
/// (PerMonitorV2 DPI-correct geometry), so display aggregation/primary-selection is unit-testable
/// without real monitors. <see cref="Win32NativeDisplaySource"/> is the real, CsWin32-backed
/// implementation; tests substitute a fake.
/// </summary>
internal interface INativeDisplaySource
{
    /// <summary>Enumerates the currently attached monitors.</summary>
    IReadOnlyList<NativeDisplayInfo> EnumerateDisplays();
}
