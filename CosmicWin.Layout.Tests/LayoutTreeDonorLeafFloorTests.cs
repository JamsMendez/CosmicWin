using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// The per-window floor <see cref="LayoutTree.TransferAcross"/> must respect: no resize, chord or
/// drag, may take a donor below <see cref="LayoutTree.DefaultMinLeafSlotLength"/> for any LEAF
/// inside it, not just the donor's own immediate share.
/// </summary>
/// <remarks>
/// <see cref="LayoutTree.DefaultMinRatio"/> alone only ever bounded the immediate sibling, which is
/// frequently a GROUP rather than a window. Measured: four windows on screen, two terminals sharing
/// a nested group, left one terminal at 150 px by a resize that stayed inside the 10% ratio the
/// whole time. Both the keyboard chord (<see cref="LayoutTree.ResizeNode"/>) and the mouse drag
/// (<see cref="LayoutTree.ApplyEdgeDrag"/>) funnel through the same private
/// <c>TransferAcross</c>, so both are exercised here.
/// </remarks>
public class LayoutTreeDonorLeafFloorTests
{
    private static GroupNode Group(SplitAxis axis, int length, params int[] sizes)
    {
        var group = new GroupNode(axis) { GroupLength = length };
        for (int index = 0; index < sizes.Length; index++)
        {
            var child = new LeafNode(new WindowRef(index + 1)) { Parent = group };
            group.Children.Add(child);
            group.Sizes.Add(sizes[index]);
        }

        return group;
    }

    /// <summary>
    /// Wires a nested group of two leaves in as the second child of a root group, keeping their
    /// sizes UNEVEN so a proportional floor (not the group's raw sum) is what actually binds.
    /// </summary>
    private static (GroupNode Root, GroupNode Donor) RootWithNestedDonor(
        SplitAxis axis, int rootLength, int focusedSize, int donorLength, int donorSmall, int donorLarge)
    {
        var root = Group(axis, rootLength, focusedSize, donorLength);
        var donor = new GroupNode(axis) { GroupLength = donorLength };
        var small = new LeafNode(new WindowRef(10)) { Parent = donor };
        var large = new LeafNode(new WindowRef(11)) { Parent = donor };
        donor.Children.Add(small);
        donor.Children.Add(large);
        donor.Sizes.Add(donorSmall);
        donor.Sizes.Add(donorLarge);
        donor.Parent = root;
        root.Children[1] = donor;
        return (root, donor);
    }

    /// <summary>A group of already-built children, each wired to it, with its length set to their sum.</summary>
    private static GroupNode GroupOf(SplitAxis axis, params (Node Child, int Size)[] slots)
    {
        var group = new GroupNode(axis);
        foreach (var (child, size) in slots)
        {
            child.Parent = group;
            group.Children.Add(child);
            group.Sizes.Add(size);
        }

        group.GroupLength = group.Sizes.Sum();
        return group;
    }

    /// <summary>
    /// Arranges a 2000 px wide root whose first slot is the 300/1000 group built by <see cref="Group"/>
    /// and checks what the user actually sees: its small leaf (<c>WindowRef(1)</c>) lands at exactly
    /// the floor after <c>RescaleSizes</c> rounds 902 * 300 / 1300 = 208.15, and no window is under it.
    /// </summary>
    private static void AssertSmallInnerLeafLandsAtTheFloor(GroupNode root)
    {
        var result = new LayoutTree(root).Arrange(new Rect(0, 0, 2000, 800));

        Assert.Equal(208, result.Single(item => item.Window == new WindowRef(1)).Bounds.Width);
        Assert.All(result, item => Assert.True(item.Bounds.Width >= LayoutTree.DefaultMinLeafSlotLength));
    }

