namespace CosmicWin.Layout;

/// <summary>
/// Plain, Win32-free snapshot of a window's identity and style bits (design D7), used by
/// <see cref="CosmicWin.Layout.Filters.WindowFilters"/> to decide whether a window should be
/// tracked/tiled (spec WE-1/WE-2). Carries raw style bitmasks rather than an HWND so exclusion
/// heuristics stay 100% unit-testable with zero Win32 dependency.
/// </summary>
public readonly record struct WindowDescriptor(
    string ClassName,
    string ProcessName,
    string Title,
    uint ExStyle,
    uint Style,
    bool IsOwned);
