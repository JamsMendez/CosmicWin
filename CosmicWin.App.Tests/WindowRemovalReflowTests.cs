using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The maintainer's report, 2026-08-22, immediately after new windows began splitting the focused
/// tile: "cuando las cierro no se reacomoda y no se extienden, queda vacío el espacio que ocupaban".
///
/// These facts drive the real production removal path and assert the property the eye actually
/// checks: after a close, the survivors still tile the WHOLE work area with no gap and no overlap.
/// Asserting only "the leaf left the tree" would have stayed green through this defect, because the
/// leaf did leave — it was the hollow group above it that kept the space.
/// </summary>
public sealed class WindowRemovalReflowTests
{
    private sealed record Setup(TreeManager Trees, WindowRegistry Registry, FakeWorkspace Workspace, IDisplay Primary);

    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1080);

    private static Setup OneDisplay()
    {
        var primary = new FakeDisplay(new IntPtr(1), WorkArea, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        return new Setup(new TreeManager([primary], primary, registry), registry, new FakeWorkspace(), primary);
    }

    private static RecordingWindow Window(int handle) =>
        new(new IntPtr(handle), Rectangle.FromSize(0, 0, 400, 300));

    /// <summary>Every survivor's rectangle, tiled edge to edge, must add back up to the work area.</summary>
    private static void AssertNoGapOrOverlap(params RecordingWindow[] survivors)
    {
        var rects = survivors.Select(w => w.LastSetPosition!.Value).ToArray();
        Assert.Equal(WorkArea.Width * WorkArea.Height, rects.Sum(r => (long)r.Width * r.Height));

        foreach (var rect in rects)
        {
            Assert.True(
                rect.Left >= WorkArea.Left && rect.Top >= WorkArea.Top &&
                rect.Right <= WorkArea.Right && rect.Bottom <= WorkArea.Bottom,
                $"{rect} escapes the work area.");
        }

        for (var i = 0; i < rects.Length; i++)
        {
            for (var j = i + 1; j < rects.Length; j++)
            {
                var overlapWidth = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].Left, rects[j].Left);
                var overlapHeight = Math.Min(rects[i].Bottom, rects[j].Bottom) - Math.Max(rects[i].Top, rects[j].Top);
                Assert.True(overlapWidth <= 0 || overlapHeight <= 0, $"{rects[i]} overlaps {rects[j]}.");
            }
        }
    }

    /// <summary>
    /// The exact reported shape. Three windows, the third splitting the second, leaves
    /// root H[ W1 , V[W2, W3] ]. Closing W3 then W2 empties that nested group; before pruning, its
    /// slot stayed reserved and the entire right half of the screen went blank.
    /// </summary>
    [Fact]
    public void ClosingTheLastWindowOfANestedGroup_LetsTheSurvivorReclaimTheWholeArea()
    {
        var s = OneDisplay();
        RecordingWindow? focused = null;
        using var adapter = new MultiMonitorWorkspaceAdapter(
            s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false,
            () => focused is not null && s.Registry.TryGetLeaf(focused.Handle, out var leaf) ? leaf : null);

        var first = Window(10);
        var second = Window(20);
        var third = Window(30);
        focused = first;
        s.Workspace.RaiseWindowAdded(first);
        focused = second;
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(third);

        s.Workspace.RaiseWindowRemoved(third);
        s.Workspace.RaiseWindowRemoved(second);

        Assert.Equal(WorkArea, first.LastSetPosition);
    }

    /// <summary>One close, one survivor per branch: the nested group collapses and nothing is left blank.</summary>
    [Fact]
    public void ClosingOneOfTwoWindowsInANestedGroup_ReflowsWithoutLeavingAGap()
    {
        var s = OneDisplay();
        RecordingWindow? focused = null;
        using var adapter = new MultiMonitorWorkspaceAdapter(
            s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false,
            () => focused is not null && s.Registry.TryGetLeaf(focused.Handle, out var leaf) ? leaf : null);

        var first = Window(10);
        var second = Window(20);
        var third = Window(30);
        focused = first;
        s.Workspace.RaiseWindowAdded(first);
        focused = second;
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(third);

        s.Workspace.RaiseWindowRemoved(third);

        AssertNoGapOrOverlap(first, second);
        Assert.Equal(Rectangle.FromSize(960, 0, 960, 1080), second.LastSetPosition);
    }

    /// <summary>
    /// The hollow level must not survive the close, or LE-2's tree walk and LE-5's moves keep
    /// counting a group that holds nothing.
    /// </summary>
    [Fact]
    public void ClosingAWindow_LeavesNoHollowGroupBehind()
    {
        var s = OneDisplay();
        RecordingWindow? focused = null;
        using var adapter = new MultiMonitorWorkspaceAdapter(
            s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false,
            () => focused is not null && s.Registry.TryGetLeaf(focused.Handle, out var leaf) ? leaf : null);

        var first = Window(10);
        var second = Window(20);
        var third = Window(30);
        focused = first;
        s.Workspace.RaiseWindowAdded(first);
        focused = second;
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(third);

        s.Workspace.RaiseWindowRemoved(third);

        s.Trees.TryGetTree(s.Primary, out var tree);
        var root = Assert.IsType<GroupNode>(tree!.Root);
        Assert.All(root.Children, child => Assert.IsType<LeafNode>(child));
    }

    /// <summary>Closing the very last window empties the tree outright rather than leaving a hollow root.</summary>
    [Fact]
    public void ClosingTheOnlyWindow_EmptiesTheTree()
    {
        var s = OneDisplay();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);

        var only = Window(10);
        s.Workspace.RaiseWindowAdded(only);
        s.Workspace.RaiseWindowRemoved(only);

        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
    }
}