    /// <summary>
    /// Keyboard resize into a neighbour GROUP of two leaves stacked on the SAME axis: growth stops
    /// where the smaller inner leaf would be rescaled under 208, not where the group's own total
    /// crosses some fixed amount -- the sizes are deliberately uneven so only the proportional rule
    /// produces this exact stopping point.
    /// </summary>
    [Fact]
    public void ResizeNode_KeyboardIntoNestedGroup_StopsWhereTheSmallerInnerLeafWouldGoUnder208()
    {
        // Donor group: 1300 total split 300/1000. Requiring the 300-side to stay >= 208 needs the
        // donor's own total S to satisfy 300/1300*S >= 208, i.e. S >= 901.33 -- so 902 is the floor.
        var (root, donor) = RootWithNestedDonor(
            SplitAxis.Horizontal, rootLength: 2000, focusedSize: 700,
            donorLength: 1300, donorSmall: 300, donorLarge: 1000);

        // A large step (half the root's length) asks for far more than the floor allows.
        var resized = LayoutTree.ResizeNode(Direction.Right, root.Children[0], step: 0.5);

        Assert.True(resized);
        Assert.Equal([1098, 902], root.Sizes);
        Assert.Equal(root.GroupLength, root.Sizes.Sum());

        var tree = new LayoutTree(root);
        var result = tree.Arrange(new Rect(0, 0, 2000, 800));

        // WindowRef(10) is the small inner leaf -- rescaled to exactly the floor, never under it.
        var smallGeometry = result.Single(item => item.Window == new WindowRef(10)).Bounds;
        var largeGeometry = result.Single(item => item.Window == new WindowRef(11)).Bounds;
        Assert.Equal(208, smallGeometry.Width);
        Assert.True(smallGeometry.Width >= LayoutTree.DefaultMinLeafSlotLength);
        Assert.True(largeGeometry.Width >= LayoutTree.DefaultMinLeafSlotLength);
        Assert.Equal(902, smallGeometry.Width + largeGeometry.Width);
    }

    /// <summary>The same protection, reached through the mouse edge-drag path instead of the chord.</summary>
    [Fact]
    public void ApplyEdgeDrag_MouseIntoNestedGroup_StopsWhereTheSmallerInnerLeafWouldGoUnder208()
    {
        var (root, donor) = RootWithNestedDonor(
            SplitAxis.Horizontal, rootLength: 2000, focusedSize: 700,
            donorLength: 1300, donorSmall: 300, donorLarge: 1000);
        _ = donor;

        // Drags the boundary between the focused leaf and the donor group 500 px to the right --
        // far more than the 398 px the donor's leaves can actually spare.
        var applied = LayoutTree.ApplyEdgeDrag(
            root.Children[0],
            new Rect(0, 0, 700, 800),
            new Rect(0, 0, 1200, 800));

        Assert.True(applied);
        Assert.Equal([1098, 902], root.Sizes);
    }

    /// <summary>
    /// A donor GROUP across the resize axis (its own children stacked on the OTHER axis, so every
    /// child spans the donor's full width) floors at 208 too: each child independently needs the
    /// full 208, none of them sharing it through a proportional split.
    /// </summary>
    [Fact]
    public void ResizeNode_DonorAcrossTheAxis_FloorsAtOneLeafSlot()
    {
        var root = Group(SplitAxis.Horizontal, 2000, 700, 1300);
        var donor = new GroupNode(SplitAxis.Vertical) { GroupLength = 1300 };
        var top = new LeafNode(new WindowRef(20)) { Parent = donor };
        var bottom = new LeafNode(new WindowRef(21)) { Parent = donor };
        donor.Children.Add(top);
        donor.Children.Add(bottom);
        donor.Sizes.Add(400);
        donor.Sizes.Add(900);
        donor.Parent = root;
        root.Children[1] = donor;

        var resized = LayoutTree.ResizeNode(Direction.Right, root.Children[0], step: 0.6);

        Assert.True(resized);
        Assert.Equal([1792, 208], root.Sizes);
    }

    /// <summary>
    /// The roles swapped: the FOCUSED subtree gives space up. Pressing Left on the leading child has
    /// no neighbour to grow into, so the chord pushes the opposite boundary and shrinks the focused
    /// group itself -- and the floor has to follow the donor role onto the focused side.
    /// </summary>
    [Fact]
    public void ResizeNode_ShrinkingTheFocusedNestedGroup_StopsWhereItsSmallerInnerLeafWouldGoUnder208()
    {
        // The mirror of the nested-group case: the focused group is 1300 split 300/1000, so its
        // floor is ceil(208 * 1300 / 300) = 902, well above the 10% ratio floor of 200.
        var focused = Group(SplitAxis.Horizontal, 1300, 300, 1000);
        var root = GroupOf(SplitAxis.Horizontal, (focused, 1300), (new LeafNode(new WindowRef(30)), 700));

        var resized = LayoutTree.ResizeNode(Direction.Left, focused, step: 0.5);

        Assert.True(resized);
        Assert.Equal([902, 1098], root.Sizes);
        AssertSmallInnerLeafLandsAtTheFloor(root);
    }

