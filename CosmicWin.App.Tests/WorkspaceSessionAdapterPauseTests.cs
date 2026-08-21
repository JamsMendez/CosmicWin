using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// Tasks 3.15/3.16 (WU11), settled full-pause semantics (Engram decision #62): Pausar gates <see
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

    /// <summary>A window opened during a pause is not retroactively pulled in once Reanudar is clicked (forward-only rule, decision #58).</summary>
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

    /// <summary>Existing window positions are unaffected by pausing (spec TC-2: "existing window positions are unaffected").</summary>
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
    /// V11-W2 (WU11-W2), settled decision #64 "full pause, no reconcile": while paused, closing a
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

        var group = Assert.IsType<GroupNode>(tree.Root);
        var remainingLeaf = Assert.IsType<LeafNode>(Assert.Single(group.Children));
        Assert.Equal(new WindowRef(survivor.Handle), remainingLeaf.Window);
        Assert.False(registry.TryGetWindow(closing.Handle, out _));
        Assert.False(registry.TryGetLeaf(closing.Handle, out _));
        Assert.Equal(callCountBeforeClose, survivor.SetPositionCallCount);
    }

    /// <summary>
    /// Regression guard (control case for V11-W2): the unpaused removal path is unchanged from
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

        var group = Assert.IsType<GroupNode>(tree.Root);
        var remainingLeaf = Assert.IsType<LeafNode>(Assert.Single(group.Children));
        Assert.Equal(new WindowRef(survivor.Handle), remainingLeaf.Window);
        Assert.False(registry.TryGetWindow(closing.Handle, out _));
        Assert.True(callCountBeforeClose < survivor.SetPositionCallCount);
        Assert.Equal(Rectangle.FromSize(0, 0, 1920, 1080), survivor.LastSetPosition);
    }

    /// <summary>
    /// V12-W3: closes verify-report #21 revision 12's WARNING that the prior version of this fact
    /// could not fail under any implementation. That version flipped a local <c>bool</c> captured by
    /// a lambda between setup and its assertion -- no production code ran in between, and under
    /// mutation MR1 (deleting the removal pause gate outright) only the sibling gated fact failed
    /// while this one stayed green. This version routes "Reanudar" through the SAME real production
    /// seam the tray menu drives: <see cref="TrayMenuController.TogglePause"/> flips the SAME <see
    /// cref="LowLevelKeyboardHook.IsPaused"/> flag <see cref="CompositionRoot.BuildTrayMenuController"/>
    /// wires in production (mirrored here with an equivalent inline delegate, since no
    /// <see cref="CosmicWin.Layout"/> tree/registry access is needed to build that controller). "no
    /// reconcile on resume" (decision #64) still holds true by construction today -- <see
    /// cref="WorkspaceSessionAdapter"/> has no resume hook, timer or re-enumeration anywhere in the
    /// class -- but now the assertion is reached only after real production code has actually run,
    /// so a future change wiring reconciliation into that same toggle path would be exercised, and
    /// this fact would fail. Proven by mutation: temporarily wiring the toggle's resume branch to
    /// touch the survivor turns this exact fact RED; reverting restores GREEN (see apply-progress).
    /// </summary>
    [Fact]
    public void WindowRemoved_WhilePaused_ThenResumedViaTogglePause_NoRetroactiveArrangeIsFired()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var hook = new LowLevelKeyboardHook(Channel.CreateUnbounded<HotkeyAction>().Writer);
        var controller = new TrayMenuController(
            () => hook.IsPaused, paused => hook.IsPaused = paused, reload: () => { }, exit: () => { });
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty,
            () => hook.IsPaused);

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
}
