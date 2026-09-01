using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// Evicting a fighter must leave NO leaf behind, however many times it was announced.
/// </summary>
/// <remarks>
/// <para>
/// Measured on real hardware, Clipchamp reopening. Adds and evictions did not balance:
/// <c>InputNonClientPointerSource</c> was announced FOUR times and given up on THREE;
/// <c>ReunionWindowingCaptionControls</c> announced twice and given up on once. Each handle
/// therefore left exactly one leaf in the tree that nothing would ever remove, because eviction
/// takes out one leaf per give-up and the announcements outnumbered them.
/// </para>
/// <para>
/// The arithmetic is the whole defect, and it produced both reported symptoms at once. The orphan
/// leaves kept their tiles, so the real Clipchamp window opened into HALF the screen and was
/// immediately squeezed into a QUARTER it never got back. And the directional focus walk kept
/// landing on them: seven consecutive
/// <c>focus Left ... target=0x3090C UntrackedTarget activation=none</c> lines, which is the user
/// unable to move focus off the window by keyboard at all.
/// </para>
/// <para>
/// Not a keyboard defect, though it was reported as one. The <c>focus Left</c> lines exist, so the
/// hook ran, the chord matched and the action dispatched -- the walk failed downstream, on a leaf
/// whose window had already been thrown out.
/// </para>
/// </remarks>
public sealed class OrphanLeafAfterEvictionTests
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

    private static int LeavesFor(Setup s, nint handle) =>
        s.Trees.LeavesOn(s.Primary).Count(leaf => leaf.Window.Handle == handle);

    /// <summary>Enough rounds to cross the give-up threshold with room to spare.</summary>
    private const int EnoughRounds = 60;

    /// <summary>
    /// The measured sequence, replayed: announced, announced again, fought to eviction, announced
    /// again, fought again -- four announcements against three give-ups.
    /// </summary>
    [Fact]
    public void AFighterAnnouncedMoreOftenThanItIsEvicted_LeavesNoLeafBehind()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var settled = Window(10);
        var fighter = Window(20);
        s.Workspace.RaiseWindowAdded(settled);

        fighter.SnapsBackTo = Elsewhere;
        for (var announcement = 0; announcement < 4; announcement++)
        {
            s.Workspace.RaiseWindowAdded(fighter);
            s.Workspace.RaiseWindowBoundsChanged(fighter);
        }

        Fight(s, fighter, EnoughRounds);

        Assert.Equal(0, LeavesFor(s, fighter.Handle));
    }

    /// <summary>
    /// The consequence the user actually saw: the settled window never gets its space back, because
    /// an orphan leaf is still holding a tile for a window that was thrown out.
    /// </summary>
    [Fact]
    public void AfterEvictingAFighterAnnouncedRepeatedly_TheSurvivorReclaimsTheWholeWorkArea()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var settled = Window(10);
        var fighter = Window(20);
        s.Workspace.RaiseWindowAdded(settled);

        fighter.SnapsBackTo = Elsewhere;
        for (var announcement = 0; announcement < 4; announcement++)
        {
            s.Workspace.RaiseWindowAdded(fighter);
            s.Workspace.RaiseWindowBoundsChanged(fighter);
        }

        Fight(s, fighter, EnoughRounds);

        Assert.Equal(WorkArea, settled.LastSetPosition);
    }

    /// <summary>
    /// The focus walk must not be able to reach a window that was evicted. This is the keyboard
    /// symptom, and it is a tree question, not a keyboard one.
    /// </summary>
    [Fact]
    public void AnEvictedFighter_IsNeverReachableAsAFocusTarget()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var settled = Window(10);
        var fighter = Window(20);
        s.Workspace.RaiseWindowAdded(settled);

        fighter.SnapsBackTo = Elsewhere;
        for (var announcement = 0; announcement < 4; announcement++)
        {
            s.Workspace.RaiseWindowAdded(fighter);
            s.Workspace.RaiseWindowBoundsChanged(fighter);
        }

        Fight(s, fighter, EnoughRounds);

        Assert.DoesNotContain(
            s.Trees.LeavesOn(s.Primary),
            leaf => leaf.Window.Handle == fighter.Handle);
    }

    /// <summary>
    /// The measured order, replayed event for event, with BOTH of Clipchamp's chrome windows in
    /// play and the real window they were splitting.
    /// </summary>
    [Fact]
    public void TheMeasuredClipchampOrder_LeavesNoOrphanLeaf()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var real = Window(0x1B0922);
        var chromeA = Window(0x3090C);
        var chromeB = Window(0x20978);
        chromeA.SnapsBackTo = Elsewhere;
        chromeB.SnapsBackTo = Elsewhere;

        s.Workspace.RaiseWindowAdded(real);

        // 4 announcements of A and 2 of B, interleaved exactly as the trace recorded them, each
        // separated by enough drift to reach the give-up threshold.
        s.Workspace.RaiseWindowAdded(chromeA);
        s.Workspace.RaiseWindowAdded(chromeB);
        s.Workspace.RaiseWindowAdded(chromeA);
        Fight(s, chromeA, EnoughRounds);
        s.Workspace.RaiseWindowAdded(chromeA);
        Fight(s, chromeA, EnoughRounds);
        s.Workspace.RaiseWindowAdded(chromeA);
        Fight(s, chromeA, EnoughRounds);
        s.Workspace.RaiseWindowAdded(chromeB);
        Fight(s, chromeB, EnoughRounds);

        Assert.Equal(0, LeavesFor(s, chromeA.Handle));
        Assert.Equal(0, LeavesFor(s, chromeB.Handle));
        Assert.Equal(WorkArea, real.LastSetPosition);
    }

    private static void Fight(Setup s, RecordingWindow fighter, int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            s.Workspace.RaiseWindowBoundsChanged(fighter);
        }
    }
}
