using CosmicWin.Layout;
using Xunit.Abstractions;

namespace CosmicWin.Layout.Tests;

/// <summary>
/// TEMPORARY diagnostic, not a behavioural test. Replays the user's reported 3-window
/// scenario through the CURRENT engine and prints the resulting tree after each chord, so a
/// report of the reference implementation's behaviour can be contrasted against what CosmicWin actually does today
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

    /// <summary>
    /// The user's second scenario: six windows opened back to back, which LE-4's
    /// aspect-ratio split heuristic curls into a spiral, then the LAST one pushed Right over and
    /// over. Prints every state so the reported walk can be contrasted against the real one.
    /// </summary>
    [Fact]
    public void ReplaySixWindowSpiralPushedRight()
    {
        foreach (var direction in new[] { Direction.Right, Direction.Left })
        {
            var (tree, last) = SpiralOfSix();
            output.WriteLine($"=== 6-window spiral, F pushed {direction} ===");
            output.WriteLine($"start : {Describe(tree.Root!)}");
            output.WriteLine($"        {Widths(tree)}");

            for (var press = 1; press <= 8; press++)
            {
                var moved = ((ITilingEngine)tree).MoveNode(direction, last);
                output.WriteLine($"press{press,-2}: moved={moved,-5} {Describe(tree.Root!)}");
            }

            output.WriteLine(string.Empty);
        }
    }

    /// <summary>Each new window splits the one opened before it -- the arrival path that curls.</summary>
    private static (LayoutTree Tree, LeafNode Last) SpiralOfSix()
    {
        var first = new LeafNode(new WindowRef(1));
        var tree = new LayoutTree(first);
        var area = new Rect(0, 0, Width, Height);
        LeafNode focused = first;

        for (var handle = 2; handle <= 6; handle++)
        {
            tree.Arrange(area);
            var region = focused.LastGeometry;
            var wasRoot = ReferenceEquals(tree.Root, focused);
            var split = LayoutTree.SplitLeafInPlace(focused, new WindowRef(handle), region.Width, region.Height);
            if (wasRoot)
            {
                tree.Root = split;
            }

            focused = (LeafNode)split.Children[^1];
        }

        tree.Arrange(area);
        return (tree, focused);
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
