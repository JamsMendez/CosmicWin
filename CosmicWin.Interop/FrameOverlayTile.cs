namespace CosmicWin.Interop;

/// <summary>A plain Interop-owned snapshot of an alert tile that can be drawn over a video frame.</summary>
/// <remarks>
/// Kept independent of App alert/layout types so application code can map its live-alert layout into
/// this narrow rendering DTO without making Interop depend on CosmicWin.App.
/// </remarks>
public readonly record struct FrameOverlayTile(
    Rectangle Bounds,
    FrameOverlayTileKind Kind = FrameOverlayTileKind.Warning,
    string? Label = null,
    DateTimeOffset StartedAt = default,
    TimeSpan Duration = default);

public enum FrameOverlayTileKind
{
    Warning,
    Failed,
}
