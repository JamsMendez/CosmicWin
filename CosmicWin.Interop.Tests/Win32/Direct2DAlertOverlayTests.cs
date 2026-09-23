using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
using Windows.Win32.Foundation;

namespace CosmicWin.Interop.Tests.Win32;

public sealed class Direct2DAlertOverlayTests
{
    [Fact]
    public void Draw_WithNoTiles_ReturnsBeforeRequiringBackBuffer()
    {
        using var overlay = new Direct2DAlertOverlay();

        Exception? exception = CaptureException(() => ((IFrameOverlay)overlay).Draw(null!, default));

        Assert.Null(exception);
        Assert.Equal(0, overlay.TileCount);
    }

    [Fact]
    public void SetTiles_CopiesSnapshotAndClearReturnsToIdle()
    {
        using var overlay = new Direct2DAlertOverlay();
        var tiles = new[]
        {
            new FrameOverlayTile(new Rectangle(10, 20, 110, 70), FrameOverlayTileKind.Warning, "CPU"),
        };

        overlay.SetTiles(tiles);
        tiles[0] = new FrameOverlayTile(new Rectangle(0, 0, 1, 1), FrameOverlayTileKind.Failed, "Mutated");

        Assert.Equal(1, overlay.TileCount);

        overlay.Clear();

        Assert.Equal(0, overlay.TileCount);
        Assert.Null(CaptureException(() => ((IFrameOverlay)overlay).Draw(null!, new RECT())));
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
