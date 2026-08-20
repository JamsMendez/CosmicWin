using System.Linq;
using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// D3 <c>RemoveChild</c> (ported from cosmic-comp's removal counterpart to <c>Data::add_window</c>):
/// proportional redistribution of the removed child's size among the remaining siblings, with the
/// design D1 invariant that <c>Sizes.Sum() == GroupLength</c> always holds afterward. Mirrors
/// <c>AddChild</c>'s rounding convention, except the overflow remainder is absorbed by the LAST
/// remaining sibling rather than a newly inserted one (there is no "new" element on removal).
/// </summary>
public class LayoutTreeRemoveChildTests
{
    [Fact]
    public void RemoveChild_FromGroupWithTwoEqualChildren_GivesFullShareToRemainingChild()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var first = new LeafNode(new WindowRef(1));
        var second = new LeafNode(new WindowRef(2));
        group.Children.Add(first);
        group.Children.Add(second);
        group.Sizes.Add(400);
        group.Sizes.Add(400);

        LayoutTree.RemoveChild(group, index: 1);

        Assert.Single(group.Children);
        Assert.Same(first, group.Children[0]);
        Assert.Equal([800], group.Sizes);
    }

    [Fact]
    public void RemoveChild_ReturnsTheRemovedNode()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var first = new LeafNode(new WindowRef(1));
        var second = new LeafNode(new WindowRef(2));
        group.Children.Add(first);
        group.Children.Add(second);
        group.Sizes.Add(400);
        group.Sizes.Add(400);

        var removed = LayoutTree.RemoveChild(group, index: 1);

        Assert.Same(second, removed);
    }

    [Fact]
    public void RemoveChild_WithThreeUnequalSiblings_RedistributesProportionally()
    {
        // Removing the 400-wide middle child: remaining 300/300 siblings (ratio 1:1) should
        // absorb the freed 400 proportionally, preserving their relative ratio.
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        var first = new LeafNode(new WindowRef(1));
        var middle = new LeafNode(new WindowRef(2));
        var last = new LeafNode(new WindowRef(3));
        group.Children.Add(first);
        group.Children.Add(middle);
        group.Children.Add(last);
        group.Sizes.Add(300);
        group.Sizes.Add(400);
        group.Sizes.Add(300);

        LayoutTree.RemoveChild(group, index: 1);

        Assert.Equal(1000, group.Sizes.Sum());
        // Equal-ratio siblings (300:300 == 1:1) should stay equal after absorbing the freed size.
        Assert.Equal(group.Sizes[0], group.Sizes[1]);
    }

    [Fact]
    public void RemoveChild_LastRemainingSiblingAbsorbsRoundingOverflow()
    {
        // 4 children: three 100-wide siblings plus one 700-wide child being removed. Each
        // survivor's proportional share of the freed 700 is 700 * (100/300) = 233.33, which
        // rounds to 233 for the non-last survivors (466 distributed); the LAST remaining
        // sibling must absorb the leftover rounding remainder (700 - 466 = 234), landing on
        // 100 + 234 = 334 rather than the naively-rounded 100 + 233 = 333, so the sum still
        // hits GroupLength exactly.
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        var c = new LeafNode(new WindowRef(3));
        var removedChild = new LeafNode(new WindowRef(4));
        group.Children.Add(a);
        group.Children.Add(b);
        group.Children.Add(c);
        group.Children.Add(removedChild);
        group.Sizes.Add(100);
        group.Sizes.Add(100);
        group.Sizes.Add(100);
        group.Sizes.Add(700);

        LayoutTree.RemoveChild(group, index: 3);

        Assert.Equal(1000, group.Sizes.Sum());
        Assert.Equal(333, group.Sizes[0]);
        Assert.Equal(333, group.Sizes[1]);
        // Last remaining sibling (index 2, formerly `c`) absorbs the rounding overflow.
        Assert.Equal(334, group.Sizes[2]);
    }

    [Fact]
    public void RemoveChild_RemovesNodeAndSizeAtRequestedIndex()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        var first = new LeafNode(new WindowRef(1));
        var second = new LeafNode(new WindowRef(2));
        var third = new LeafNode(new WindowRef(3));
        group.Children.Add(first);
        group.Children.Add(second);
        group.Children.Add(third);
        group.Sizes.Add(300);
        group.Sizes.Add(300);
        group.Sizes.Add(300);

        LayoutTree.RemoveChild(group, index: 0);

        Assert.Equal(2, group.Children.Count);
        Assert.Same(second, group.Children[0]);
        Assert.Same(third, group.Children[1]);
    }

    [Fact]
    public void RemoveChild_OnlyRemainingChild_LeavesGroupEmpty()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var only = new LeafNode(new WindowRef(1));
        group.Children.Add(only);
        group.Sizes.Add(800);

        LayoutTree.RemoveChild(group, index: 0);

        Assert.Empty(group.Children);
        Assert.Empty(group.Sizes);
    }

    // Property test: Sizes.Sum() == GroupLength must hold after RemoveChild, regardless of the
    // starting distribution or which index is removed — including non-round GroupLength values
    // that stress rounding.
    [Theory]
    [InlineData(1000, new int[] { 600, 400 }, 0)]
    [InlineData(1000, new int[] { 600, 400 }, 1)]
    [InlineData(333, new int[] { 111, 111, 111 }, 2)]
    [InlineData(999, new int[] { 333, 333, 333 }, 0)]
    [InlineData(7, new int[] { 3, 2, 2 }, 1)]
    public void RemoveChild_SizesAlwaysSumToGroupLength(int groupLength, int[] existingSizes, int removeIndex)
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = groupLength };
        foreach (var size in existingSizes)
        {
            group.Children.Add(new LeafNode(new WindowRef(group.Children.Count + 1)));
            group.Sizes.Add(size);
        }

        LayoutTree.RemoveChild(group, index: removeIndex);

        Assert.Equal(groupLength, group.Sizes.Sum());
    }
}
