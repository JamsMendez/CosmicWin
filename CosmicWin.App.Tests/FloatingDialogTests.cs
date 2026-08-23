using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// A modal dialog is centred where it opens, and nothing else is ever touched.
/// </summary>
/// <remarks>
/// <para>
/// Requested after live use, matching the reference implementation: a dialog should arrive in the
/// middle of the screen rather than wherever the application last left it.
/// </para>
/// <para>
/// This needed its own path into CosmicWin. <c>Win32NativeWindowSource.IsTrackable</c> is
/// <c>!hasOwner &amp;&amp; !isCloaked</c>, and a modal always has an owner, so <c>WindowAdded</c>
/// never fires for one -- the tiling pipeline has never seen a dialog and still does not. The
/// watcher below reports every window being SHOWN, ungated, and the narrowing happens here.
/// </para>
/// <para>
/// Which makes the negative facts the important ones. The same event delivers every tooltip,
/// dropdown and context menu on the desktop, and moving one of those would drag it out from under
/// the pointer that opened it.
/// </para>
/// </remarks>
public sealed class FloatingDialogTests
{
    private const uint Dialog = WindowStyleFlags.SystemMenu;
    private const uint Tileable =
        WindowStyleFlags.SystemMenu | WindowStyleFlags.MaximizeBox | WindowStyleFlags.MinimizeBox;

    private sealed class FakeWindowShownWatcher : IWindowShownWatcher
    {
        public event EventHandler<WindowEventArgs>? WindowShown;

        public int OpenCallCount { get; private set; }

        public void Open() => OpenCallCount++;

        public void Raise(IWindow window) => WindowShown?.Invoke(this, new WindowEventArgs(window));

        public void Dispose()
        {
        }
    }

    private static FakeDisplay Display() =>
        new(new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);

    private static (FakeWindowShownWatcher Watcher, FloatingDialogAdapter Adapter, TreeManager Trees) Build(
        Func<bool>? isPaused = null, ExceptionList? exceptions = null)
    {
        var display = Display();
        var trees = new TreeManager([display], display, new WindowRegistry());
        var watcher = new FakeWindowShownWatcher();
        var adapter = new FloatingDialogAdapter(
            watcher, trees, () => exceptions ?? ExceptionList.Empty, isPaused ?? (() => false));

        return (watcher, adapter, trees);
    }

    [Fact]
    public void AModalDialog_IsCentredOnTheWorkAreaItOpenedOn()
    {
        var (watcher, adapter, _) = Build();
        using (adapter)
        {
            var dialog = new RecordingWindow(
                new IntPtr(0x901), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);

            watcher.Raise(dialog);

            Assert.Equal(1, dialog.SetPositionCallCount);
            Assert.Equal(Rectangle.FromSize(760, 440, 400, 200), dialog.LastSetPosition);
        }
    }

    /// <summary>An ordinary application window belongs to the tiling engine, which has already placed it.</summary>
    [Fact]
    public void ATileableWindow_IsLeftWhereItIs()
    {
        var (watcher, adapter, _) = Build();
        using (adapter)
        {
            var tiled = new RecordingWindow(
                new IntPtr(0x902), Rectangle.FromSize(0, 0, 960, 1080), style: Tileable);

            watcher.Raise(tiled);

            Assert.Equal(0, tiled.SetPositionCallCount);
        }
    }

    /// <summary>
    /// The expensive mistake: a dropdown or tooltip is owned and has no maximise button either, and
    /// centring one would move it away from the control that opened it.
    /// </summary>
    [Fact]
    public void AToolWindow_IsLeftWhereItIs()
    {
        var (watcher, adapter, _) = Build();
        using (adapter)
        {
            var popup = new RecordingWindow(
                new IntPtr(0x903), Rectangle.FromSize(500, 500, 200, 80),
                style: Dialog, exStyle: WindowStyleFlags.ExToolWindow, isOwned: true);

            watcher.Raise(popup);

            Assert.Equal(0, popup.SetPositionCallCount);
        }
    }

    /// <summary>Pause means hands off everything, exactly as it does for tiling.</summary>
    [Fact]
    public void WhilePaused_NothingIsMoved()
    {
        var (watcher, adapter, _) = Build(isPaused: () => true);
        using (adapter)
        {
            var dialog = new RecordingWindow(
                new IntPtr(0x904), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);

            watcher.Raise(dialog);

            Assert.Equal(0, dialog.SetPositionCallCount);
        }
    }

    /// <summary>
    /// A manual exception says "leave this alone", and it must mean that here too. The automatic
    /// tiling filter cannot express it -- every dialog is auto-excluded already -- so this path has
    /// to consult the user's list itself or the setting would silently not apply to dialogs.
    /// </summary>
    [Fact]
    public void AWindowOnTheUsersExceptionList_IsLeftWhereItIs()
    {
        var excluded = new ExceptionList([new ExceptionRule(ExceptionRuleKind.ProcessName, "installer")]);
        var (watcher, adapter, _) = Build(exceptions: excluded);
        using (adapter)
        {
            var dialog = new RecordingWindow(
                new IntPtr(0x905), Rectangle.FromSize(30, 40, 400, 200),
                processName: "installer", style: Dialog, isOwned: true);

            watcher.Raise(dialog);

            Assert.Equal(0, dialog.SetPositionCallCount);
        }
    }

