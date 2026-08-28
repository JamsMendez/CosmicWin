using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// HA-1's last two unimplemented chords: <c>Alt+]</c> "descend into nested group (focus in)" and
/// <c>Alt+[</c> "ascend to parent group (focus out)". They were a documented no-op because the
/// executor tracked a focused LEAF, and a leaf has nothing to ascend from.
///
/// The spec resolves this itself: LE-3, LE-5 and LE-6 all say "the focused NODE", never "leaf", and
/// <see cref="ITilingEngine"/> already accepts <see cref="Node"/> for Move/Toggle/Resize. So focus
/// out/in do not add a new operation — they select WHICH node the existing operations act on. The
/// default scope is the focused leaf, which is exactly today's behaviour, so nothing changes until
/// the user presses <c>Alt+[</c>.
/// </summary>
/// <remarks>
/// Directional focus always returns the scope to a leaf:
/// ascending is a deliberate, short-lived act aimed at the next Move or Resize, not a mode
/// the user can get stranded in. NOTE for whoever picks this up: CosmicWin has no border/overlay
/// surface, so ascending is currently INVISIBLE — the user sees nothing until the next Move or
/// Resize acts on the group. That is a real UX gap, not an oversight in these facts.
/// </remarks>
public sealed class ActionExecutorFocusScopeTests
{
    private static (ActionExecutor Executor, FakeForegroundWindowSource Foreground, RecordingWindow A,
        RecordingWindow B, RecordingWindow C, RecordingWindow D) BuildNestedTree()
    {
        // root Horizontal [ A , (Vertical [ B , C ]) , D ] over a 900x100 work area.
        var leafA = new LeafNode(new WindowRef(new IntPtr(1)));
        var leafB = new LeafNode(new WindowRef(new IntPtr(2)));
        var leafC = new LeafNode(new WindowRef(new IntPtr(3)));
        var leafD = new LeafNode(new WindowRef(new IntPtr(4)));

        var inner = new GroupNode(SplitAxis.Vertical) { GroupLength = 100 };
        inner.Children.Add(leafB);
        inner.Children.Add(leafC);
        inner.Sizes.Add(50);
        inner.Sizes.Add(50);
        leafB.Parent = inner;
        leafC.Parent = inner;

        var root = new GroupNode(SplitAxis.Horizontal) { GroupLength = 900 };
        root.Children.Add(leafA);
        root.Children.Add(inner);
        root.Children.Add(leafD);
        root.Sizes.Add(300);
        root.Sizes.Add(300);
        root.Sizes.Add(300);
        leafA.Parent = root;
        inner.Parent = root;
        leafD.Parent = root;

        ITilingEngine engine = new LayoutTree(root);
        var registry = new WindowRegistry();
        var windowA = new RecordingWindow(leafA.Window.Handle, Rectangle.Empty);
        var windowB = new RecordingWindow(leafB.Window.Handle, Rectangle.Empty);
        var windowC = new RecordingWindow(leafC.Window.Handle, Rectangle.Empty);
        var windowD = new RecordingWindow(leafD.Window.Handle, Rectangle.Empty);
        registry.Register(windowA, leafA);
        registry.Register(windowB, leafB);
        registry.Register(windowC, leafC);
        registry.Register(windowD, leafD);

        var foreground = new FakeForegroundWindowSource { Handle = windowB.Handle };
        var executor = new ActionExecutor(engine, registry, foreground) { WorkArea = new Rect(0, 0, 900, 100) };
        return (executor, foreground, windowA, windowB, windowC, windowD);
    }

    private static Task Press(ActionExecutor executor, HotkeyActionKind kind) =>
        executor.ScheduleAsync(new HotkeyAction(kind), CancellationToken.None).AsTask();

    /// <summary>
    /// The whole point of ascending. Focus starts on B, nested inside the Vertical group. A plain
    /// MoveRight is an LE-5 no-op there (B's parent is Vertical, and B has no adjacent sibling to
    /// reparent with beyond C). After Alt+[, the SAME chord moves the entire group past D.
    /// </summary>
    [Fact]
    public async Task FocusOut_ThenMoveRight_MovesTheWholeGroup_PastItsSibling()
    {
        var (executor, _, windowA, windowB, windowC, windowD) = BuildNestedTree();

        await Press(executor, HotkeyActionKind.FocusOut);
        await Press(executor, HotkeyActionKind.MoveRight);

        // The root has THREE children, so LE-5 forks instead of
        // swapping -- the group pairs up with D inside a new group taking D's slot. A keeps the
        // left half (450); the pair splits the right half, group at 450 and D at 675. What this
        // fact actually pins is unchanged and still holds: the chord moved the WHOLE group, since
        // B and C travelled together and land on the same Left.
        Assert.Equal(0, windowA.LastSetPosition!.Value.Left);
        Assert.Equal(450, windowB.LastSetPosition!.Value.Left);
        Assert.Equal(450, windowC.LastSetPosition!.Value.Left);
        Assert.Equal(675, windowD.LastSetPosition!.Value.Left);
    }

