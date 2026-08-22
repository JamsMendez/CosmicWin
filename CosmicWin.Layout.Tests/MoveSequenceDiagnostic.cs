using CosmicWin.Layout;
using Xunit.Abstractions;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// TEMPORARY diagnostic, not a behavioural test. Replays the maintainer's reported 3-window
/// scenario through the CURRENT engine and prints the resulting tree after each chord, so a
/// report of COSMIC's behaviour can be contrasted against what CosmicWin actually does today
/// instead of against a guess about what it does.
/// </summary>
public sealed class MoveSequenceDiagnostic(ITestOutputHelper output)
{
    private const int Width = 3440;
    private const int Height = 1392;

    [Fact]
    public void ReplayThreeWindowLeftThenRightSequence()
    {
        var (tree, c) = ThreeWindows();
        output.WriteLine("=== LEFT x4, focus on C ===");
        output.WriteLine($"start : {Describe(tree.Root!)}   |   {Widths(tree)}");
        Press(tree, c, Direction.Left, 4);

        // Reversibility: the same walk back must retrace the identical states.
        output.WriteLine(string.Empty);
        output.WriteLine("=== then RIGHT x5, same window ===");
        Press(tree, c, Direction.Right, 5);
    }

    private void Press(LayoutTree tree, LeafNode focused, Direction direction, int times)
    {
        for (var press = 1; press <= times; press++)
        {
            var moved = ((ITilingEngine)tree).MoveNode(direction, focused);
            output.WriteLine($"press{press}: moved={moved,-5} {Describe(tree.Root!),-18}   |   {Widths(tree)}");
        }
    }

    /// <summary>A, then B splitting A, then C splitting B -- the real LE-4 arrival path.</summary>
    private static (LayoutTree Tree, LeafNode C) ThreeWindows()
    {
        var a = new LeafNode(new WindowRef(1));
        var tree = new LayoutTree(a);

        var firstSplit = LayoutTree.SplitLeafInPlace(a, new WindowRef(2), Width, Height);
        tree.Root = firstSplit;
        var b = (LeafNode)firstSplit.Children[^1];

        tree.Arrange(new Rect(0, 0, Width, Height));
        var bRegion = b.LastGeometry;
        var secondSplit = LayoutTree.SplitLeafInPlace(b, new WindowRef(3), bRegion.Width, bRegion.Height);
        var c = (LeafNode)secondSplit.Children[^1];

        tree.Arrange(new Rect(0, 0, Width, Height));
        return (tree, c);
    }

    private static string Describe(Node node) => node switch
    {
        LeafNode leaf => Name(leaf),
        GroupNode g => (g.Axis == SplitAxis.Horizontal ? "H[" : "V[")
            + string.Join(" ", g.Children.Select(Describe)) + "]",
        _ => "?"
    };

    private static string Widths(LayoutTree tree) => string.Join("  ",
        tree.Arrange(new Rect(0, 0, Width, Height))
            .OrderBy(entry => entry.Bounds.X)
            .Select(entry => $"{Name(entry.Window)}={entry.Bounds.Width}"));

    private static string Name(LeafNode leaf) => Name(leaf.Window);

    private static string Name(WindowRef window) => ((char)('A' + (int)window.Handle - 1)).ToString();
}
