using CosmicWin.App;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Reported: windows at the screen edge had visible space on the left, right and bottom
/// but none at the top. The cause was measured in Interop -- Win32's invisible resize border is 7px
/// on three sides and 0 on the top -- and fixed there, so a tile now lands exactly where it is
/// asked. That left a choice, and the user chose a uniform, configurable gap over flush.
/// <para>
/// The arithmetic that makes it uniform: HALF the gap comes off the work area and HALF off every
/// tile. Adjacent windows then contribute half each, and an outer edge contributes the work-area
/// half plus the tile's half -- so both distances are exactly <see cref="TreeArranger.Gap"/>.
/// Taking a whole gap off each tile instead would leave the screen edge twice as wide as the space
/// between windows, which is the asymmetry this whole thread started with.
/// </para>
/// </summary>
public sealed class TreeArrangerGapTests
{
    [Fact]
    public void TwoTiles_LeaveTheSameGapAtEveryEdgeAndBetweenThem()
    {
        var left = new LeafNode(new WindowRef(1));
        var right = new LeafNode(new WindowRef(2));
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        foreach (var leaf in new[] { left, right })
        {
            leaf.Parent = root;
            root.Children.Add(leaf);
            root.Sizes.Add(500);
        }

        var engine = new LayoutTree(root);
        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(left.Window.Handle, Rectangle.Empty);
        var windowB = new RecordingWindow(right.Window.Handle, Rectangle.Empty);
        registry.Register(windowA, left);
        registry.Register(windowB, right);

        TreeArranger.ArrangeAndPosition(engine, registry, new Rect(0, 0, 1000, 600), gap: 8);

        var a = windowA.LastSetPosition!.Value;
        var b = windowB.LastSetPosition!.Value;

        Assert.Equal(8, a.Left);                 // left screen edge
        Assert.Equal(8, a.Top);                  // top screen edge -- the one that was missing
        Assert.Equal(600 - 8, a.Bottom);         // bottom screen edge
        Assert.Equal(8, b.Left - a.Right);       // between the two windows
        Assert.Equal(1000 - 8, b.Right);         // right screen edge
        Assert.Equal(a.Top, b.Top);
        Assert.Equal(a.Bottom, b.Bottom);
    }

    [Fact]
    public void GapOfZero_TilesEdgeToEdgeExactly()
    {
        var only = new LeafNode(new WindowRef(1));
        var engine = new LayoutTree(only);
        var registry = new WindowRegistry();
        var window = new RecordingWindow(only.Window.Handle, Rectangle.Empty);
        registry.Register(window, only);

        TreeArranger.ArrangeAndPosition(engine, registry, new Rect(0, 0, 1000, 600), gap: 0);

        Assert.Equal(Rectangle.FromSize(0, 0, 1000, 600), window.LastSetPosition);
    }
}
