using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// A chord that MOVES a window acts on the window the user is looking at, or on nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// Reported from real use and reproduced deliberately: with GoLand's Settings dialog focused,
/// <c>Alt+Shift+&lt;direction&gt;</c> rearranged the GoLand window BEHIND it. The dialog is owned,
/// so <c>IsTrackable</c> keeps it out of the tree entirely -- measured, <c>track=no</c> -- and
/// <see cref="ActionExecutor"/> then fell back to the last leaf it had focused, which was the
/// window underneath.
/// </para>
/// <para>
/// The fallback itself is deliberate and stays. For a FOCUS chord it is the right answer: it is how
/// a user gets back into the tiled world from a dialog or a non-tiled app, and dropping the chord
/// would strand them. For a MUTATION it is the wrong answer twice over -- it rearranges something
/// the user did not aim at, and it does so underneath a modal, where they cannot even see it happen.
/// </para>
/// <para>
/// The distinction is the whole fix. <c>ActionExecutorFocusResolutionTests</c> pins the fallback for
/// focus chords; every one of its facts uses <c>FocusRight</c>, and none of them is weakened here.
/// </para>
/// </remarks>
public sealed class ActionExecutorUntrackedForegroundTests
{
    /// <summary>A window handle belonging to nothing CosmicWin tracks — a modal dialog, in the report.</summary>
    private static readonly IntPtr UntrackedDialog = new(0x404);

    private sealed class FakeForeground : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed record Harness(
        ActionExecutor Executor, FakeForeground Foreground, LayoutTree Tree, GroupNode Group,
        RecordingWindow WindowA, RecordingWindow WindowB, RecordingWindow WindowC)
    {
        /// <summary>
        /// Read from the TREE's root, never from the group captured at build time: a move that forks
        /// reassigns the root, leaving that group a stale subtree that reports the tree as having
        /// lost windows it still holds.
        /// </summary>
        public nint[] Leaves => Tree.Root is { } root ? LeavesOf(root) : [];
    }

    private static Harness BuildThreeLeafRow()
    {
        var leafA = new LeafNode(new WindowRef(new IntPtr(1)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(2)));
        var leafC = new LeafNode(new WindowRef(new IntPtr(3)));
        var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        foreach (var leaf in new[] { leafA, leafB, leafC })
        {
            group.Children.Add(leaf);
            group.Sizes.Add(300);
            leaf.Parent = group;
        }

        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(leafA.Window.Handle, Rectangle.Empty);
        var windowB = new RecordingWindow(leafB.Window.Handle, Rectangle.Empty);
        var windowC = new RecordingWindow(leafC.Window.Handle, Rectangle.Empty);
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);
        registry.Register(windowC, leafC);

