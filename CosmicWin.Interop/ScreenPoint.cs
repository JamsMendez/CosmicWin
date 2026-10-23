namespace CosmicWin.Interop;

/// <summary>
/// A point in real (unscaled by DPI) screen-space pixel coordinates -- the same space
/// <see cref="Rectangle"/> is measured in.
/// </summary>
public readonly record struct ScreenPoint(int X, int Y);
