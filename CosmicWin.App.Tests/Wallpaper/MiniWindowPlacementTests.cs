using CosmicWin.App.Wallpaper;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests.Wallpaper;

public sealed class MiniWindowPlacementTests
{
    // A work area with a non-zero origin (second-monitor style) and a 48px taskbar taken off the bottom.
    private static readonly Rect WorkArea = new(100, 50, 2560, 1392);

    [Theory]
    [InlineData(1440, 288)]
    [InlineData(1080, 216)]
    [InlineData(1083, 216)]
    [InlineData(1079, 215)]
    public void TheSideIsAFifthOfTheMonitorHeight_RoundedDown(int monitorHeight, int side)
    {
        var rect = MiniWindowPlacement.Compute(WorkArea, monitorHeight, MiniCorner.BottomRight);

        Assert.Equal(side, rect.Width);
        Assert.Equal(side, rect.Height);
    }

    [Theory]
    [InlineData(MiniCorner.TopLeft, 100, 50)]
    [InlineData(MiniCorner.TopRight, 100 + 2560 - 288, 50)]
    [InlineData(MiniCorner.BottomLeft, 100, 50 + 1392 - 288)]
    [InlineData(MiniCorner.BottomRight, 100 + 2560 - 288, 50 + 1392 - 288)]
    public void EachCornerIsFlushWithTheWorkAreaEdges(MiniCorner corner, int x, int y)
    {
        var rect = MiniWindowPlacement.Compute(WorkArea, 1440, corner);

        Assert.Equal(new Rect(x, y, 288, 288), rect);
    }

    [Fact]
    public void ABottomCornerStopsAtTheTaskbar_NotAtTheMonitorEdge()
    {
        var rect = MiniWindowPlacement.Compute(WorkArea, 1440, MiniCorner.BottomLeft);

        Assert.Equal(WorkArea.Y + WorkArea.Height, rect.Y + rect.Height);
        Assert.True(rect.Y + rect.Height < 1440 + WorkArea.Y);
    }

    [Fact]
    public void ATopCornerStaysBelowATaskbarDockedAtTheTop()
    {
        // 2560x1440 monitor with a 48px taskbar docked at the top edge.
        var topTaskbarWorkArea = new Rect(0, 48, 2560, 1392);

        var rect = MiniWindowPlacement.Compute(topTaskbarWorkArea, 1440, MiniCorner.TopRight);

        Assert.Equal(new Rect(2560 - 288, 48, 288, 288), rect);
    }

    [Fact]
    public void ARightCornerStaysLeftOfATaskbarDockedAtTheRight()
    {
        // 2560x1440 monitor with a 64px taskbar docked at the right edge.
        var rightTaskbarWorkArea = new Rect(0, 0, 2560 - 64, 1440);

        var rect = MiniWindowPlacement.Compute(rightTaskbarWorkArea, 1440, MiniCorner.TopRight);

        Assert.Equal(new Rect(2560 - 64 - 288, 0, 288, 288), rect);
    }

    [Theory]
    [InlineData(MiniCorner.TopLeft, MiniCorner.TopRight)]
    [InlineData(MiniCorner.TopRight, MiniCorner.BottomRight)]
    [InlineData(MiniCorner.BottomRight, MiniCorner.BottomLeft)]
    [InlineData(MiniCorner.BottomLeft, MiniCorner.TopLeft)]
    public void NextCyclesClockwise(MiniCorner from, MiniCorner expected)
    {
        Assert.Equal(expected, MiniWindowPlacement.Next(from));
    }
}
