using System.Linq;
using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// D3 <c>AddChild</c>: equal-share initial
/// placement, proportional shrink of existing siblings, and the design's invariant that
/// <c>Sizes.Sum() == GroupLength</c> always holds afterward. Also covers LE-4's split-orientation
/// heuristic, used when a Leaf is first split into a Group.
/// </summary>
public class LayoutTreeAddChildTests
{
    [Fact]
    public void AddChild_ToEmptyGroup_GivesFullShareToTheOnlyChild()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        var child = new LeafNode(new WindowRef(1));

        LayoutTree.AddChild(group, child, index: 0);

        Assert.Single(group.Children);
        Assert.Same(child, group.Children[0]);
        Assert.Equal([800], group.Sizes);
    }

    [Fact]
    public void AddChild_ToGroupWithOneEqualChild_SplitsEqually()
    {
        // D3 "equal-share initial placement": the canonical first-split case — one existing
        // child occupying the full length, a second child added, both end up with equal shares.
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 800 };
        group.Children.Add(new LeafNode(new WindowRef(1)));
        group.Sizes.Add(800);

        LayoutTree.AddChild(group, new LeafNode(new WindowRef(2)), index: 1);

        Assert.Equal([400, 400], group.Sizes);
        Assert.Equal(800, group.Sizes.Sum());
    }

    [Fact]
    public void AddChild_ToGroupWithUnequalSizes_ShrinksExistingSiblingsProportionally()
    {
        // D3 "proportional-shrink of existing siblings": relative ratio between existing
        // siblings (600:400 == 3:2) must be preserved after shrinking to make room.
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1000 };
        group.Children.Add(new LeafNode(new WindowRef(1)));
        group.Children.Add(new LeafNode(new WindowRef(2)));
        group.Sizes.Add(600);
        group.Sizes.Add(400);

        LayoutTree.AddChild(group, new LeafNode(new WindowRef(3)), index: 2);

        Assert.Equal(1000, group.Sizes.Sum());
        // Ratio preserved (within rounding): 600/400 == 1.5, new first two sizes should too.
        Assert.Equal(1.5, group.Sizes[0] / (double)group.Sizes[1], precision: 1);
    }

    [Fact]
    public void AddChild_InsertsChildAndSizeAtRequestedIndex()
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        var first = new LeafNode(new WindowRef(1));
        var second = new LeafNode(new WindowRef(2));
        group.Children.Add(first);
        group.Sizes.Add(900);

        var inserted = new LeafNode(new WindowRef(3));
        LayoutTree.AddChild(group, inserted, index: 0);

        Assert.Same(inserted, group.Children[0]);
        Assert.Same(first, group.Children[1]);
    }

    // Property test: Sizes.Sum() == GroupLength must hold after AddChild, regardless of the
    // starting distribution — including a non-round GroupLength that stresses rounding.
    [Theory]
    [InlineData(800, new int[] { })]
    [InlineData(1000, new int[] { 600, 400 })]
    [InlineData(333, new int[] { 111, 111, 111 })]
    [InlineData(1, new int[] { })]
    [InlineData(999, new int[] { 333, 333, 333 })]
    public void AddChild_SizesAlwaysSumToGroupLength(int groupLength, int[] existingSizes)
    {
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = groupLength };
        foreach (var size in existingSizes)
        {
            group.Children.Add(new LeafNode(new WindowRef(group.Children.Count + 1)));
            group.Sizes.Add(size);
        }

        LayoutTree.AddChild(group, new LeafNode(new WindowRef(999)), index: group.Children.Count);

        Assert.Equal(groupLength, group.Sizes.Sum());
    }

    // LE-4 split-orientation heuristic, tested as a pure function first (no tree state needed).
    [Theory]
    [InlineData(1920, 1080, SplitAxis.Horizontal)] // wider than tall -> side by side
    [InlineData(1080, 1920, SplitAxis.Vertical)]   // taller than wide -> stacked
    [InlineData(1000, 1000, SplitAxis.Vertical)]   // square: height >= width -> stacked
    public void ChooseSplitAxis_UsesAspectRatioHeuristic(int width, int height, SplitAxis expected)
    {
        // NOTE (spec/design inconsistency discovered during implementation): LE-4's literal
        // text pairs "width > height -> Vertical split (side-by-side)". That labeling actually
        // matches the ORIGINAL (rejected) convention, in which the label Vertical
        // names the axis that measures WIDTH (i.e. is the side-by-side axis), while Horizontal
        // names the one that measures HEIGHT (i.e. is the "inverted" stacked axis the design says
        // CosmicWin rejects). Design D2 explicitly flips this so SplitAxis.Horizontal = children left->right
        // (side by side). Applying D2's own definition, the *behavior* LE-4 describes (wide ->
        // side-by-side, tall -> stacked) maps to Horizontal/Vertical the other way round from
        // LE-4's literal parenthetical labels. This test follows D2's definition (source of
        // truth for what the enum values mean) while preserving LE-4's behavioral intent (wide
        // regions split side by side). Flagged in apply-progress/return summary for spec
        // reconciliation — see the design notes.
        Assert.Equal(expected, LayoutTree.ChooseSplitAxis(width, height));
    }

    [Fact]
    public void AddChild_SplittingWideLeaf_ProducesSideBySideGroup()
    {
        // LE-4 scenario "Wide node splits vertically": a leaf wider than tall gets replaced by
        // a new group with the two windows side by side. Per the corrected D2 mapping (see note
        // above), "side by side" is SplitAxis.Horizontal.
        var existingLeaf = new LeafNode(new WindowRef(1));

        var group = LayoutTree.AddChild(existingLeaf, new WindowRef(2), regionWidth: 1920, regionHeight: 1080);

        Assert.Equal(SplitAxis.Horizontal, group.Axis);
        Assert.Equal(1920, group.GroupLength);
        Assert.Equal(2, group.Children.Count);
        Assert.Same(existingLeaf, group.Children[0]);
        Assert.Equal(new WindowRef(2), ((LeafNode)group.Children[1]).Window);
        Assert.Equal(1920, group.Sizes.Sum());
    }

    [Fact]
    public void AddChild_SplittingTallLeaf_ProducesStackedGroup()
    {
        var existingLeaf = new LeafNode(new WindowRef(1));

        var group = LayoutTree.AddChild(existingLeaf, new WindowRef(2), regionWidth: 1080, regionHeight: 1920);

        Assert.Equal(SplitAxis.Vertical, group.Axis);
        Assert.Equal(1920, group.GroupLength);
        Assert.Equal(1920, group.Sizes.Sum());
    }

    // Task 3.4 (MM-3 reparent) needs an overload that inserts an ALREADY-EXISTING Node instance
    // (e.g. a LeafNode already registered in App's WindowRegistry when a monitor disconnects) --
    // not the WindowRef overload above, which always builds a brand-new LeafNode and would orphan
    // the caller's existing registry mapping (a bug this rule exists to prevent).
    [Fact]
    public void AddChild_LeafWithExistingNode_PreservesNodeReference_ForCrossTreeReparenting()
    {
        var existingLeaf = new LeafNode(new WindowRef(1));
        var movedNode = new LeafNode(new WindowRef(2));

        var group = LayoutTree.AddChild(existingLeaf, movedNode, regionWidth: 1920, regionHeight: 1080);

        Assert.Same(movedNode, group.Children[1]);
        Assert.Same(group, movedNode.Parent);
        Assert.Equal(1920, group.Sizes.Sum());
    }

    [Fact]
    public void AddChild_WindowRefOverload_DelegatesToNodeOverload_SameGeometryBehavior()
    {
        // Approval-style triangulation: the pre-existing WindowRef overload must keep behaving
        // identically now that it delegates to the new Node overload (no regression).
        var existingLeaf = new LeafNode(new WindowRef(10));

        var group = LayoutTree.AddChild(existingLeaf, new WindowRef(20), regionWidth: 1000, regionHeight: 500);

        Assert.Equal(SplitAxis.Horizontal, group.Axis);
        var inserted = Assert.IsType<LeafNode>(group.Children[1]);
        Assert.Equal(new WindowRef(20), inserted.Window);
        Assert.Same(group, inserted.Parent);
    }
}
