namespace CosmicWin.Layout;

/// <summary>
/// Plain, Win32-free snapshot of a window's identity and style bits, used by
/// <see cref="CosmicWin.Layout.Filters.WindowFilters"/> to decide whether a window should be
/// tracked/tiled. Carries raw style bitmasks rather than an HWND so exclusion
/// heuristics stay 100% unit-testable with zero Win32 dependency.
/// <para>
/// <see cref="Width"/>/<see cref="Height"/> are the drawn frame's size, carried for the same
/// reason the style bits are: a filter needs them and must not reach for an HWND to get them.
/// Like <c>WS_MINIMIZE</c> they are TRANSIENT, so a rule reading them has to be paired with
/// re-admission when they change.
/// </para>
/// </summary>
public readonly record struct WindowDescriptor(
    string ClassName,
    string ProcessName,
    string Title,
    uint ExStyle,
    uint Style,
    bool IsOwned,
    int Width,
    int Height);
