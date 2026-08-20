using CosmicWin.Layout;

namespace CosmicWin.Layout.Tests;

public class TilingEngineContractTests
{
    [Fact]
    public void LayoutTree_ImplementsPureTilingEngineContract()
    {
        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 100 };
        var first = new LeafNode(new WindowRef(1));
        var second = new LeafNode(new WindowRef(2));
        LayoutTree.AddChild(root, first, 0);
        LayoutTree.AddChild(root, second, 1);

        ITilingEngine engine = new LayoutTree(root);

        Assert.Equal(FocusWalkStatus.Found, engine.NextFocus(Direction.Right, first).Status);
        Assert.True(engine.MoveNode(Direction.Right, first));
        Assert.True(engine.ToggleAxis(first));
        Assert.True(engine.ResizeNode(Direction.Up, first));
        Assert.Equal(2, engine.Arrange(new Rect(0, 0, 200, 100)).Count);
    }

    [Fact]
    public void LayoutTree_EmptyRoot_ArrangesToEmptyResult()
    {
        ITilingEngine engine = new LayoutTree();

        Assert.Empty(engine.Arrange(new Rect(0, 0, 100, 100)));
    }
}