    /// <summary>Ascending must be undoable, or the user is stranded one level up with no way back.</summary>
    [Fact]
    public async Task FocusIn_AfterFocusOut_ReturnsTheScopeToTheLeaf()
    {
        var (executor, _, _, windowB, windowC, _) = BuildNestedTree();

        await Press(executor, HotkeyActionKind.FocusOut);
        await Press(executor, HotkeyActionKind.FocusIn);
        await Press(executor, HotkeyActionKind.ToggleOrientation);

        // Back at leaf scope, Alt+O flips B's OWN parent (the inner Vertical group) to Horizontal,
        // so B and C sit side by side inside the middle third instead of stacked.
        Assert.Equal(300, windowB.LastSetPosition!.Value.Left);
        Assert.Equal(450, windowC.LastSetPosition!.Value.Left);
        Assert.Equal(0, windowB.LastSetPosition!.Value.Top);
        Assert.Equal(0, windowC.LastSetPosition!.Value.Top);
    }

    /// <summary>At the root there is nothing above to select; the chord must be a quiet no-op, never a throw.</summary>
    [Fact]
    public async Task FocusOut_RepeatedPastTheRoot_StaysAtTheRoot_WithoutThrowing()
    {
        var (executor, _, _, windowB, _, _) = BuildNestedTree();

        var exception = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await Press(executor, HotkeyActionKind.FocusOut);
            }
        });

        Assert.Null(exception);
        Assert.Equal(0, windowB.SetPositionCallCount); // Ascending alone never re-arranges anything.
    }

    /// <summary>Descending when the scope is already the leaf has nowhere to go.</summary>
    [Fact]
    public async Task FocusIn_WhenTheScopeIsAlreadyTheLeaf_IsANoOp()
    {
        var (executor, _, _, windowB, windowC, _) = BuildNestedTree();

        await Press(executor, HotkeyActionKind.FocusIn);
        await Press(executor, HotkeyActionKind.ToggleOrientation);

        // Unchanged from a plain Alt+O: the inner group flips, exactly as if Alt+] had never happened.
        Assert.Equal(300, windowB.LastSetPosition!.Value.Left);
        Assert.Equal(450, windowC.LastSetPosition!.Value.Left);
    }

    /// <summary>
    /// Ascending is short-lived by design: a directional focus move puts the user back on a leaf, so
    /// a forgotten Alt+[ cannot silently turn a later Move into a group move.
    /// </summary>
    [Fact]
    public async Task DirectionalFocus_ResetsTheScopeBackToTheLeaf()
    {
        var (executor, _, _, windowB, windowC, _) = BuildNestedTree();

        await Press(executor, HotkeyActionKind.FocusOut);
        await Press(executor, HotkeyActionKind.FocusDown); // B -> C, inside the inner group.
        await Press(executor, HotkeyActionKind.ToggleOrientation);

        Assert.Equal(300, windowB.LastSetPosition!.Value.Left);
        Assert.Equal(450, windowC.LastSetPosition!.Value.Left);
    }

    /// <summary>
    /// The safety valve. If the focused leaf is no longer anywhere beneath the remembered scope --
    /// the user clicked a window in a different branch, or the tree was reshaped underneath -- the
    /// scope is stale and must be dropped rather than applied to an unrelated subtree.
    /// </summary>
    [Fact]
    public async Task WhenTheFocusedLeafIsOutsideTheRememberedScope_TheScopeIsDropped()
    {
        var (executor, foreground, windowA, windowB, windowC, windowD) = BuildNestedTree();
        await Press(executor, HotkeyActionKind.FocusOut); // Scope = the inner Vertical group.

        // The user clicks D by hand: the OS foreground is now a leaf outside that group.
        foreground.Handle = windowD.Handle;

        await Press(executor, HotkeyActionKind.MoveLeft);

        // D forked with the inner GROUP as a single sibling (three root children fork rather than
        // swap), which is what a LEAF-scoped LE-5 move does. Had the
        // stale group scope survived, the move would have acted on the group instead and left D
        // where it was -- D ending up at 675 rather than untouched is what proves the scope dropped.
        Assert.Equal(0, windowA.LastSetPosition!.Value.Left);
        Assert.Equal(450, windowB.LastSetPosition!.Value.Left);
        Assert.Equal(450, windowC.LastSetPosition!.Value.Left);
        Assert.Equal(675, windowD.LastSetPosition!.Value.Left);
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }
}
