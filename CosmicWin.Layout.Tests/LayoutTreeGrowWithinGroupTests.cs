using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// Giving one child more of its group's length and taking it from the others.
/// </summary>
/// <remarks>
/// <para>
/// The answer for a window that needs more than its share and has no branch to be given. Real trees
/// are BINARY -- a new window splits the focused tile in two -- so a constrained window usually has
/// exactly one sibling, and standing it beside "the others" is not available. What is available is
/// the share itself.
/// </para>
/// <para>
/// It takes exactly the shortfall and no more, so the damage is bounded by what the window actually
/// needs rather than by a policy. The donors are protected by the same floor ratio the resize chord
/// already uses, and a request that would push one under it is refused outright -- the tree is left
/// exactly as it was.
/// </para>
/// </remarks>
public class LayoutTreeGrowWithinGroupTests
{
    private static (GroupNode Group, LeafNode A, LeafNode B, LeafNode C) ThreeEqual()
    {
        var group = new GroupNode(SplitAxis.Vertical) { GroupLength = 900 };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        var c = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(group, a, index: 0);
        LayoutTree.AddChild(group, b, index: 1);
        LayoutTree.AddChild(group, c, index: 2);
        group.Sizes[0] = 300;
        group.Sizes[1] = 300;
        group.Sizes[2] = 300;
        return (group, a, b, c);
    }

    [Fact]
    public void Grow_TakesTheShortfallFromTheSiblings()
    {
        var (group, _, b, _) = ThreeEqual();

        Assert.True(LayoutTree.GrowWithinGroup(b, by: 120));

        Assert.Equal(420, group.Sizes[1]);
        Assert.Equal(900, group.Sizes.Sum());
    }

    /// <summary>
    /// Evenly, because nothing here knows which neighbour deserves to give more. The invariant is
    /// what matters: the group still adds up.
    /// </summary>
    [Fact]
    public void Grow_SpreadsTheCostAcrossEveryOtherChild()
    {
        var (group, _, b, _) = ThreeEqual();

        Assert.True(LayoutTree.GrowWithinGroup(b, by: 120));

        Assert.Equal(240, group.Sizes[0]);
        Assert.Equal(240, group.Sizes[2]);
    }

    /// <summary>
    /// A pair, which is the shape that actually occurs: one sibling pays the whole shortfall.
    /// </summary>
    [Fact]
    public void Grow_InAPair_TakesItAllFromTheOneSibling()
    {
        var group = new GroupNode(SplitAxis.Vertical) { GroupLength = 1376 };
        var a = new LeafNode(new WindowRef(1));
        var b = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(group, a, index: 0);
        LayoutTree.AddChild(group, b, index: 1);
        group.Sizes[0] = 688;
        group.Sizes[1] = 688;

        Assert.True(LayoutTree.GrowWithinGroup(b, by: 84));

        Assert.Equal(604, group.Sizes[0]);
        Assert.Equal(772, group.Sizes[1]);
    }

    /// <summary>
    /// A request that would push a donor under the floor ratio is refused OUTRIGHT, not clamped.
    /// </summary>
    /// <remarks>
    /// Clamping would hand back a half-answer the caller cannot use: it asked because a window has a
    /// size it will not go under, and most of the way there is the same as nowhere. Refusing leaves
    /// the tree untouched so the caller can fall back on something else.
    /// </remarks>
    [Fact]
    public void Grow_ThatWouldSquashADonorBelowTheFloorRatio_ChangesNothing()
    {
        var (group, _, b, _) = ThreeEqual();

        // 300 each, floor is 10% of 900 = 90, so the two donors can spare 210 each at most.
        Assert.False(LayoutTree.GrowWithinGroup(b, by: 500));

        Assert.Equal([300, 300, 300], group.Sizes);
    }

    [Fact]
    public void Grow_ByNothingOrLess_ChangesNothing()
    {
        var (group, _, b, _) = ThreeEqual();

        Assert.False(LayoutTree.GrowWithinGroup(b, by: 0));
        Assert.False(LayoutTree.GrowWithinGroup(b, by: -50));

        Assert.Equal([300, 300, 300], group.Sizes);
    }

    /// <summary>A node with no group has no siblings to take from.</summary>
    [Fact]
    public void Grow_ARootLeaf_ChangesNothing()
    {
        var only = new LeafNode(new WindowRef(1));

        Assert.False(LayoutTree.GrowWithinGroup(only, by: 100));
    }
}
