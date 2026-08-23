namespace CosmicWin.Interop;

/// <summary>
/// Represents a physical monitor attached to the system. Shaped after the reference implementation's
/// <c>IDisplay</c>, trimmed to what
/// WT-2 (DPI-correct geometry) and multi-monitor-tiling (MM-1..5, later work units) require.
/// </summary>
public interface IDisplay : IEquatable<IDisplay>
{
    /// <summary>The native monitor handle (<c>HMONITOR</c> for the Win32 implementation).</summary>
    IntPtr Handle { get; }

    /// <summary>The display's full bounds, in real (unscaled) pixel coordinates.</summary>
    Rectangle Bounds { get; }

    /// <summary>The usable work area (bounds minus taskbar/reserved regions).</summary>
    Rectangle WorkArea { get; }

    /// <summary>The display's DPI scaling factor (1.0 == 100%, 1.5 == 150%, ...).</summary>
    double Scaling { get; }

    /// <summary>Whether this is the system's primary display.</summary>
    bool IsPrimary { get; }
}
