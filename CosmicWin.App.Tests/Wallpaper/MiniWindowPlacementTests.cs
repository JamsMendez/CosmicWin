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
    [InlineData(MiniCorner.TopLeft, MiniCorner.TopCenter)]
    [InlineData(MiniCorner.TopCenter, MiniCorner.TopRight)]
    [InlineData(MiniCorner.TopRight, MiniCorner.RightCenter)]
    [InlineData(MiniCorner.RightCenter, MiniCorner.BottomRight)]
    [InlineData(MiniCorner.BottomRight, MiniCorner.BottomCenter)]
    [InlineData(MiniCorner.BottomCenter, MiniCorner.BottomLeft)]
    [InlineData(MiniCorner.BottomLeft, MiniCorner.LeftCenter)]
    [InlineData(MiniCorner.LeftCenter, MiniCorner.TopLeft)]
    public void NextCyclesClockwise(MiniCorner from, MiniCorner expected)
    {
        Assert.Equal(expected, MiniWindowPlacement.Next(from));
    }

    [Fact]
    public void TheFullCycleVisitsAllEightPositionsOnceAndReturnsToTheStart()
    {
        var seen = new List<MiniCorner>();
        var current = MiniCorner.TopLeft;
        for (var i = 0; i < 8; i++)
        {
            seen.Add(current);
            current = MiniWindowPlacement.Next(current);
        }

        Assert.Equal(MiniCorner.TopLeft, current);
        Assert.Equal(8, seen.Distinct().Count());
    }

    [Theory]
    [InlineData(MiniCorner.TopCenter, 100 + (2560 - 288) / 2, 50)]
    [InlineData(MiniCorner.RightCenter, 100 + 2560 - 288, 50 + (1392 - 288) / 2)]
    [InlineData(MiniCorner.BottomCenter, 100 + (2560 - 288) / 2, 50 + 1392 - 288)]
    [InlineData(MiniCorner.LeftCenter, 100, 50 + (1392 - 288) / 2)]
    public void EachMidpointIsCenteredOnItsSideAndFlushWithThatEdge(MiniCorner corner, int x, int y)
    {
        var rect = MiniWindowPlacement.Compute(WorkArea, 1440, corner);

        Assert.Equal(new Rect(x, y, 288, 288), rect);
    }

    [Fact]
    public void ARightCenterStaysLeftOfATaskbarDockedAtTheRight()
    {
        var rightTaskbarWorkArea = new Rect(0, 0, 2560 - 64, 1440);

        var rect = MiniWindowPlacement.Compute(rightTaskbarWorkArea, 1440, MiniCorner.RightCenter);

        Assert.Equal(new Rect(2560 - 64 - 288, (1440 - 288) / 2, 288, 288), rect);
    }

    [Fact]
    public void ABottomCenterStopsAtABottomTaskbar()
    {
        var bottomTaskbarWorkArea = new Rect(0, 0, 2560, 1440 - 48);

        var rect = MiniWindowPlacement.Compute(bottomTaskbarWorkArea, 1440, MiniCorner.BottomCenter);

        Assert.Equal(new Rect((2560 - 288) / 2, 1440 - 48 - 288, 288, 288), rect);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void ANonPositiveMonitorHeightYieldsAnEmptyWindowAtTheCorner_NeverANegativeSide(int monitorHeight)
    {
        var rect = MiniWindowPlacement.Compute(WorkArea, monitorHeight, MiniCorner.BottomRight);

        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);
        Assert.Equal(WorkArea.X + WorkArea.Width, rect.X);
        Assert.Equal(WorkArea.Y + WorkArea.Height, rect.Y);
    }

    [Fact]
    public void AWorkAreaSmallerThanTheSideClampsTheSideToItsShorterDimension_StayingInside()
    {
        var tiny = new Rect(500, 300, 200, 150);

        var rect = MiniWindowPlacement.Compute(tiny, 1440, MiniCorner.BottomRight);

        Assert.Equal(new Rect(500 + 200 - 150, 300, 150, 150), rect);
    }

    [Fact]
    public void AnEmptyWorkAreaYieldsAnEmptyWindowAtItsOrigin()
    {
        var rect = MiniWindowPlacement.Compute(new Rect(10, 20, 0, 0), 1440, MiniCorner.TopRight);

        Assert.Equal(new Rect(10, 20, 0, 0), rect);
    }
}
