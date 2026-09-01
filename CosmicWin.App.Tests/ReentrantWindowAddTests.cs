using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// One announcement of a handle may be in flight while another arrives, and only one leaf may result.
/// </summary>
/// <remarks>
/// <para>
/// Measured on real hardware, Clipchamp opening, with the guard's own state recorded on every
/// announcement:
/// </para>
/// <code>
/// 48.1280  guard state hwnd=0x40944 owner=False leaf=False givenUp=False
/// 48.1292  guard state hwnd=0x40944 owner=False leaf=False givenUp=False
/// 48.1934  added hwnd=0x40944 ...
/// 48.2268  added hwnd=0x40944 ...
/// </code>
/// <para>
/// The <c>added</c> line is written at the END of the handler. Sequential processing would read
/// guard, added, guard, added. Reading both guards first and both additions after proves the two
/// calls were inside the handler AT THE SAME TIME -- the handler re-enters, 1.2 ms apart, on one
/// thread.
/// </para>
/// <para>
/// That is why the existing duplicate guard could not help and never once fired in production. It
/// asks whether the handle is already an owned, registered leaf, and the bookkeeping that would
/// make it so runs LATER in the same method. The second call reaches the question before the first
/// call has answered it, so both see nothing and both build a leaf.
/// </para>
/// <para>
/// The consequence is not a tidy duplicate. Eviction removes ONE leaf per give-up, so announcements
/// outnumbering give-ups leave orphans behind -- measured three times, four announcements against
/// three give-ups and two against one. Those orphans keep their tiles, which squeezed the real
/// window into a quarter of the screen it never got back, and the directional focus walk kept
/// electing them: seven consecutive <c>UntrackedTarget activation=none</c> lines, which is a user
/// unable to move focus by keyboard at all.
/// </para>
/// </remarks>
public sealed class ReentrantWindowAddTests
{
    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1080);

    private sealed record Setup(TreeManager Trees, WindowRegistry Registry, FakeWorkspace Workspace, IDisplay Primary);

    private static Setup OneDisplay()
    {
        var primary = new FakeDisplay(new IntPtr(1), WorkArea, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        return new Setup(new TreeManager([primary], primary, registry), registry, new FakeWorkspace(), primary);
    }

    private static RecordingWindow Window(int handle) =>
        new(new IntPtr(handle), Rectangle.FromSize(0, 0, 400, 300));

    private static int LeavesFor(Setup s, nint handle) =>
        s.Trees.LeavesOn(s.Primary).Count(leaf => leaf.Window.Handle == handle);

    /// <summary>
    /// Re-entry is injected through the focused-leaf lookup, which the handler consults BEFORE it
    /// registers anything -- the same window the real re-entry lands in.
    /// </summary>
    [Fact]
    public void AHandleAnnouncedAgainWhileItsFirstAnnouncementIsStillRunning_BuildsOneLeaf()
    {
        var s = OneDisplay();
        var window = Window(0x40944);
        var reentered = false;

        using var adapter = new MultiMonitorWorkspaceAdapter(
            s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false,
            () =>
            {
                if (!reentered)
                {
                    reentered = true;
                    s.Workspace.RaiseWindowAdded(window);
                }

                return null;
            });

        s.Workspace.RaiseWindowAdded(window);

        Assert.True(reentered, "the re-entrant announcement never fired, so this proves nothing");
        Assert.Equal(1, LeavesFor(s, window.Handle));
    }

    /// <summary>
    /// The consequence, stated as the user saw it: a neighbour must get the whole area back when the
    /// re-announced window is closed. A second leaf nothing can reach keeps its tile forever.
    /// </summary>
    [Fact]
    public void ClosingAWindowThatWasAnnouncedReentrantly_LetsTheNeighbourReclaimTheWholeArea()
    {
        var s = OneDisplay();
        var neighbour = Window(10);
        var window = Window(0x40944);
        var reentered = false;

        using var adapter = new MultiMonitorWorkspaceAdapter(
            s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false,
            () =>
            {
                if (!reentered)
                {
                    reentered = true;
                    s.Workspace.RaiseWindowAdded(window);
                }

                return null;
            });

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(window);
        s.Workspace.RaiseWindowRemoved(window);

        Assert.Equal(0, LeavesFor(s, window.Handle));
        Assert.Equal(WorkArea, neighbour.LastSetPosition);
    }
}