        var tree = new LayoutTree(group);
        var foreground = new FakeForeground { Handle = windowA.Handle };
        var executor = new ActionExecutor(tree, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 900, 100),
        };

        return new Harness(executor, foreground, tree, group, windowA, windowB, windowC);
    }

    /// <summary>Every leaf under <paramref name="root"/>, in order — a move can nest them arbitrarily deep.</summary>
    private static nint[] LeavesOf(Node root)
    {
        if (root is LeafNode leaf)
        {
            return [leaf.Window.Handle];
        }

        return root is GroupNode group
            ? group.Children.SelectMany(LeavesOf).ToArray()
            : [];
    }

    /// <summary>
    /// Populates the focus cache the way real use does -- a chord answered while a tracked window
    /// held the foreground -- so the untracked-foreground facts below exercise the FALLBACK rather
    /// than an empty cache, which would pass for the wrong reason.
    /// </summary>
    private static async Task PrimeFocusCacheAsync(Harness harness)
    {
        harness.Foreground.Handle = harness.WindowA.Handle;
        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        harness.Foreground.Handle = UntrackedDialog;
    }

    /// <summary>The reported defect, exactly: a move chord under a modal must touch nothing.</summary>
    [Theory]
    [InlineData(HotkeyActionKind.MoveLeft)]
    [InlineData(HotkeyActionKind.MoveRight)]
    [InlineData(HotkeyActionKind.MoveUp)]
    [InlineData(HotkeyActionKind.MoveDown)]
    public async Task AMoveChord_WithAnUntrackedForeground_ChangesNothing(HotkeyActionKind kind)
    {
        var harness = BuildThreeLeafRow();
        await PrimeFocusCacheAsync(harness);
        var before = harness.Leaves;

        await harness.Executor.ScheduleAsync(new HotkeyAction(kind), CancellationToken.None);

        Assert.Equal(before, harness.Leaves);
        Assert.Equal(0, harness.WindowA.SetPositionCallCount);
        Assert.Equal(0, harness.WindowB.SetPositionCallCount);
        Assert.Equal(0, harness.WindowC.SetPositionCallCount);
    }

    /// <summary>
    /// Resize is the same class of harm and worse to notice: nothing changes order, so the only
    /// evidence is a window quietly growing behind a dialog.
    /// </summary>
    [Theory]
    [InlineData(HotkeyActionKind.ResizeLeft)]
    [InlineData(HotkeyActionKind.ResizeRight)]
    [InlineData(HotkeyActionKind.ResizeUp)]
    [InlineData(HotkeyActionKind.ResizeDown)]
    public async Task AResizeChord_WithAnUntrackedForeground_ChangesNothing(HotkeyActionKind kind)
    {
        var harness = BuildThreeLeafRow();
        await PrimeFocusCacheAsync(harness);
        var sizes = harness.Group.Sizes.ToArray();

        await harness.Executor.ScheduleAsync(new HotkeyAction(kind), CancellationToken.None);

        Assert.Equal(sizes, harness.Group.Sizes);
        Assert.Equal(0, harness.WindowA.SetPositionCallCount);
        Assert.Equal(0, harness.WindowB.SetPositionCallCount);
    }

    [Fact]
    public async Task ToggleOrientation_WithAnUntrackedForeground_ChangesNothing()
    {
        var harness = BuildThreeLeafRow();
        await PrimeFocusCacheAsync(harness);
        var axis = harness.Group.Axis;

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.ToggleOrientation), CancellationToken.None);

        Assert.Equal(axis, harness.Group.Axis);
        Assert.Equal(0, harness.WindowA.SetPositionCallCount);
        Assert.Equal(0, harness.WindowB.SetPositionCallCount);
    }

    /// <summary>
    /// The half that must NOT change. Focus still falls back, because that is how a user reaches the
    /// tiled world again from a window CosmicWin does not manage.
    /// </summary>
    [Fact]
    public async Task AFocusChord_WithAnUntrackedForeground_StillFallsBackToTheCachedLeaf()
    {
        var harness = BuildThreeLeafRow();
        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);
        harness.Foreground.Handle = UntrackedDialog;

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.FocusRight), CancellationToken.None);

        Assert.Equal(1, harness.WindowC.TryActivateCallCount);
    }

    /// <summary>
    /// The ordinary case is untouched: a real tiled window still answers its move chord.
    /// </summary>
    /// <remarks>
    /// Asserted on geometry being APPLIED -- the exact mirror of what every untracked fact above
    /// asserts is zero -- and deliberately not on the tree's shape. <c>MoveNode</c>'s ancestor walk
    /// forks, which nests two leaves into a new group while leaving the flattened leaf order
    /// identical, so order proves nothing here and pinning the resulting shape would be re-testing
    /// the engine from the wrong layer.
    /// </remarks>
    [Fact]
    public async Task AMoveChord_WithATrackedForeground_StillMovesTheWindow()
    {
        var harness = BuildThreeLeafRow();
        harness.Foreground.Handle = harness.WindowA.Handle;

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None);

        Assert.True(harness.WindowA.SetPositionCallCount > 0);
        Assert.True(harness.WindowB.SetPositionCallCount > 0);
    }

    /// <summary>
    /// Doing nothing was only ever half the answer. A floating window still deserves the chord --
    /// it just cannot be moved through a tree it is not in, so the direction is handed to whoever
    /// manages it instead.
    /// </summary>
    [Theory]
    [InlineData(HotkeyActionKind.MoveLeft, Direction.Left)]
    [InlineData(HotkeyActionKind.MoveRight, Direction.Right)]
    [InlineData(HotkeyActionKind.MoveUp, Direction.Up)]
    [InlineData(HotkeyActionKind.MoveDown, Direction.Down)]
    public async Task AMoveChord_WithAnUntrackedForeground_OffersTheDirectionToTheFloatingHandler(
        HotkeyActionKind kind, Direction expected)
    {
        var harness = BuildThreeLeafRow();
        var offered = new List<(nint Handle, Direction Direction)>();
        harness.Executor.MoveFloatingWindow = (handle, direction) =>
        {
            offered.Add((handle, direction));
            return true;
        };

        await PrimeFocusCacheAsync(harness);

        await harness.Executor.ScheduleAsync(new HotkeyAction(kind), CancellationToken.None);

        Assert.Equal([(UntrackedDialog, expected)], offered);
        Assert.Equal(0, harness.WindowA.SetPositionCallCount);
    }

    /// <summary>
    /// Offered even when nothing has ever been focused. The tree may be empty, or the dialog may be
    /// the first window of the session -- neither is a reason to swallow the chord, which is why the
    /// offer is made BEFORE focus is resolved rather than inside the branch that needs a leaf.
    /// </summary>
    [Fact]
    public async Task AMoveChord_WithAnUntrackedForegroundAndNothingEverFocused_StillOffersTheDirection()
    {
        var harness = BuildThreeLeafRow();
        var offered = 0;
        harness.Executor.MoveFloatingWindow = (_, _) => { offered++; return true; };
        harness.Foreground.Handle = UntrackedDialog;

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.MoveLeft), CancellationToken.None);

        Assert.Equal(1, offered);
    }

    /// <summary>
    /// Resize and toggle-axis stay no-ops. Half a work area is a position, not a size a dialog laid
    /// itself out for, and there is no meaning to toggling the split axis of a window in no group.
    /// </summary>
    [Theory]
    [InlineData(HotkeyActionKind.ResizeLeft)]
    [InlineData(HotkeyActionKind.ResizeUp)]
    [InlineData(HotkeyActionKind.ToggleOrientation)]
    public async Task ANonMoveMutation_WithAnUntrackedForeground_IsNotOfferedToTheFloatingHandler(
        HotkeyActionKind kind)
    {
        var harness = BuildThreeLeafRow();
        var offered = 0;
        harness.Executor.MoveFloatingWindow = (_, _) => { offered++; return true; };

        await PrimeFocusCacheAsync(harness);
        await harness.Executor.ScheduleAsync(new HotkeyAction(kind), CancellationToken.None);

        Assert.Equal(0, offered);
    }

    /// <summary>A tracked window is the tree's business; the floating handler must never hear about it.</summary>
    [Fact]
    public async Task AMoveChord_WithATrackedForeground_IsNeverOfferedToTheFloatingHandler()
    {
        var harness = BuildThreeLeafRow();
        var offered = 0;
        harness.Executor.MoveFloatingWindow = (_, _) => { offered++; return true; };
        harness.Foreground.Handle = harness.WindowA.Handle;

        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.MoveRight), CancellationToken.None);

        Assert.Equal(0, offered);
        Assert.True(harness.WindowA.SetPositionCallCount > 0);
    }
}
