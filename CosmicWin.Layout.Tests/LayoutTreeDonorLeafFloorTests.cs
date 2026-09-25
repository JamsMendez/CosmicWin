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
