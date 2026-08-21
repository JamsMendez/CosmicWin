using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// WU17 (closes carried finding W3, spec MM-1/MM-4): the first real production caller of <see
/// cref="TreeManager"/>. Reuses <see cref="WorkspaceSessionAdapter"/>'s extracted <see
/// cref="WorkspaceSessionAdapter.InsertWindow"/>/<see cref="WorkspaceSessionAdapter.RemoveWindow"/>/
/// <see cref="WorkspaceSessionAdapter.IsExcluded"/> statics (so both classes can never drift), but
/// resolves each window's owning monitor via <see cref="TreeManager.ResolveDisplay"/> instead of a
/// single fixed tree.
/// </summary>
public sealed class MultiMonitorWorkspaceAdapterTests
{
    private sealed record Setup(TreeManager Trees, WindowRegistry Registry, FakeWorkspace Workspace, IDisplay Primary, IDisplay Secondary);

    private static FakeDisplay Display(int handle, int left, int top, int width, int height, bool primary = false) =>
        new(new IntPtr(handle), Rectangle.FromSize(left, top, width, height),
            Rectangle.FromSize(left, top, width, height), 1.0, primary);

    private static Setup TwoDisplays()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var secondary = Display(2, 1920, 0, 1280, 720);
        var registry = new WindowRegistry();
        var trees = new TreeManager(new IDisplay[] { primary, secondary }, primary, registry);
        return new Setup(trees, registry, new FakeWorkspace(), primary, secondary);
    }

    private static Setup OneDisplay()
    {
        var primary = Display(1, 0, 0, 1920, 1080, primary: true);
        var registry = new WindowRegistry();
        var trees = new TreeManager(new IDisplay[] { primary }, primary, registry);
        return new Setup(trees, registry, new FakeWorkspace(), primary, primary);
    }

    [Fact]
    public void WindowAdded_OnPrimaryAndSecondary_RouteToOwnTree_AndArrangeWithOwnWorkArea()
    {
        var s = TwoDisplays();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        var onPrimary = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(100, 100, 400, 300));
        var onSecondary = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(2000, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(onPrimary);
        s.Workspace.RaiseWindowAdded(onSecondary);

        s.Trees.TryGetTree(s.Primary, out var primaryTree);
        s.Trees.TryGetTree(s.Secondary, out var secondaryTree);
        Assert.Equal(new WindowRef(onPrimary.Handle), Assert.IsType<LeafNode>(primaryTree!.Root).Window);
        Assert.Equal(new WindowRef(onSecondary.Handle), Assert.IsType<LeafNode>(secondaryTree!.Root).Window);
        Assert.Equal(Rectangle.FromSize(1920, 0, 1280, 720), onSecondary.LastSetPosition);
    }

    [Fact]
    public void WindowRemoved_OnSecondary_ReflowsOnlySecondaryTree_PrimaryUntouched()
    {
        var s = TwoDisplays();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        var onPrimary = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(onPrimary);
        var primarySetCountAfterAdd = onPrimary.SetPositionCallCount;
        var first = new RecordingWindow(new IntPtr(50), Rectangle.FromSize(2000, 100, 400, 300));
        var second = new RecordingWindow(new IntPtr(60), Rectangle.FromSize(2000, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);

        s.Workspace.RaiseWindowRemoved(second);

        s.Trees.TryGetTree(s.Secondary, out var secondaryTree);
        var group = Assert.IsType<GroupNode>(secondaryTree!.Root);
        Assert.Equal(new WindowRef(first.Handle), Assert.IsType<LeafNode>(Assert.Single(group.Children)).Window);
        Assert.Equal(Rectangle.FromSize(1920, 0, 1280, 720), first.LastSetPosition);
        Assert.Equal(primarySetCountAfterAdd, onPrimary.SetPositionCallCount); // primary untouched
    }

    [Fact]
    public void WindowAdded_WhilePaused_DoesNotTrackOrArrange()
    {
        var s = OneDisplay();
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => true);
        var window = new RecordingWindow(new IntPtr(70), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(window);
        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
        Assert.Equal(0, window.SetPositionCallCount);
    }

    [Fact]
    public void WindowAdded_ExcludedWindow_NotTracked()
    {
        var s = OneDisplay();
        var exceptions = new ExceptionList([new ExceptionRule(ExceptionRuleKind.ProcessName, "Excluded.exe")]);
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => exceptions, () => false);
        var window = new RecordingWindow(new IntPtr(80), Rectangle.FromSize(100, 100, 400, 300), processName: "Excluded.exe");
        s.Workspace.RaiseWindowAdded(window);
        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
    }

    // V11-W2 semantics (decision #64), re-proven here: removal always happens, only reflow is gated.
    [Fact]
    public void WindowRemoved_WhilePaused_RemovesFromRegistry_ButSkipsReflow()
    {
        var s = OneDisplay();
        var paused = false;
        using var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => paused);
        var first = new RecordingWindow(new IntPtr(90), Rectangle.FromSize(100, 100, 400, 300));
        var second = new RecordingWindow(new IntPtr(100), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);
        var firstSetCountBeforePause = first.SetPositionCallCount;

        paused = true;
        s.Workspace.RaiseWindowRemoved(second);

        Assert.False(s.Registry.TryGetLeaf(second.Handle, out _));
        Assert.Equal(firstSetCountBeforePause, first.SetPositionCallCount);
    }

    [Fact]
    public void Dispose_UnsubscribesFromWorkspaceEvents_LaterAddIsIgnored()
    {
        var s = OneDisplay();
        var adapter = new MultiMonitorWorkspaceAdapter(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false);
        adapter.Dispose();
        var window = new RecordingWindow(new IntPtr(110), Rectangle.FromSize(100, 100, 400, 300));
        s.Workspace.RaiseWindowAdded(window);
        s.Trees.TryGetTree(s.Primary, out var tree);
        Assert.Null(tree!.Root);
    }
}
