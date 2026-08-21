using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// Task 3.31/3.32: wires WU9's <see cref="WindowFilters"/>/<see cref="ExceptionList"/> (spec
/// WE-1/WE-2) onto the real production tracking path -- closes verify-report #21's carried
/// WARNING N1. Filtering applies only at window-creation time; an already-tracked window becoming
/// newly excluded by a later Reload is NOT retroactively removed (WU10 documented scope boundary).
/// </summary>
public sealed class WorkspaceSessionAdapterFilterTests
{
    /// <summary>N1 pin: a <c>Shell_TrayWnd</c>-shaped descriptor must never enter the tree/registry on the real wiring path.</summary>
    [Fact]
    public void WindowAdded_ShellTrayWndShapedDescriptor_VisibleUnownedToolWindow_NeverAddedToTree_OrRegistered()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty, () => false);

        var shellTray = new RecordingWindow(
            new IntPtr(500),
            Rectangle.FromSize(0, 0, 40, 40),
            className: "Shell_TrayWnd",
            processName: "explorer.exe",
            exStyle: WindowStyleFlags.ExToolWindow,
            isOwned: false);
        workspace.RaiseWindowAdded(shellTray);

        Assert.Null(tree.Root);
        Assert.Null(adapter.Root);
        Assert.Equal(0, shellTray.SetPositionCallCount);
        Assert.False(registry.TryGetWindow(shellTray.Handle, out _));
        Assert.False(registry.TryGetLeaf(shellTray.Handle, out _));
    }

    /// <summary>WE-2 through the real composed <see cref="WindowFilters.IsExcluded"/> entry point.</summary>
    [Fact]
    public void WindowAdded_ManuallyExcludedWindow_ProcessNameInExceptionList_NeverAddedToTree_EvenWhenOtherwiseTileable()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var exceptions = new ExceptionList([new ExceptionRule(ExceptionRuleKind.ProcessName, "Spotify.exe")]);
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => exceptions, () => false);

        // Deliberately tileable (fake defaults) so only the manual exception excludes it.
        var spotify = new RecordingWindow(
            new IntPtr(510),
            Rectangle.FromSize(0, 0, 800, 600),
            processName: "Spotify.exe");
        workspace.RaiseWindowAdded(spotify);

        Assert.Null(tree.Root);
        Assert.Null(adapter.Root);
        Assert.Equal(0, spotify.SetPositionCallCount);
        Assert.False(registry.TryGetWindow(spotify.Handle, out _));
    }

    /// <summary>Regression-safety: a normal tileable window (fake defaults) must still be added/arranged.</summary>
    [Fact]
    public void WindowAdded_NormalTileableWindow_StillAddedAndArranged_NoRegression()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty, () => false);

        var normal = new RecordingWindow(new IntPtr(520), Rectangle.FromSize(0, 0, 1920, 1080));
        workspace.RaiseWindowAdded(normal);

        var leaf = Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(new WindowRef(normal.Handle), leaf.Window);
        Assert.Equal(1, normal.SetPositionCallCount);
        Assert.True(registry.TryGetWindow(normal.Handle, out var found));
        Assert.Same(normal, found);
    }

    /// <summary>Removing a window that was never registered (because it was excluded on add) must not throw or corrupt state.</summary>
    [Fact]
    public void WindowRemoved_NeverRegisteredExcludedWindow_IsSafeNoOp()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        using var adapter = new WorkspaceSessionAdapter(
            workspace, tree, registry, () => new Rect(0, 0, 1920, 1080), () => ExceptionList.Empty, () => false);

        var shellTray = new RecordingWindow(
            new IntPtr(530),
            Rectangle.FromSize(0, 0, 40, 40),
            exStyle: WindowStyleFlags.ExToolWindow,
            isOwned: false);
        workspace.RaiseWindowAdded(shellTray);

        var exception = Record.Exception(() => workspace.RaiseWindowRemoved(shellTray));

        Assert.Null(exception);
        Assert.Null(tree.Root);
        Assert.False(registry.TryGetWindow(shellTray.Handle, out _));
    }
}