    /// <summary>
    /// Centring must not put the dialog in the tree. It has no siblings, divides no region, and
    /// leaves nothing to reflow when it closes -- the whole reason it is a separate path.
    /// </summary>
    [Fact]
    public void ACentredDialog_NeverEntersTheLayoutTree()
    {
        var (watcher, adapter, trees) = Build();
        using (adapter)
        {
            var dialog = new RecordingWindow(
                new IntPtr(0x906), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);

            watcher.Raise(dialog);

            Assert.True(trees.TryGetTree(trees.Primary, out var tree));
            Assert.Null(tree!.Root);
        }
    }

    /// <summary>A dialog that refuses to be moved is left alone, not fought in a loop.</summary>
    [Fact]
    public void ADialogThatRefusesToBeMoved_IsAskedExactlyOnce()
    {
        var (watcher, adapter, _) = Build();
        using (adapter)
        {
            var stubborn = new RecordingWindow(
                new IntPtr(0x907), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);
            stubborn.FailNextSetPosition();

            watcher.Raise(stubborn);
            watcher.Raise(stubborn);

            Assert.Equal(1, stubborn.SetPositionCallCount);
        }
    }

    [Fact]
    public void DisposingUnsubscribes()
    {
        var (watcher, adapter, _) = Build();
        var dialog = new RecordingWindow(
            new IntPtr(0x908), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);

        adapter.Dispose();
        watcher.Raise(dialog);

        Assert.Equal(0, dialog.SetPositionCallCount);
    }

    /// <summary>
    /// A dialog has no tile to travel between, so the move chord SNAPS it. Left takes the left half
    /// of the work area it opened on.
    /// </summary>
    [Fact]
    public void SnappingAKnownDialogLeft_TakesTheLeftHalf()
    {
        var (watcher, adapter, _) = Build();
        using (adapter)
        {
            var dialog = new RecordingWindow(
                new IntPtr(0xA01), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);
            watcher.Raise(dialog);

            Assert.True(adapter.TrySnap(dialog.Handle, Direction.Left));

            Assert.Equal(Rectangle.FromSize(0, 0, 960, 1080), dialog.LastSetPosition);
        }
    }

    /// <summary>
    /// Down returns it to the size it OPENED at, not the half-screen it currently wears -- which is
    /// why the adapter has to remember that size from the moment it first saw the window.
    /// </summary>
    [Fact]
    public void SnappingDownAfterASnap_RestoresTheSizeItOpenedAt()
    {
        var (watcher, adapter, _) = Build();
        using (adapter)
        {
            var dialog = new RecordingWindow(
                new IntPtr(0xA02), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);
            watcher.Raise(dialog);
            adapter.TrySnap(dialog.Handle, Direction.Left);

            Assert.True(adapter.TrySnap(dialog.Handle, Direction.Down));

            Assert.Equal(Rectangle.FromSize(760, 440, 400, 200), dialog.LastSetPosition);
        }
    }

    /// <summary>
    /// A handle the adapter has never centred is not its business. Saying so with <c>false</c> is
    /// what lets the caller fall through to its own no-op instead of guessing.
    /// </summary>
    [Fact]
    public void SnappingAnUnknownHandle_IsRefused()
    {
        var (_, adapter, _) = Build();
        using (adapter)
        {
            Assert.False(adapter.TrySnap(new IntPtr(0xDEAD), Direction.Left));
        }
    }

    /// <summary>Pause means hands off here too.</summary>
    [Fact]
    public void SnappingWhilePaused_IsRefused()
    {
        var paused = false;
        var (watcher, adapter, _) = Build(isPaused: () => paused);
        using (adapter)
        {
            var dialog = new RecordingWindow(
                new IntPtr(0xA03), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);
            watcher.Raise(dialog);
            var afterCentring = dialog.SetPositionCallCount;

            paused = true;

            Assert.False(adapter.TrySnap(dialog.Handle, Direction.Left));
            Assert.Equal(afterCentring, dialog.SetPositionCallCount);
        }
    }

    /// <summary>A dialog that has closed is no longer snappable, and must not be resurrected by a chord.</summary>
    [Fact]
    public void SnappingAClosedDialog_IsRefused()
    {
        var (watcher, adapter, _) = Build();
        using (adapter)
        {
            var dialog = new RecordingWindow(
                new IntPtr(0xA04), Rectangle.FromSize(30, 40, 400, 200), style: Dialog, isOwned: true);
            watcher.Raise(dialog);
            dialog.Kill();

            Assert.False(adapter.TrySnap(dialog.Handle, Direction.Left));
        }
    }
}
