using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// The move chord with tiling off: halves to the sides, and a top or bottom half that two windows
/// share as quarters.
/// </summary>
public sealed class FloatingSnapperTests
{
    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1000, 800);
    private static readonly Guid Desktop = Guid.NewGuid();

    private static RecordingWindow Window(int handle) =>
        new(new IntPtr(handle), Rectangle.FromSize(100, 100, 300, 200));

    private static void Snap(FloatingSnapper snapper, RecordingWindow window, Direction direction) =>
        snapper.Snap(window, WorkArea, direction, Desktop);

    [Theory]
    [InlineData(Direction.Left, 0, 0, 500, 800)]
    [InlineData(Direction.Right, 500, 0, 1000, 800)]
    [InlineData(Direction.Up, 0, 0, 1000, 400)]
    [InlineData(Direction.Down, 0, 400, 1000, 800)]
    public void ALoneWindowTakesHalfTheWorkArea(Direction direction, int left, int top, int right, int bottom)
    {
        var snapper = new FloatingSnapper(() => 0);
        var window = Window(1);

        Snap(snapper, window, direction);

        Assert.Equal(new Rectangle(left, top, right, bottom), window.Bounds);
    }

    [Fact]
    public void TheGapIsKeptAtTheEdgesAndBetweenTheHalves()
    {
        var snapper = new FloatingSnapper(() => 10);
        var left = Window(1);
        var right = Window(2);

        Snap(snapper, left, Direction.Left);
        Snap(snapper, right, Direction.Right);

        Assert.Equal(new Rectangle(10, 10, 495, 790), left.Bounds);
        Assert.Equal(new Rectangle(505, 10, 990, 790), right.Bounds);
    }

    [Theory]
    [InlineData(Direction.Up, 0, 400)]
    [InlineData(Direction.Down, 400, 800)]
    public void ASecondWindowSentToTheSameHalf_SplitsItIntoTwoQuarters(Direction direction, int top, int bottom)
    {
        var snapper = new FloatingSnapper(() => 0);
        var first = Window(1);
        var second = Window(2);

        Snap(snapper, first, direction);
        Snap(snapper, second, direction);

        Assert.Equal(new Rectangle(0, top, 500, bottom), first.Bounds);
        Assert.Equal(new Rectangle(500, top, 1000, bottom), second.Bounds);
    }

    [Fact]
    public void AThirdWindow_OverlapsTheRightQuarter_AndLeavesTheOtherTwoWhereTheyAre()
    {
        var snapper = new FloatingSnapper(() => 0);
        var first = Window(1);
        var second = Window(2);
        var third = Window(3);

        Snap(snapper, first, Direction.Up);
        Snap(snapper, second, Direction.Up);
        var firstMoves = first.SetPositionCallCount;
        var secondMoves = second.SetPositionCallCount;

        Snap(snapper, third, Direction.Up);

        Assert.Equal(new Rectangle(500, 0, 1000, 400), third.Bounds);
        Assert.Equal(firstMoves, first.SetPositionCallCount);
        Assert.Equal(secondMoves, second.SetPositionCallCount);
    }

    [Fact]
    public void TheTopAndTheBottomAreCountedApart()
    {
        var snapper = new FloatingSnapper(() => 0);
        var top = Window(1);
        var bottom = Window(2);

        Snap(snapper, top, Direction.Up);
        Snap(snapper, bottom, Direction.Down);

        Assert.Equal(new Rectangle(0, 0, 1000, 400), top.Bounds);
        Assert.Equal(new Rectangle(0, 400, 1000, 800), bottom.Bounds);
    }

    [Fact]
    public void AWindowMovedAwayFromTheTop_NoLongerOccupiesIt()
    {
        var snapper = new FloatingSnapper(() => 0);
        var first = Window(1);
        var second = Window(2);

        Snap(snapper, first, Direction.Up);
        first.SimulateExternalMove(Rectangle.FromSize(50, 500, 300, 200));
        Snap(snapper, second, Direction.Up);

        Assert.Equal(new Rectangle(0, 0, 1000, 400), second.Bounds);
        Assert.Equal(Rectangle.FromSize(50, 500, 300, 200), first.Bounds);
    }

    [Fact]
    public void AClosedWindow_NoLongerOccupiesTheTop()
    {
        var snapper = new FloatingSnapper(() => 0);
        var first = Window(1);
        var second = Window(2);

        Snap(snapper, first, Direction.Up);
        first.Kill();
        Snap(snapper, second, Direction.Up);

        Assert.Equal(new Rectangle(0, 0, 1000, 400), second.Bounds);
    }

    [Fact]
    public void AWindowOnAnotherDesktop_DoesNotOccupyThisOnesTop()
    {
        var snapper = new FloatingSnapper(() => 0);
        var elsewhere = Window(1);
        var here = Window(2);

        snapper.Snap(elsewhere, WorkArea, Direction.Up, Guid.NewGuid());
        Snap(snapper, here, Direction.Up);

        Assert.Equal(new Rectangle(0, 0, 1000, 400), here.Bounds);
        Assert.Equal(new Rectangle(0, 0, 1000, 400), elsewhere.Bounds);
    }

    [Fact]
    public void AWindowOnAnotherMonitor_DoesNotOccupyThisOnesTop()
    {
        var snapper = new FloatingSnapper(() => 0);
        var otherMonitor = Window(1);
        var here = Window(2);

        snapper.Snap(otherMonitor, Rectangle.FromSize(1000, 0, 1000, 800), Direction.Up, Desktop);
        Snap(snapper, here, Direction.Up);

        Assert.Equal(new Rectangle(0, 0, 1000, 400), here.Bounds);
    }

    [Fact]
    public void SendingTheOnlyTopWindowUpAgain_KeepsItOnTheWholeHalf()
    {
        var snapper = new FloatingSnapper(() => 0);
        var window = Window(1);

        Snap(snapper, window, Direction.Up);
        Snap(snapper, window, Direction.Up);

        Assert.Equal(new Rectangle(0, 0, 1000, 400), window.Bounds);
    }

    [Fact]
    public void SendingAQuarterUpAgain_KeepsBothQuartersWhereTheyAre()
    {
        var snapper = new FloatingSnapper(() => 0);
        var first = Window(1);
        var second = Window(2);

        Snap(snapper, first, Direction.Up);
        Snap(snapper, second, Direction.Up);
        Snap(snapper, first, Direction.Up);

        Assert.Equal(new Rectangle(0, 0, 500, 400), first.Bounds);
        Assert.Equal(new Rectangle(500, 0, 1000, 400), second.Bounds);
    }

    [Fact]
    public void AQuarterLeftAloneOnTheTop_IsJoinedByTheNextWindowInTheFreeQuarter()
    {
        var snapper = new FloatingSnapper(() => 0);
        var first = Window(1);
        var second = Window(2);
        var third = Window(3);

        Snap(snapper, first, Direction.Up);
        Snap(snapper, second, Direction.Up);
        Snap(snapper, first, Direction.Left);
        Snap(snapper, third, Direction.Up);

        Assert.Equal(new Rectangle(0, 0, 500, 400), third.Bounds);
        Assert.Equal(new Rectangle(500, 0, 1000, 400), second.Bounds);
    }
}
