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

    [Fact]
    public void FrameOverlayTile_CarriesAlertTimingWithBackwardsCompatibleDefaults()
    {
        var legacy = new FrameOverlayTile(new Rectangle(1, 2, 3, 4));
        var startedAt = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var timed = new FrameOverlayTile(
            new Rectangle(10, 20, 110, 70),
            FrameOverlayTileKind.Failed,
            "Disk",
            startedAt,
            TimeSpan.FromSeconds(5));

        Assert.Equal(default, legacy.StartedAt);
        Assert.Equal(default, legacy.Duration);
        Assert.Equal(startedAt, timed.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(5), timed.Duration);
    }

    [Fact]
    public void ComputeVisualState_WarningStartsRevealImmediatelyAndCountsEveryHundredMilliseconds()
    {
        var startedAt = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var tile = new FrameOverlayTile(
            new Rectangle(0, 0, 320, 180),
            FrameOverlayTileKind.Warning,
            StartedAt: startedAt,
            Duration: TimeSpan.FromSeconds(5));

        var state = Direct2DAlertOverlay.ComputeVisualStateForTests(tile, startedAt.AddMilliseconds(250));

        Assert.False(state.IsShaking);
        Assert.Equal(0, state.ShakeElapsedMilliseconds);
        Assert.Equal(250, state.RevealElapsedMilliseconds);
        Assert.Equal(0.25 / 0.7, state.RevealProgress, precision: 6);
        Assert.Equal(2, state.Counter);
        Assert.Equal(0.05, state.LifetimeProgress, precision: 6);
        Assert.False(state.PixelateBackdrop);
        Assert.Equal("WARNING", state.Title);
        Assert.Equal("01010111010000010101001001001110010010010100111001000111", state.Bits);
    }

    [Fact]
    public void ComputeVisualState_FailedExposesShakeBeforeRevealThenCountersFromRevealStart()
    {
        var startedAt = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var tile = new FrameOverlayTile(
            new Rectangle(0, 0, 320, 180),
            FrameOverlayTileKind.Failed,
            StartedAt: startedAt,
            Duration: TimeSpan.FromSeconds(5));

        var shaking = Direct2DAlertOverlay.ComputeVisualStateForTests(tile, startedAt.AddMilliseconds(100));
        var revealing = Direct2DAlertOverlay.ComputeVisualStateForTests(tile, startedAt.AddMilliseconds(480));

        Assert.True(shaking.IsShaking);
        Assert.Equal(100, shaking.ShakeElapsedMilliseconds);
        Assert.Equal(0, shaking.RevealElapsedMilliseconds);
        Assert.Equal(0, shaking.Counter);
        Assert.True(shaking.PixelateBackdrop);
        Assert.Equal("FAILED", shaking.Title);
        Assert.Equal("0100010101010010010100100100111101010010", shaking.Bits);

        Assert.False(revealing.IsShaking);
        Assert.Equal(230, revealing.ShakeElapsedMilliseconds);
        Assert.Equal(250, revealing.RevealElapsedMilliseconds);
        Assert.Equal(0.25 / 0.7, revealing.RevealProgress, precision: 6);
        Assert.Equal(2, revealing.Counter);
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
