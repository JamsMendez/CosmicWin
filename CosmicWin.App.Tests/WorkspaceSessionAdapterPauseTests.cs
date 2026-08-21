using CosmicWin.App.Tests.TestDoubles;
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
}
