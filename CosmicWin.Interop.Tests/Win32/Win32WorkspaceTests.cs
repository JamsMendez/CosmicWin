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
    /// Reported from real use: dragging a tiled window showed a flickering ghost while
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

    /// <summary>
    /// Which of the two it was survives onto the event. The drop and an app moving itself are the
    /// same fact about geometry and a completely different one about intent -- only the drop is the
    /// user answering "how big should this be" -- and the App layer resizes the tree from exactly
    /// one of them.
    /// </summary>
    [Fact]
    public void WindowBoundsChanged_AtTheDropOfADrag_IsFlaggedAsTheUsersOwnGesture()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "resized", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var gestures = new List<bool>();
        workspace.WindowBoundsChanged += (_, e) => gestures.Add(e.IsUserGesture);
        workspace.Open();

        source.SimulateMoveSizeStart(new IntPtr(1));
        source.SimulateWindowMovedWithEvent(new IntPtr(1), Rectangle.FromSize(0, 0, 520, 300));
        source.SimulateMoveSizeEnd(new IntPtr(1));

        Assert.True(Assert.Single(gestures));
    }

    /// <summary>The mirror: a window that moves on its own carries no such claim.</summary>
    [Fact]
    public void WindowBoundsChanged_OutsideADrag_IsNotFlaggedAsAUserGesture()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "self-mover", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var gestures = new List<bool>();
        workspace.WindowBoundsChanged += (_, e) => gestures.Add(e.IsUserGesture);
        workspace.Open();

        source.SimulateWindowMovedWithEvent(new IntPtr(1), Rectangle.FromSize(0, 0, 520, 300));

        Assert.False(Assert.Single(gestures));
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

    /// <summary>
    /// Measured on real hardware: switching virtual desktops rearranged the windows on
    /// the desktop returned to. The chain was verifiable in code -- DWM CLOAKS every window on the
    /// desktop being left, <c>IsTrackable</c> rejects a cloaked window, so the enumeration stops
    /// listing it, and <see cref="Win32Workspace.Poll"/> read "absent from the enumeration" as
    /// "destroyed". The whole tree was dismantled on the way out and rebuilt in enumeration order
    /// on the way back.
    /// <para>
    /// Not enumerable is not the same as gone. A window that still answers <c>TryGetWindowInfo</c>
    /// is alive; only its visibility changed, and reporting it as removed throws away a layout the
    /// user expects to find waiting for them.
    /// </para>
    /// </summary>
    [Fact]
    public void Poll_AWindowThatStoppedBeingEnumerableButStillExists_IsNotReportedAsRemoved()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "stays", Rectangle.FromSize(0, 0, 400, 300));
        source.SeedExistingWindow(new IntPtr(2), "cloaked", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var removed = new List<nint>();
        workspace.WindowRemoved += (_, e) => removed.Add(e.Window.Handle);
        workspace.Open();

        source.HideFromEnumeration(new IntPtr(2));
        workspace.Poll();

        Assert.Empty(removed);
        Assert.Equal(2, workspace.Snapshot.Count);
        Assert.True(workspace.Snapshot.Single(w => w.Handle == new IntPtr(2)).IsAlive);
    }

    /// <summary>The other half: a window that is genuinely gone must still be reported, or a closed window would haunt the tree forever.</summary>
    [Fact]
    public void Poll_AWindowThatNoLongerExists_IsStillReportedAsRemoved()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "stays", Rectangle.FromSize(0, 0, 400, 300));
        source.SeedExistingWindow(new IntPtr(2), "closes", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var removed = new List<nint>();
        workspace.WindowRemoved += (_, e) => removed.Add(e.Window.Handle);
        workspace.Open();

        source.SimulateWindowDestroyedSilently(new IntPtr(2));
        workspace.Poll();

        Assert.Equal(new IntPtr(2), Assert.Single(removed));
    }

    /// <summary>
    /// Reported from real use with Discord: closing it left its slot reserved and the focus border
    /// drawn on it. An application that lives in the notification area does not DESTROY its window
    /// on close, it hides it -- <c>ShowWindow(SW_HIDE)</c> -- so no destroy event is ever raised and
    /// the window keeps answering every liveness question that is asked about it.
    /// <para>
    /// The hide is the close, as far as the layout is concerned: nothing is drawn into that tile
    /// any more, so nothing may keep claiming it.
    /// </para>
    /// </summary>
    [Fact]
    public void HookDeliversHideEvent_RaisesWindowRemoved_AndLeavesSnapshot()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(3), "Discord", Rectangle.FromSize(0, 0, 500, 500));
        using var workspace = new Win32Workspace(source);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowRemoved += (_, e) => received = e;

        source.SimulateWindowHiddenWithEvent(new IntPtr(3));

        Assert.NotNull(received);
        Assert.Equal(new IntPtr(3), received!.Window.Handle);
        Assert.DoesNotContain(workspace.Snapshot, w => w.Handle == new IntPtr(3));
    }

    /// <summary>
    /// The reconciliation half of the same defect. A missed hide is worse than a missed destroy: the
    /// window stays alive forever, so <see cref="Win32Workspace.Poll"/>'s "still answers
    /// TryGetWindowInfo" test keeps it in the tree for the rest of the session.
    /// </summary>
    /// <remarks>
    /// Visibility is what separates this from the cloaked window above it, and it is a real
    /// distinction rather than a convenient one: DWM cloaking leaves <c>WS_VISIBLE</c> set -- the
    /// window is still shown, just not on the desktop being looked at -- while
    /// <c>ShowWindow(SW_HIDE)</c> clears it. Asking "is it enumerable" cannot tell them apart;
    /// asking "is it visible" can.
    /// </remarks>
    [Fact]
    public void Poll_AWindowHiddenToTheNotificationArea_IsReportedAsRemoved()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "stays", Rectangle.FromSize(0, 0, 400, 300));
        source.SeedExistingWindow(new IntPtr(2), "Discord", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var removed = new List<nint>();
        workspace.WindowRemoved += (_, e) => removed.Add(e.Window.Handle);
        workspace.Open();

        source.SimulateWindowHiddenSilently(new IntPtr(2));
        workspace.Poll();

        Assert.Equal(new IntPtr(2), Assert.Single(removed));
        Assert.DoesNotContain(workspace.Snapshot, w => w.Handle == new IntPtr(2));
    }

    /// <summary>
    /// Reported from real use: selecting text in Chrome and picking Emoji opens the Windows emoji
    /// panel, which takes a tile. Dismissing it left that tile claimed for the rest of the session,
    /// and walking to another desktop and back drew a focus border on a window that was not there.
    /// <para>
    /// The panel is never destroyed and never hidden -- it CLOAKS itself, which is the one
    /// disappearance every test above deliberately refuses to act on. A cloaked window keeps its
    /// HWND and keeps <c>WS_VISIBLE</c> set, so "does it still answer" and "is it still visible"
    /// both say yes, and absence from the enumeration alone cannot tell this apart from the user
    /// walking to another desktop.
    /// </para>
    /// <para>
    /// The DESKTOP is what tells them apart, and it is the only thing that does. A window cloaked
    /// while it is still filed under the desktop being looked at was dismissed; a window cloaked
    /// because the user left is alive somewhere else.
    /// </para>
    /// </summary>
    [Fact]
    public void Poll_AWindowCloakedOnTheDesktopTheUserIsWatching_IsReportedAsRemoved()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "stays", Rectangle.FromSize(0, 0, 400, 300));
        source.SeedExistingWindow(new IntPtr(2), "emoji panel", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var removed = new List<nint>();
        workspace.WindowRemoved += (_, e) => removed.Add(e.Window.Handle);
        workspace.Open();

        source.HideFromEnumeration(new IntPtr(2));
        source.PlaceOnCurrentDesktop(new IntPtr(2));
        workspace.Poll();
        workspace.Poll();

        Assert.Equal(new IntPtr(2), Assert.Single(removed));
        Assert.DoesNotContain(workspace.Snapshot, w => w.Handle == new IntPtr(2));
    }

    /// <summary>
    /// One reading is not enough to act on, and this is why. Switching desktops cloaks the windows
    /// being left AND moves the current desktop, and those are two separate steps: a pass landing
    /// between them would see a window cloaked while its desktop is still the current one --
    /// indistinguishable, in that instant, from a dismissal. Acting on a single pass would put the
    /// dismantle-the-whole-tree-on-a-desktop-switch regression back for anyone whose timing is
    /// unlucky, which is far too high a price for reclaiming a tile two seconds sooner.
    /// <para>
    /// So the fact has to hold across two consecutive passes. A desktop switch settles well inside
    /// one interval, so nothing transient survives; a dismissed window answers the same way for as
    /// long as it exists, so the only cost is that its tile is reclaimed one pass later.
    /// </para>
    /// </summary>
    [Fact]
    public void Poll_AWindowCloakedOnTheCurrentDesktopForOneSinglePass_IsNotReportedAsRemoved()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "stays", Rectangle.FromSize(0, 0, 400, 300));
        source.SeedExistingWindow(new IntPtr(2), "mid-switch", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var removed = new List<nint>();
        workspace.WindowRemoved += (_, e) => removed.Add(e.Window.Handle);
        workspace.Open();

        source.HideFromEnumeration(new IntPtr(2));
        source.PlaceOnCurrentDesktop(new IntPtr(2));
        workspace.Poll();

        Assert.Empty(removed);
        Assert.Contains(workspace.Snapshot, w => w.Handle == new IntPtr(2));
    }

    /// <summary>
    /// The regression this whole mechanism is built around, stated as its own fact: a window cloaked
    /// because the user walked to another desktop must survive any number of passes. It is alive, it
    /// is somewhere, and the layout it belongs to has to be waiting when the user walks back.
    /// </summary>
    [Fact]
    public void Poll_AWindowCloakedByLeavingItsDesktop_IsNeverReportedAsRemoved()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "stays", Rectangle.FromSize(0, 0, 400, 300));
        source.SeedExistingWindow(new IntPtr(2), "on desktop 2", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var removed = new List<nint>();
        workspace.WindowRemoved += (_, e) => removed.Add(e.Window.Handle);
        workspace.Open();

        source.HideFromEnumeration(new IntPtr(2));
        source.PlaceOnAnotherDesktop(new IntPtr(2));
        workspace.Poll();
        workspace.Poll();
        workspace.Poll();

        Assert.Empty(removed);
        Assert.True(workspace.Snapshot.Single(w => w.Handle == new IntPtr(2)).IsAlive);
    }

    /// <summary>
    /// Fail closed. The shell declines to place plenty of windows, and a refusal is not a "yes" --
    /// reading it as one would report a living window as closed on the strength of an error. A tile
    /// held by a window nobody can place is a smaller defect than a window torn out of the tree
    /// while the user is still using it.
    /// </summary>
    [Fact]
    public void Poll_AWindowTheShellWillNotPlace_IsLeftAlone()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "stays", Rectangle.FromSize(0, 0, 400, 300));
        source.SeedExistingWindow(new IntPtr(2), "unplaceable", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var removed = new List<nint>();
        workspace.WindowRemoved += (_, e) => removed.Add(e.Window.Handle);
        workspace.Open();

        source.HideFromEnumeration(new IntPtr(2));
        source.RefuseToPlace(new IntPtr(2));
        workspace.Poll();
        workspace.Poll();
        workspace.Poll();

        Assert.Empty(removed);
        Assert.True(workspace.Snapshot.Single(w => w.Handle == new IntPtr(2)).IsAlive);
    }

    /// <summary>
    /// Two passes means two CONSECUTIVE passes. A window that looks dismissed once, is then placed
    /// somewhere else, and later looks dismissed again has not been dismissed twice -- it has been
    /// dismissed once, most recently, and the count has to start over. Without this the two-pass
    /// guard degrades into "any two readings ever", which a long enough session guarantees.
    /// </summary>
    [Fact]
    public void Poll_AWindowThatLooksDismissedThenDoesNot_StartsCountingOver()
    {
        var source = new FakeNativeWindowSource();
        source.SeedExistingWindow(new IntPtr(1), "stays", Rectangle.FromSize(0, 0, 400, 300));
        source.SeedExistingWindow(new IntPtr(2), "flickers", Rectangle.FromSize(0, 0, 400, 300));
        using var workspace = new Win32Workspace(source);
        var removed = new List<nint>();
        workspace.WindowRemoved += (_, e) => removed.Add(e.Window.Handle);
        workspace.Open();

        source.HideFromEnumeration(new IntPtr(2));

        source.PlaceOnCurrentDesktop(new IntPtr(2));
        workspace.Poll();

        source.PlaceOnAnotherDesktop(new IntPtr(2));
        workspace.Poll();

        source.PlaceOnCurrentDesktop(new IntPtr(2));
        workspace.Poll();

        Assert.Empty(removed);
        Assert.Contains(workspace.Snapshot, w => w.Handle == new IntPtr(2));
    }
}
