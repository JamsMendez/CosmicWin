using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

public sealed class FloatingNudgeTests
{
    private static readonly Rectangle Window = Rectangle.FromSize(100, 200, 300, 150);

    [Theory]
    [InlineData(Direction.Left, 90, 200)]
    [InlineData(Direction.Right, 110, 200)]
    [InlineData(Direction.Up, 100, 190)]
    [InlineData(Direction.Down, 100, 210)]
    public void MovesTheWindowOneStep_KeepingItsSize(Direction direction, int left, int top)
    {
        var moved = FloatingNudge.Apply(Window, direction);

        Assert.Equal(Rectangle.FromSize(left, top, 300, 150), moved);
    }

    [Fact]
    public void TheStepIsTenPixels() => Assert.Equal(10, FloatingNudge.Step);
}
