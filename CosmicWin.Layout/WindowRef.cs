namespace CosmicWin.Layout;

/// <summary>
/// Opaque identifier for a window tracked by the layout engine.
/// </summary>
/// <remarks>
/// <c>CosmicWin.Layout</c> has zero dependency on <c>CosmicWin.Interop</c> (design D1/D8) —
/// this wraps a native-handle-shaped value without referencing any Win32 or Interop type. The
/// App layer (WU7+) is responsible for mapping a real HWND to a <see cref="WindowRef"/> via
/// <c>CosmicWin.Interop</c>.
/// </remarks>
public readonly record struct WindowRef(nint Handle);
