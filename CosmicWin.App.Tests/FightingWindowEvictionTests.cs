using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// A window that will not stay where it is put is given up on, rather than fought forever.
/// </summary>
/// <remarks>
/// <para>
/// Measured with Clipchamp: it opens three top-level windows, and one of them --
/// <c>InputNonClientPointerSource</c>, 138x31, the OS's own input plumbing for a custom title bar --
/// accepted every reposition and reported itself somewhere else every time. Placed at x=1724, it
/// read back x=4307, off the right edge of a 3440-wide desktop, forever. 31,759 reflows of one
/// handle in two minutes and a 3.8 MB trace file, with the machine's CPU paying for all of it.
/// Photoshop produced the identical shape from a 108x24 window of class <c>Button</c>.
/// </para>
/// <para>
/// Two guards already existed and neither could see this. The zero-area rule in
/// <c>WindowFilters</c> was written for Notepad's version of the same window, which had literally
/// no area; these have real area. <c>CanReposition</c> catches a window that REFUSES -- this one
/// accepts, reports success, and drifts, which is indistinguishable from compliance at the moment
/// of the call.
/// </para>
/// <para>
/// So the signature is not a size, a class name, or a refusal. It is NON-CONVERGENCE: the same tile
/// handed to the same window over and over, never reached. That is measurable without knowing
/// anything about the window, which is what makes it the general guard -- a list of class names is
/// always one release behind, and this catches the next one nobody has met yet.
/// </para>
/// <para>
/// Eviction has to refuse re-admission too. The window stays visible, trackable and un-excluded, so
/// the very next reconciliation pass would adopt it straight back and restart the storm.
/// </para>
/// </remarks>
public sealed class FightingWindowEvictionTests
{
    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1080);

    /// <summary>Where the fighter insists on living: nowhere near any tile it is offered.</summary>
    private static readonly Rectangle Elsewhere = Rectangle.FromSize(5000, 5000, 960, 1080);

    private sealed record Setup(TreeManager Trees, WindowRegistry Registry, FakeWorkspace Workspace, IDisplay Primary);

    private static Setup OneDisplay()
    {
        var primary = new FakeDisplay(new IntPtr(1), WorkArea, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        return new Setup(new TreeManager([primary], primary, registry), registry, new FakeWorkspace(), primary);
    }

    private static MultiMonitorWorkspaceAdapter Adapter(Setup s) =>
        new(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);

    private static RecordingWindow Window(int handle) =>
        new(new IntPtr(handle), Rectangle.FromSize(0, 0, 400, 300));

    private static bool InTree(Setup s, nint handle) =>
        s.Trees.LeavesOn(s.Primary).Any(leaf => leaf.Window.Handle == handle);

    /// <summary>Drives the fight the way the OS does: the window drifts, so a bounds event arrives.</summary>
    private static void Fight(Setup s, RecordingWindow fighter, int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            s.Workspace.RaiseWindowBoundsChanged(fighter);
        }
    }

    /// <summary>The storm, bounded. Enough rounds to be certain, still finite.</summary>
    private const int EnoughRounds = 60;

    [Fact]
    public void AWindowThatNeverReachesItsTile_IsEvictedFromTheTree()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var settled = Window(10);
        var fighter = Window(20);
        s.Workspace.RaiseWindowAdded(settled);
        s.Workspace.RaiseWindowAdded(fighter);
        fighter.SnapsBackTo = Elsewhere;

        Fight(s, fighter, EnoughRounds);

        Assert.False(InTree(s, fighter.Handle));
    }

    /// <summary>The point of evicting: the survivor gets the space back.</summary>
    [Fact]
    public void OnceTheFighterIsEvicted_TheSurvivorReclaimsTheWholeWorkArea()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var settled = Window(10);
        var fighter = Window(20);
        s.Workspace.RaiseWindowAdded(settled);
        s.Workspace.RaiseWindowAdded(fighter);
        fighter.SnapsBackTo = Elsewhere;

        Fight(s, fighter, EnoughRounds);

        Assert.Equal(WorkArea, settled.LastSetPosition);
    }

    /// <summary>
    /// Eviction alone would be a slower storm. The window is still visible and still tileable, so
    /// the next pass would take it straight back.
    /// </summary>
    [Fact]
    public void AnEvictedFighter_IsNotAdmittedAgain()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var settled = Window(10);
        var fighter = Window(20);
        s.Workspace.RaiseWindowAdded(settled);
        s.Workspace.RaiseWindowAdded(fighter);
        fighter.SnapsBackTo = Elsewhere;
        Fight(s, fighter, EnoughRounds);

        s.Workspace.RaiseWindowAdded(fighter);

        Assert.False(InTree(s, fighter.Handle));
        Assert.Equal(WorkArea, settled.LastSetPosition);
    }

    /// <summary>
    /// The fight is BOUNDED, not merely detected. Left running, the real one issued hundreds of
    /// repositions a second for as long as the app was open.
    /// </summary>
    [Fact]
    public void AFighter_StopsBeingRepositionedOnceItIsGivenUpOn()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var settled = Window(10);
        var fighter = Window(20);
        s.Workspace.RaiseWindowAdded(settled);
        s.Workspace.RaiseWindowAdded(fighter);
        fighter.SnapsBackTo = Elsewhere;

        Fight(s, fighter, EnoughRounds);
        var afterEviction = fighter.SetPositionCallCount;
        Fight(s, fighter, EnoughRounds);

        Assert.Equal(afterEviction, fighter.SetPositionCallCount);
    }

    /// <summary>
    /// The guard that keeps this honest. An ordinary window generates bounds events all day --
    /// every reflow of every neighbour is one -- and none of them is a fight.
    /// </summary>
    [Fact]
    public void AWindowThatLandsWhereItIsPut_IsNeverEvicted()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var first = Window(10);
        var second = Window(20);
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);

        Fight(s, second, EnoughRounds);

        Assert.True(InTree(s, second.Handle));
        Assert.True(InTree(s, first.Handle));
    }

    /// <summary>
    /// A window that drifts a few times and then settles is forgiven. Applications move themselves
    /// while they start up, and holding an early wobble against one forever would evict windows
    /// that went on to behave perfectly.
    /// </summary>
    [Fact]
    public void AWindowThatSettlesBeforeTheLimit_KeepsItsPlace()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var first = Window(10);
        var wobbler = Window(20);
        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(wobbler);

        for (var settleRound = 0; settleRound < EnoughRounds; settleRound++)
        {
            wobbler.SnapsBackTo = Elsewhere;
            s.Workspace.RaiseWindowBoundsChanged(wobbler);
            s.Workspace.RaiseWindowBoundsChanged(wobbler);

            // Complies again before the limit is anywhere near reached.
            wobbler.SnapsBackTo = null;
            s.Workspace.RaiseWindowBoundsChanged(wobbler);
        }

        Assert.True(InTree(s, wobbler.Handle));
    }
}
