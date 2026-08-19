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
}
