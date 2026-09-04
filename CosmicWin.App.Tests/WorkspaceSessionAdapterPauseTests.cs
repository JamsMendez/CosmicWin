using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// Pausar gates <see
/// cref="WorkspaceSessionAdapter.OnWindowAdded"/> in addition to the hotkey path (see <see
/// cref="Input.KeyboardHookTests"/>) -- a window opened while paused is NOT auto-tiled and is not
/// retroactively pulled in on Reanudar (forward-only, matching WE-3's own scenario direction).
/// Pausing itself performs no tree mutation -- the existing layout is left exactly as it was.
/// </summary>
public sealed class WorkspaceSessionAdapterPauseTests
{
    [Fact]
    public void WindowAdded_WhilePaused_NeverAddedToTreeOrRegistered()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty, () => true);

        var window = new RecordingWindow(new IntPtr(900), Rectangle.FromSize(0, 0, 800, 600));
        workspace.RaiseWindowAdded(window);

        Assert.Null(tree.Root);
        Assert.Equal(0, window.SetPositionCallCount);
        Assert.False(registry.TryGetWindow(window.Handle, out _));
    }

    /// <summary>A window opened during a pause is not retroactively pulled in once Reanudar is clicked (forward-only rule, an earlier decision).</summary>
    [Fact]
    public void WindowAdded_AfterResume_NewWindowIsAddedAndArranged_PauseWindowNeverRetroactivelyPulledIn()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var paused = true;
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty, () => paused);

        var duringPause = new RecordingWindow(new IntPtr(901), Rectangle.FromSize(0, 0, 800, 600));
        workspace.RaiseWindowAdded(duringPause);
        paused = false;
        var afterResume = new RecordingWindow(new IntPtr(902), Rectangle.FromSize(0, 0, 800, 600));
        workspace.RaiseWindowAdded(afterResume);

        var leaf = Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(new WindowRef(afterResume.Handle), leaf.Window);
        Assert.Equal(1, afterResume.SetPositionCallCount);
        Assert.False(registry.TryGetWindow(duringPause.Handle, out _));
    }

    /// <summary>Existing window positions are unaffected by pausing.</summary>
    [Fact]
    public void WindowAdded_WhilePaused_DoesNotRearrangeOrRepositionAnExistingTiledWindow()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var paused = false;
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty, () => paused);

        var existing = new RecordingWindow(new IntPtr(903), Rectangle.FromSize(0, 0, 800, 600));
        workspace.RaiseWindowAdded(existing);
        Assert.Equal(1, existing.SetPositionCallCount);

        paused = true;
        var duringPause = new RecordingWindow(new IntPtr(904), Rectangle.FromSize(0, 0, 800, 600));
        workspace.RaiseWindowAdded(duringPause);

        Assert.Equal(1, existing.SetPositionCallCount);
        Assert.Equal(0, duringPause.SetPositionCallCount);
        var leaf = Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(new WindowRef(existing.Handle), leaf.Window);
    }

    /// <summary>
    /// While paused, closing a
    /// tracked window still removes its node from the tree and unregisters it -- no dead handle is
    /// left behind -- but <see cref="TreeArranger.ArrangeAndPosition"/> is NOT invoked, so the
    /// surviving sibling is left exactly where it was, un-repositioned. The on-screen hole this
    /// creates (the survivor keeps its stale, now-half-empty geometry) is accepted behavior for
    /// TC-2, not a defect.
    /// </summary>
    [Fact]
    public void WindowRemoved_WhilePaused_RemovesNodeFromTree_ButDoesNotRearrangeOrRepositionSurvivor()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var paused = false;
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty, () => paused);

        var survivor = new RecordingWindow(new IntPtr(905), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(survivor);
        var closing = new RecordingWindow(new IntPtr(906), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(closing);
        var callCountBeforeClose = survivor.SetPositionCallCount;

        paused = true;
        workspace.RaiseWindowRemoved(closing);

        // A group down to a single child is now collapsed on removal, so the survivor IS the
        // root rather than sitting inside a one-child wrapper. The wrapper was incidental
        // structure this fact never meant to pin; the survivor and its geometry are.
        var remainingLeaf = Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(new WindowRef(survivor.Handle), remainingLeaf.Window);
        Assert.False(registry.TryGetWindow(closing.Handle, out _));
        Assert.False(registry.TryGetLeaf(closing.Handle, out _));
        Assert.Equal(callCountBeforeClose, survivor.SetPositionCallCount);
    }

    /// <summary>
    /// Regression guard (control case for): the unpaused removal path is unchanged from
    /// pre-existing behavior -- the node is removed AND the survivor is re-arranged and
    /// repositioned to fill the full work area, exactly as
    /// <see cref="WorkspaceSessionAdapterTests.WindowRemoved_WithSibling_ReArrangesTree_AndPositionsRemainingLeafToFullWorkArea"/>
    /// already pins.
    /// </summary>
    [Fact]
    public void WindowRemoved_NotPaused_RemovesNodeFromTree_AndRearrangesAndRepositionsSurvivor()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var paused = false;
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty, () => paused);

        var survivor = new RecordingWindow(new IntPtr(907), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(survivor);
        var closing = new RecordingWindow(new IntPtr(908), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(closing);
        var callCountBeforeClose = survivor.SetPositionCallCount;

        workspace.RaiseWindowRemoved(closing);

        // A group down to a single child is now collapsed on removal, so the survivor IS the
        // root rather than sitting inside a one-child wrapper. The wrapper was incidental
        // structure this fact never meant to pin; the survivor and its geometry are.
        var remainingLeaf = Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(new WindowRef(survivor.Handle), remainingLeaf.Window);
        Assert.False(registry.TryGetWindow(closing.Handle, out _));
        Assert.True(callCountBeforeClose < survivor.SetPositionCallCount);
        Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), survivor.LastSetPosition);
    }

    /// <summary>
    /// Closes 's WARNING that the prior version of this fact
    /// could not fail under any implementation. That version flipped a local <c>bool</c> captured by
    /// a lambda between setup and its assertion -- no production code ran in between, and under
    /// mutation MR1 (deleting the removal pause gate outright) only the sibling gated fact failed
    /// while this one stayed green.
    /// </summary>
    /// <remarks>
    /// The rewrite routed "Reanudar" through <see
    /// cref="TrayMenuController.TogglePause"/> against a real <see cref="LowLevelKeyboardHook"/>, but
    /// built its own <see cref="TrayMenuController"/> instance with an inline delegate
    /// "mirrored...to be an equivalent" of <see cref="CompositionRoot.BuildTrayMenuController"/>'s
    /// own wiring, rather than obtaining the controller FROM that factory. That pinned the hand-
    /// written mirror, not the real production seam: probe P4 showed a genuine
    /// production-shaped reconcile-on-resume, wired through <see
    /// cref="CompositionRoot.BuildTrayMenuController"/>'s real <c>setPaused</c> delegate and invoked
    /// from <see cref="App"/>, compiled clean and stayed green against the version of this
    /// fact. This version obtains the controller directly from <see
    /// cref="CompositionRoot.BuildTrayMenuController"/> itself -- the exact factory the tray's
    /// Reanudar menu item drives in production -- so a future production reconcile wired into that
    /// factory's own <c>setPaused</c> delegate is now exercised by this fact, not bypassed by it.
    /// This fact still hand-built the ADAPTER half of the production wiring with
    /// <c>new WorkspaceSessionAdapter(...)</c>, which is <see
    /// cref="CompositionRoot.BuildPauseGatedSession"/>'s body retyped by hand
    /// probe P3 showed a genuine production reconcile-on-resume wired at THAT factory (where the
    /// hook and the adapter are actually coupled) escaped this fact entirely, with zero test edits
    /// needed to ship it. This version obtains the adapter from <see
    /// cref="CompositionRoot.BuildPauseGatedSession"/> itself, so BOTH halves of the fact now come
    /// from production factories, not a mirror. "No reconcile on resume" still holds
    /// true by construction today -- <see cref="WorkspaceSessionAdapter"/> has no resume hook, timer
    /// or re-enumeration anywhere in the class. Proven by mutation, twice: (1) 's own proof
    /// temporarily adding a production-shaped <c>WorkspaceSessionAdapter.Reconcile()</c> wired into
    /// <see cref="CompositionRoot.BuildTrayMenuController"/>'s own <c>setPaused</c> delegate turns
    /// this exact fact RED; (2) 's proof -- temporarily wiring that SAME <c>Reconcile</c>
    /// into <see cref="CompositionRoot.BuildPauseGatedSession"/> itself (the factory this fact now
    /// calls) ALSO turns it RED, with NO edit to this file at all -- three production-only edits are
    /// enough. Reverting either probe restores GREEN (see apply-progress).
    /// </remarks>
    [Fact]
    public void WindowRemoved_WhilePaused_ThenResumedViaTogglePause_NoRetroactiveArrangeIsFired()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var hook = new LowLevelKeyboardHook(Channel.CreateUnbounded<HotkeyAction>().Writer);
        var exceptions = new ExceptionListStore(ExceptionList.Empty);
        var controller = CompositionRoot.BuildTrayMenuController(
            hook, exceptions, loadExceptions: () => ExceptionList.Empty,
            getFocusBorder: () => true, setFocusBorder: _ => { }, exit: () => { });
        var (_, executor) = CompositionRoot.Build(
            new RecordingTilingEngine(), registry, new StaticForegroundWindowSource(IntPtr.Zero),
            new Rect(0, 0, 1920, 1080));
        using var adapter = CompositionRoot.BuildPauseGatedSession(
            workspace, tree, registry, executor, exceptions, hook);

        var survivor = new RecordingWindow(new IntPtr(909), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(survivor);
        var closing = new RecordingWindow(new IntPtr(910), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(closing);

        controller.TogglePause(); // Pausar -- the real production seam, not a local bool flip
        workspace.RaiseWindowRemoved(closing);
        var callCountAfterPausedRemoval = survivor.SetPositionCallCount;

        controller.TogglePause(); // Reanudar -- the SAME real production seam

        Assert.Equal(callCountAfterPausedRemoval, survivor.SetPositionCallCount);
    }

    /// <summary>
    /// Minimal <see cref="ITilingEngine"/> double so <see
    /// cref="WindowRemoved_WhilePaused_ThenResumedViaTogglePause_NoRetroactiveArrangeIsFired"/> can
    /// obtain a real <see cref="ActionExecutor"/> (needed by <see
    /// cref="CompositionRoot.BuildPauseGatedSession"/>) without depending on hotkey-dispatch
    /// behavior this fact never exercises. Mirrors the identical double already used by
    /// <c>CompositionRootTests.RecordingTilingEngine</c>.
    /// </summary>
    private sealed class RecordingTilingEngine : ITilingEngine
    {
        public FocusResult NextFocus(Direction direction, LeafNode focused) => FocusResult.NoMatch;

        public bool MoveNode(Direction direction, Node focused) => false;

        public bool ToggleAxis(Node focused) => false;

        public bool ResizeNode(Direction direction, Node focused, double step = LayoutTree.DefaultResizeStep, int minLength = 0, int maxLength = int.MaxValue) => false;

        public IReadOnlyList<(WindowRef Window, Rect Bounds)> Arrange(Rect workArea) =>
            Array.Empty<(WindowRef, Rect)>();

        public bool Remove(Node focused) => false;
    }

    /// <summary>: minimal <see cref="IForegroundWindowSource"/> double, same reason as <see cref="RecordingTilingEngine"/>.</summary>
    private sealed class StaticForegroundWindowSource(nint handle) : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => handle;
    }
}
