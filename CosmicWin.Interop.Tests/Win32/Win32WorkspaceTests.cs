using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// WT-1: enumerate at startup, track create/destroy/move/resize via the hook event source,
/// and reconcile via <c>Poll()</c> when the hook misses an event.
/// </summary>
public class Win32WorkspaceTests
{
    [Fact]
    public void Open_EnumeratesExistingWindows_IntoSnapshot()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(1), "Notepad", Rectangle.FromSize(0, 0, 800, 600));
        var workspace = new Win32Workspace(native);

        workspace.Open();

        Assert.True(workspace.IsOpen);
        Assert.Contains(workspace.Snapshot, w => w.Handle == new IntPtr(1) && w.Title == "Notepad");
    }

    [Fact]
    public void HookDeliversCreateEvent_RaisesWindowAdded_AndAppearsInSnapshot()
    {
        var native = new FakeNativeWindowSource();
        var workspace = new Win32Workspace(native);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowAdded += (_, e) => received = e;

        native.SimulateWindowCreatedWithEvent(new IntPtr(2), "Calculator", Rectangle.FromSize(0, 0, 300, 400));

        Assert.NotNull(received);
        Assert.Equal(new IntPtr(2), received!.Window.Handle);
        Assert.Contains(workspace.Snapshot, w => w.Handle == new IntPtr(2));
    }

    [Fact]
    public void HookDeliversDestroyEvent_RaisesWindowRemoved_AndLeavesSnapshot()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(3), "Paint", Rectangle.FromSize(0, 0, 500, 500));
        var workspace = new Win32Workspace(native);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowRemoved += (_, e) => received = e;

        native.SimulateWindowDestroyedWithEvent(new IntPtr(3));

        Assert.NotNull(received);
        Assert.Equal(new IntPtr(3), received!.Window.Handle);
        Assert.DoesNotContain(workspace.Snapshot, w => w.Handle == new IntPtr(3));
    }

    [Fact]
    public void HookDeliversMoveOrResizeEvent_RaisesWindowBoundsChanged_WithNewBounds()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(4), "Explorer", Rectangle.FromSize(0, 0, 400, 300));
        var workspace = new Win32Workspace(native);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowBoundsChanged += (_, e) => received = e;

        native.SimulateWindowMovedWithEvent(new IntPtr(4), Rectangle.FromSize(100, 100, 640, 480));

        Assert.NotNull(received);
        Assert.Equal(Rectangle.FromSize(100, 100, 640, 480), received!.Window.Bounds);
    }

    [Fact]
    public void HookMissesDestroyEvent_PollDetectsStaleWindow_AndRemovesIt()
    {
        // Spec WT-1 scenario "Hook misses an event": SetWinEventHook fails to deliver a
        // destroy event; the next polling pass detects the stale window and removes it.
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(5), "Stale", Rectangle.FromSize(0, 0, 200, 200));
        var workspace = new Win32Workspace(native);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowRemoved += (_, e) => received = e;

        native.SimulateWindowDestroyedSilently(new IntPtr(5));
        Assert.Null(received); // no hook event delivered yet — workspace still believes it's alive
        Assert.Contains(workspace.Snapshot, w => w.Handle == new IntPtr(5));

        workspace.Poll();

        Assert.NotNull(received);
        Assert.Equal(new IntPtr(5), received!.Window.Handle);
        Assert.DoesNotContain(workspace.Snapshot, w => w.Handle == new IntPtr(5));
    }

    [Fact]
    public void HookMissesCreateEvent_PollDetectsNewWindow_AndAddsIt()
    {
        // Symmetric case: a create can be missed too, and Poll()'s full reconciliation
        // (not just a destroy-only sweep) must pick it up just the same.
        var native = new FakeNativeWindowSource();
        var workspace = new Win32Workspace(native);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowAdded += (_, e) => received = e;

        native.SimulateWindowCreatedSilently(new IntPtr(6), "MissedHook", Rectangle.FromSize(0, 0, 100, 100));
        Assert.Null(received);
        Assert.DoesNotContain(workspace.Snapshot, w => w.Handle == new IntPtr(6));

        workspace.Poll();

        Assert.NotNull(received);
        Assert.Contains(workspace.Snapshot, w => w.Handle == new IntPtr(6));
    }

    [Fact]
    public void HookMissesChangeEvent_PollRefreshesWindow_AndRaisesOnlyForChangedBounds()
    {
        var native = new FakeNativeWindowSource();
        var handle = new IntPtr(8);
        native.SeedExistingWindow(handle, "Before", Rectangle.FromSize(0, 0, 100, 100));
        var workspace = new Win32Workspace(native);
        workspace.Open();
        var eventCount = 0;
        workspace.WindowBoundsChanged += (_, _) => eventCount++;

        var changedBounds = Rectangle.FromSize(10, 20, 300, 400);
        native.SimulateWindowChangedSilently(handle, "After", changedBounds);
        workspace.Poll();
        workspace.Poll();

        var window = Assert.Single(workspace.Snapshot);
        Assert.Equal("After", window.Title);
        Assert.Equal(changedBounds, window.Bounds);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void Poll_BeforeOpen_Throws()
    {
        var workspace = new Win32Workspace(new FakeNativeWindowSource());

        Assert.Throws<InvalidOperationException>(() => workspace.Poll());
    }

    [Fact]
    public void Dispose_UnsubscribesFromHook_SoLaterEventsAreIgnored()
    {
        var native = new FakeNativeWindowSource();
        var workspace = new Win32Workspace(native);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowAdded += (_, e) => received = e;

        workspace.Dispose();
        native.SimulateWindowCreatedWithEvent(new IntPtr(7), "TooLate", Rectangle.FromSize(0, 0, 100, 100));

        Assert.Null(received);
    }

    /// <summary>
    /// Reported from real use 2026-08-22: dragging a tiled window showed a flickering ghost while
    /// the window itself never moved, and dropping it changed nothing. The snap-back was correct
    /// but relentless -- Windows raises EVENT_OBJECT_LOCATIONCHANGE for every intermediate frame of
    /// a drag, so the tree re-applied the tile dozens of times per second and fought the gesture.
    /// <para>
    /// Decision #80's own wording is "snaps back on DROP", and there was no drop detection at all.
    /// A drag is now bracketed by EVENT_SYSTEM_MOVESIZESTART/END, and everything between them is
    /// one gesture: no bounds event escapes until the user lets go, and then exactly one does.
    /// </para>
    /// </summary>
    [Fact]
    public void WindowBoundsChanged_DuringADrag_IsWithheldUntilTheDrop()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "dragged", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var settled = new List<Rectangle>();
        workspace.WindowBoundsChanged += (_, e) => settled.Add(e.Window.Bounds);
        workspace.Open();

        source.SimulateMoveSizeStart(new IntPtr(1));
        source.SimulateWindowMovedWithEvent(new IntPtr(1), Rectangle.FromSize(10, 10, 400, 300));
        source.SimulateWindowMovedWithEvent(new IntPtr(1), Rectangle.FromSize(50, 40, 400, 300));
        source.SimulateWindowMovedWithEvent(new IntPtr(1), Rectangle.FromSize(120, 90, 400, 300));

        Assert.Empty(settled);

        source.SimulateMoveSizeEnd(new IntPtr(1));

        var reported = Assert.Single(settled);
        Assert.Equal(Rectangle.FromSize(120, 90, 400, 300), reported);
    }

    /// <summary>A move the user did NOT drag -- an app repositioning itself -- is still reported at once.</summary>
    [Fact]
    public void WindowBoundsChanged_OutsideADrag_IsStillReportedImmediately()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "self-mover", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var settled = new List<Rectangle>();
        workspace.WindowBoundsChanged += (_, e) => settled.Add(e.Window.Bounds);
        workspace.Open();

        source.SimulateWindowMovedWithEvent(new IntPtr(1), Rectangle.FromSize(7, 7, 400, 300));

        Assert.Equal(Rectangle.FromSize(7, 7, 400, 300), Assert.Single(settled));
    }
}