    /// <summary>The same role swap through the mouse: dragging the focused group's own right edge inward.</summary>
    [Fact]
    public void ApplyEdgeDrag_ShrinkingTheFocusedNestedGroup_StopsWhereItsSmallerInnerLeafWouldGoUnder208()
    {
        var focused = Group(SplitAxis.Horizontal, 1300, 300, 1000);
        var root = GroupOf(SplitAxis.Horizontal, (focused, 1300), (new LeafNode(new WindowRef(30)), 700));

        // 500 px inward, more than the 398 px the focused group's leaves can spare.
        var applied = LayoutTree.ApplyEdgeDrag(
            focused,
            new Rect(0, 0, 1300, 800),
            new Rect(0, 0, 800, 800));

        Assert.True(applied);
        Assert.Equal([902, 1098], root.Sizes);
        AssertSmallInnerLeafLandsAtTheFloor(root);
    }

    /// <summary>
    /// Vertical axis, three levels, both kinds of nesting: a same-axis group holding an across-axis
    /// group that holds a same-axis group again. Only the proportional rule composed through BOTH
    /// cases lands on this exact stopping point, and the deepest small leaf ends at exactly 208.
    /// </summary>
    [Fact]
    public void ResizeNode_VerticalDonorNestedTwoLevelsAcrossMixedAxes_KeepsTheDeepestLeafAt208()
    {
        // innermost: Vertical 250/750          -> floor max(ceil(208*1000/250), ceil(208*1000/750)) = 832
        // across:    Horizontal [leaf, inner]  -> floor max(208, 832)                                 = 832
        // donor:     Vertical 1000/1000        -> floor max(208*2000/1000, 832*2000/1000)             = 1664
        var deepest = new LeafNode(new WindowRef(40));
        var inner = GroupOf(SplitAxis.Vertical, (deepest, 250), (new LeafNode(new WindowRef(41)), 750));
        var across = GroupOf(SplitAxis.Horizontal, (new LeafNode(new WindowRef(42)), 500), (inner, 500));
        var donor = GroupOf(SplitAxis.Vertical, (new LeafNode(new WindowRef(43)), 1000), (across, 1000));
        var focused = new LeafNode(new WindowRef(44));
        var root = GroupOf(SplitAxis.Vertical, (focused, 1000), (donor, 2000));

        var resized = LayoutTree.ResizeNode(Direction.Down, focused, step: 0.5);

        Assert.True(resized);
        Assert.Equal([1336, 1664], root.Sizes);

        var result = new LayoutTree(root).Arrange(new Rect(0, 0, 1000, 3000));
        Assert.Equal(208, result.Single(item => item.Window == new WindowRef(40)).Bounds.Height);
        Assert.All(result, item => Assert.True(item.Bounds.Height >= LayoutTree.DefaultMinLeafSlotLength));
    }

    /// <summary>A donor already under its floor gives nothing, and the tree is left exactly as it was.</summary>
    [Fact]
    public void ResizeNode_DonorAlreadyUnderItsFloor_IsNoOpAndLeavesTheTreeUnchanged()
    {
        // 150 clears the 10% ratio floor of 1000 (100) but is already under the 208 leaf floor.
        var group = Group(SplitAxis.Horizontal, 1000, 700, 150);

        var resized = LayoutTree.ResizeNode(Direction.Right, group.Children[0]);

        Assert.False(resized);
        Assert.Equal([700, 150], group.Sizes);
    }

    /// <summary>No regression: a plain two-leaf case well clear of the floor still transfers the full step.</summary>
    [Fact]
    public void ResizeNode_PlainTwoLeafCaseWellAboveTheFloor_TransfersTheFullStep()
    {
        var group = Group(SplitAxis.Horizontal, 1000, 500, 500);

        var resized = LayoutTree.ResizeNode(Direction.Right, group.Children[0]);

        Assert.True(resized);
        Assert.Equal([550, 450], group.Sizes);
        Assert.Equal(group.GroupLength, group.Sizes.Sum());
    }
}
