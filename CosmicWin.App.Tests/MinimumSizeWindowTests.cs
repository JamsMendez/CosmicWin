using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// A window that will not go under a size is untiled for as long as that is true, and tiled again
/// the moment a slot that fits it exists.
/// </summary>
/// <remarks>
/// <para>
/// Reported with NVIDIA Broadcast, read out of the desktop trace. Offered [8,700 850x684] it landed
/// on [8,700 850x772] -- same corner, same width, and a height it would not shrink to -- twelve
/// times running, two seconds apart, and was then evicted as a fighter and refused for the life of
/// the handle. Twenty-two seconds of visible thrashing to reach a verdict the window had given on
/// its second attempt, and the verdict was wrong: it tiles perfectly in any slot 772 tall or more.
/// </para>
/// <para>
/// The give-up guard was reading the opposite behaviour to the one it was written for. Its
/// signature is NON-CONVERGENCE -- Clipchamp's input plumbing accepted every reposition and turned
/// up off the right edge of the desktop, forever, with no fixed point at all. This window converges
/// immediately and precisely; what it reports is a constraint, not a fault, and a constraint is
/// about the TILE it was offered rather than about the window.
/// </para>
/// <para>
/// Counting cannot tell them apart, because a fighter's landing is just as reproducible. The shape
/// of the miss can: obeying the position and every dimension it can meet, and raising only the one
/// it cannot, is a window doing as it is told as far as it is able.
/// </para>
/// </remarks>
public sealed class MinimumSizeWindowTests
{
    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1080);

    /// <summary>
    /// Wider than half the work area and narrower than all of it, so the same window does not fit
    /// beside a neighbour and does fit alone. That gap is the whole subject.
    /// </summary>
    /// <remarks>
    /// Wide enough that no SHARE of a two-window group can reach it either. A neighbour may be
    /// squeezed down to the layout floor ratio and no further, which on this work area leaves at
    /// most 768 to give; asking for 840 more than half is asking for space that does not exist, so
    /// these facts still describe a window that genuinely cannot be fitted rather than one the
    /// tiler has simply not tried hard enough for.
    /// </remarks>
    private static readonly (int Width, int Height) Floor = (1800, 100);

    /// <summary>
    /// And the ceiling the real one turned out to have as well. Narrower than the whole work area,
    /// so the same window that OVERFLOWS a half tile UNDER-FILLS the full one -- which is exactly
    /// the pair of behaviours the hardware showed, and exactly what one axis of a
    /// <c>WM_GETMINMAXINFO</c> range looks like from outside.
    /// </summary>
    private static readonly (int Width, int Height) Ceiling = (1850, 1080);

    private sealed record Setup(TreeManager Trees, WindowRegistry Registry, FakeWorkspace Workspace, IDisplay Primary);

    private static Setup OneDisplay()
    {
        var primary = new FakeDisplay(new IntPtr(1), WorkArea, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        return new Setup(new TreeManager([primary], primary, registry), registry, new FakeWorkspace(), primary);
    }

    private static MultiMonitorWorkspaceAdapter Adapter(Setup s) =>
        new(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, () => null);

    /// <summary>
    /// An adapter that reports a focused leaf, so an arriving window SPLITS it.
    /// </summary>
    /// <remarks>
    /// The default factory reports none, and <c>InsertWindow</c> then appends to the root group --
    /// which builds FLAT trees. Production almost never does: with a focused leaf it goes through
    /// <c>SplitLeafInPlace</c> and every group comes out a pair. A fact about nesting has to be
    /// built the way the real thing is, or it measures a shape no user will ever have.
    /// </remarks>
    private static MultiMonitorWorkspaceAdapter AdapterFocusing(Setup s, Func<LeafNode?> focused) =>
        new(s.Workspace, s.Trees, s.Registry, () => ExceptionList.Empty, () => false, focused);

    /// <summary>
    /// Collects the adapter's own lines, so a fact can pin that the retry RAN rather than only what
    /// it decided. A window whose floor no arrangement can satisfy never changes the tree, and its
    /// being asked at all is then invisible in every other observable.
    /// </summary>
    private sealed class RecordingTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private static RecordingWindow Window(int handle) =>
        new(new IntPtr(handle), Rectangle.FromSize(0, 0, 400, 300));

    private static bool InTree(Setup s, nint handle) =>
        s.Trees.LeavesOn(s.Primary).Any(leaf => leaf.Window.Handle == handle);

    private static void Rounds(Setup s, RecordingWindow window, int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            s.Workspace.RaiseWindowBoundsChanged(window);
        }
    }

    /// <summary>Far more rounds than the fighter limit, so "still tiled" cannot be a slow verdict.</summary>
    private const int EnoughRounds = 60;

    /// <summary>
    /// Two windows, side by side: neither tile is wide enough for <see cref="Floor"/>.
    /// </summary>
    /// <remarks>
    /// The adapter is built BEFORE anything is announced -- it subscribes in its constructor, so a
    /// window added ahead of it is never seen and every assertion below would be measuring an empty
    /// tree. And the floor is set before the window is announced, because that is when a real one
    /// has it: applying it afterwards leaves the window sitting exactly on its tile for one extra
    /// round, spending a round on an artefact of the test rather than on the behaviour.
    /// </remarks>
    private static (Setup Setup, MultiMonitorWorkspaceAdapter Adapter, RecordingWindow Neighbour, RecordingWindow Constrained) SideBySide()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);

        var neighbour = Window(10);
        var constrained = Window(20);
        constrained.MinimumSize = Floor;
        constrained.MaximumSize = Ceiling;

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(constrained);
        return (s, adapter, neighbour, constrained);
    }

    /// <summary>
    /// The second identical answer is the whole answer. Twelve was the right number for deciding
    /// whether an application is still wobbling its way through startup; it is the wrong number for
    /// a window that has stated a fact about itself and will state the same one forever.
    /// </summary>
    [Fact]
    public void AWindowWhoseTileIsUnderItsFloor_IsUntiledOnTheSecondMiss()
    {
        var (s, adapter, _, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 1);
        Assert.True(InTree(s, constrained.Handle), "one miss is a moment, not a constraint");

        Rounds(s, constrained, 1);
        Assert.False(InTree(s, constrained.Handle));
    }

    /// <summary>The point of untiling it: the space stops being reserved for a window that overflows it.</summary>
    [Fact]
    public void OnceItIsUntiled_TheNeighbourReclaimsTheWholeWorkArea()
    {
        var (s, adapter, neighbour, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 2);

        Assert.Equal(WorkArea, neighbour.LastSetPosition);
    }

    /// <summary>
    /// The half that separates this from giving up, and the reason it is worth building at all. The
    /// window was never at fault, so the refusal is spent on the TILE: close the neighbour, and the
    /// slot that fits it exists, and it tiles like anything else without the user asking twice.
    /// </summary>
    [Fact]
    public void OnceASlotThatFitsExists_ItIsTiledAgain()
    {
        var (s, adapter, neighbour, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 2);
        Assert.False(InTree(s, constrained.Handle));

        s.Workspace.RaiseWindowRemoved(neighbour);
        s.Workspace.RaiseWindowAdded(constrained);

        Assert.True(InTree(s, constrained.Handle));
        Assert.Equal(WorkArea, constrained.LastSetPosition);
    }

    /// <summary>
    /// The other side of not refusing the handle. Admission is retried freely -- every bounds change
    /// on an untiled window routes back through the add path -- so the floor has to be re-checked
    /// there, against the tile the window ACTUALLY receives. Without that, each retry would insert
    /// it, reflow the tree, and start the same two-round measurement over: a slower storm, which is
    /// the exact failure `_givenUp` was invented to prevent.
    /// </summary>
    [Fact]
    public void WhileNoSlotFits_ReAnnouncingItChangesNothing()
    {
        var (s, adapter, neighbour, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 2);
        Assert.False(InTree(s, constrained.Handle));

        s.Workspace.RaiseWindowAdded(constrained);

        Assert.False(InTree(s, constrained.Handle));
        Assert.Equal(WorkArea, neighbour.LastSetPosition);
    }

    /// <summary>
    /// And "changes nothing" has to mean nothing was TOUCHED, not merely that the tree came back
    /// the same shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on hardware, 17:27:01 to 17:27:07 and still going. A window parked for its floor was
    /// re-admitted every two seconds forever: `added ... -> 850x1376` then `still too small ...
    /// left untiled`, over and over, each pass inserting it into the tree, reflowing the whole
    /// display, measuring what was already recorded, taking it out again and reflowing back.
    /// </para>
    /// <para>
    /// It sustains ITSELF, which is why it never stops. The admission asks the parked window to
    /// take a tile it cannot take; the workspace files the tile it asked for; the window is still
    /// its own larger size; and the next reconciliation pass reads that difference as a bounds
    /// change and routes it straight back into admission. The loop's fuel is the one call that
    /// should never have been made.
    /// </para>
    /// <para>
    /// So the fact is about the ASK, not about the outcome. Arranging is arithmetic on the tree and
    /// costs nothing; positioning is what moves windows and what is remembered. A floor already
    /// recorded can be measured against the tile the tree would hand out before anything is moved.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReAnnouncingAParkedWindow_NeverAsksItToTakeATileItCannotTake()
    {
        var (s, adapter, _, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 2);
        Assert.False(InTree(s, constrained.Handle));

        var asked = constrained.SetPositionCallCount;
        s.Workspace.RaiseWindowAdded(constrained);

        Assert.Equal(asked, constrained.SetPositionCallCount);
    }

    /// <summary>
    /// The same pass seen from the neighbour: it must never be handed the half tile that only
    /// exists while a window that cannot stay is briefly in the tree.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a count. Re-arranging an unchanged tree re-applies the geometry it already
    /// has, which moves nothing and is nobody's bug; being given HALF the work area and the whole
    /// of it again is the flash this is about. Asserting the count would fail on the harmless case
    /// and pass on a build that flashed and settled, which is backwards.
    /// </remarks>
    [Fact]
    public void ReAnnouncingAParkedWindow_NeverFlashesTheNeighbourIntoASmallerTile()
    {
        var (s, adapter, neighbour, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 2);
        Assert.Equal(WorkArea, neighbour.LastSetPosition);

        var settled = neighbour.Positions.Count;
        s.Workspace.RaiseWindowAdded(constrained);

        Assert.All(neighbour.Positions.Skip(settled), position => Assert.Equal(WorkArea, position));
    }

    /// <summary>
    /// Untouched. Every other route back into the tree runs through a bounds change on the parked
    /// window itself, and a window floating where nobody is touching it does not produce one -- so
    /// "it tiles again once a slot fits" was only ever true for a window the user happened to move.
    /// Closing the neighbour is the moment a slot can GROW, and it is the moment to ask again.
    /// </summary>
    [Fact]
    public void WhenTheNeighbourCloses_TheParkedWindowIsTiledWithoutBeingTouched()
    {
        var (s, adapter, neighbour, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 2);
        Assert.False(InTree(s, constrained.Handle));

        s.Workspace.RaiseWindowRemoved(neighbour);

        Assert.True(InTree(s, constrained.Handle));
    }

    /// <summary>
    /// And asking again is not the same as letting it in. A neighbour closing on a display where
    /// the freed space still does not reach the floor must leave the window exactly where it was --
    /// the retry re-measures, it does not forgive.
    /// </summary>
    [Fact]
    public void WhenTheFreedSpaceStillDoesNotFit_TheParkedWindowStaysParked()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var first = Window(10);
        var second = Window(20);
        var constrained = Window(30);

        // A floor no arrangement on this work area can ever satisfy, so the freed space cannot help.
        constrained.MinimumSize = (WorkArea.Width * 2, 100);

        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(constrained);

        Rounds(s, constrained, 2);
        Assert.False(InTree(s, constrained.Handle));

        s.Workspace.RaiseWindowRemoved(second);

        Assert.False(InTree(s, constrained.Handle));
        Assert.True(InTree(s, first.Handle));
    }

    /// <summary>
    /// A retry reaches only the display whose space actually changed. Admitting a window resolves
    /// its display from its own bounds, so a parked window swept up by a close on ANOTHER monitor is
    /// inserted into its own tree, reflowed in, measured, and untiled again -- every window on that
    /// untouched monitor jumping twice because something closed somewhere else.
    /// </summary>
    [Fact]
    public void AWindowParkedOnOneDisplay_IsNotRetriedWhenSomethingClosesOnAnother()
    {
        var left = new FakeDisplay(new IntPtr(1), WorkArea, WorkArea, 1.0, true);
        var rightArea = Rectangle.FromSize(1920, 0, 1920, 1080);
        var right = new FakeDisplay(new IntPtr(2), rightArea, rightArea, 1.0, false);

        var registry = new WindowRegistry();
        var s = new Setup(new TreeManager([left, right], left, registry), registry, new FakeWorkspace(), left);
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var host = Window(10);
        var constrained = Window(20);
        constrained.MinimumSize = Floor;

        var onRight = new RecordingWindow(new IntPtr(30), Rectangle.FromSize(1930, 10, 400, 300));
        var alsoOnRight = new RecordingWindow(new IntPtr(40), Rectangle.FromSize(1940, 20, 400, 300));

        s.Workspace.RaiseWindowAdded(host);
        s.Workspace.RaiseWindowAdded(constrained);
        s.Workspace.RaiseWindowAdded(onRight);
        s.Workspace.RaiseWindowAdded(alsoOnRight);

        Rounds(s, constrained, 2);
        Assert.False(InTree(s, constrained.Handle));

        // Nothing on the left monitor may move because something closed on the right one.
        var settled = host.Positions.Count;

        s.Workspace.RaiseWindowRemoved(alsoOnRight);

        Assert.Equal(settled, host.Positions.Count);
        Assert.False(InTree(s, constrained.Handle));
    }

    /// <summary>
    /// A retry is an ADOPTION, never a birth, and the difference is not cosmetic. A birth carries
    /// the redirect that sends a newly-created window to the desktop the user is actually on -- and
    /// a parked window has been sitting where it was left, on whichever desktop that is. Announcing
    /// the retry as a birth would drag it across desktops for the crime of a neighbour closing,
    /// which is the shape of a regression this project has already paid for once: restart with
    /// windows on several desktops and they empty themselves into the one in view.
    /// </summary>
    [Fact]
    public void ARetryIsAnAdoption_AndNeverAsksToMoveTheWindowBetweenDesktops()
    {
        var elsewhere = Guid.NewGuid();
        var user = Guid.NewGuid();

        var primary = new FakeDisplay(new IntPtr(1), WorkArea, WorkArea, 1.0, true);
        var registry = new WindowRegistry();
        var trees = new TreeManager([primary], primary, registry) { CurrentDesktop = () => elsewhere };
        var workspace = new FakeWorkspace();

        var sent = new List<nint>();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = _ => elsewhere,
            ResolveUserDesktop = () => user,
            SendWindowToDesktop = (handle, _) =>
            {
                sent.Add(handle);
                return true;
            },
        };

        var s = new Setup(trees, registry, workspace, primary);

        var neighbour = Window(10);
        var constrained = Window(20);
        constrained.MinimumSize = Floor;

        // Adopted, so the arrival itself asks to move nothing and the retry is the only suspect.
        workspace.RaiseWindowAdded(neighbour, WindowArrival.Adopted);
        workspace.RaiseWindowAdded(constrained, WindowArrival.Adopted);

        Rounds(s, constrained, 2);
        Assert.False(InTree(s, constrained.Handle));

        workspace.RaiseWindowRemoved(neighbour);

        Assert.True(InTree(s, constrained.Handle));
        Assert.Empty(sent);
    }

    /// <summary>
    /// And it stays tiled. A floor that keeps being met is not a miss, so nothing accumulates and
    /// the window is never judged again for a constraint it is no longer straining against.
    /// </summary>
    [Fact]
    public void OnceTiledInASlotThatFits_ItIsLeftAlone()
    {
        var (s, adapter, neighbour, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 2);
        s.Workspace.RaiseWindowRemoved(neighbour);
        s.Workspace.RaiseWindowAdded(constrained);

        Rounds(s, constrained, EnoughRounds);

        Assert.True(InTree(s, constrained.Handle));
    }

    /// <summary>
    /// The discriminator, stated as its own fact. This window misses by exactly as much and just as
    /// reproducibly as a constrained one, and lands nowhere near the corner it was given -- which is
    /// what a fighter does and what no amount of repetition turns into a constraint. It must take
    /// the full count and stay refused afterwards.
    /// </summary>
    [Fact]
    public void AWindowThatLandsBiggerButInTheWrongPlace_IsAFighterNotAFloor()
    {
        var (s, adapter, _, constrained) = SideBySide();
        using var _adapter = adapter;

        constrained.MinimumSize = null;
        constrained.SnapsBackTo = Rectangle.FromSize(5000, 5000, Floor.Width, WorkArea.Height);

        Rounds(s, constrained, 3);
        Assert.True(InTree(s, constrained.Handle), "a wrong-corner landing is never a floor, however often it repeats");

        Rounds(s, constrained, EnoughRounds);
        Assert.False(InTree(s, constrained.Handle));

        // Given up on, not parked: a fighter is refused for the life of the handle, and no slot
        // will ever be big enough to change that.
        s.Workspace.RaiseWindowAdded(constrained);
        Assert.False(InTree(s, constrained.Handle));
    }

    /// <summary>
    /// A window that cannot FILL its tile keeps it. This reverses a reading made earlier in the same
    /// work, and the hardware is what reversed it: a shrink at the tile's own corner was treated as a
    /// fight, on the reasoning that only growing could be a constraint. NVIDIA Broadcast clamps its
    /// height UP to 772 in a short tile and DOWN to 1000 in a tall one -- one window, one range, both
    /// directions -- so that reasoning was simply wrong about what a constraint looks like.
    /// <para>
    /// And the two directions deserve different answers, which is the part worth keeping. A window
    /// that overflows its tile is covering its neighbour, so it is untiled. A window that under-fills
    /// harms nobody: it leaves a little empty space inside its own slot, and evicting it over that
    /// would cost the user a tiled window to tidy away a gap.
    /// </para>
    /// </summary>
    [Fact]
    public void AWindowThatCannotFillItsTile_KeepsItAndIsNeverGivenUpOn()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var modest = Window(20);
        modest.MaximumSize = (500, 1080);
        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(modest);

        // Everything the neighbour was asked to do BEFORE the rounds start, including the whole
        // work area it legitimately held while it was alone. Only what follows is under test.
        var duringSetup = neighbour.Positions.Count;

        Rounds(s, modest, EnoughRounds);

        Assert.True(InTree(s, modest.Handle));
        Assert.True(InTree(s, neighbour.Handle));

        // Never untiled, not even for an instant. Ending up back in the tree is not the same as
        // never leaving it: untiling this window reflows the neighbour across the WHOLE work area,
        // and re-admitting it hands the half straight back, so the round trip is invisible from
        // where the two of them finish. It is not invisible from what the neighbour was asked to do
        // in between -- and a mutation that untiled this window survived the suite until this line.
        Assert.DoesNotContain(WorkArea, neighbour.Positions.Skip(duringSetup));
    }

    /// <summary>
    /// Keeping its tile is not the same as being asked for it again every two seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured with the real NVIDIA Broadcast, 18:51:44 onwards. It was admitted, judged
    /// correctly -- `clamps itself ... will not go over 1136x1000; kept, and its tile is not
    /// filled` -- and then reflowed on EVERY poll for the life of the window:
    /// </para>
    /// <para>
    /// <c>reflow ... [W=1136 H=1000] -> [W=1136 H=1376]</c>, at :44.9, :46.9, :48.9, :51.0, :53.0.
    /// </para>
    /// <para>
    /// The same self-sustaining shape as the parked-window storm and the same fuel. The verdict
    /// falls through to a reflow, the reflow re-asks the window for the height it has just refused,
    /// the ask is filed as its bounds, and the next reconciliation pass reads the difference
    /// between that ask and the window's real size as a fresh bounds change. Nothing about the tree
    /// changed; the only thing that moved was a number the tiler wrote about a window that had
    /// already said no.
    /// </para>
    /// <para>
    /// Cleared for a reflow the moment the window stops being inside the tile it holds, because a
    /// tile that shrank under it is a different fact -- possibly a floor -- and has to be judged
    /// again.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWindowThatCannotFillItsTile_IsNotAskedToFillItForever()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var clamped = Window(10);
        clamped.MaximumSize = (WorkArea.Width - 400, WorkArea.Height - 200);
        s.Workspace.RaiseWindowAdded(clamped);

        // Enough to reach the verdict twice over, so what follows is measured on a settled window
        // rather than on one still being worked out.
        Rounds(s, clamped, 4);
        var settled = clamped.SetPositionCallCount;

        Rounds(s, clamped, EnoughRounds);

        Assert.True(InTree(s, clamped.Handle));
        Assert.Equal(settled, clamped.SetPositionCallCount);
    }

    /// <summary>
    /// And it goes back to being a question the moment its tile stops fitting around it: a slot
    /// that shrank under a clamping window may be under its FLOOR, which is the one verdict that
    /// must still untile it.
    /// </summary>
    [Fact]
    public void AClampingWindowWhoseTileShrinksUnderIt_IsJudgedAgain()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var clamped = Window(10);
        clamped.MinimumSize = Floor;
        clamped.MaximumSize = Ceiling;
        s.Workspace.RaiseWindowAdded(clamped);

        // Alone, it cannot fill the work area: judged, kept, and thereafter left alone.
        Rounds(s, clamped, 4);
        Assert.True(InTree(s, clamped.Handle));

        // A neighbour arrives and halves the slot, which is now under the floor.
        s.Workspace.RaiseWindowAdded(Window(20));
        Rounds(s, clamped, 4);

        Assert.False(InTree(s, clamped.Handle));
    }

    /// <summary>
    /// A floor is what the window REFUSED to go under, not everything it happened to measure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured with the real NVIDIA Broadcast, 19:08:33. Its tile was flipped to a wide short row
    /// -- [8,930 3424x454] -- and it landed on 1945x772: it raised the height it would not go under
    /// AND, on the other axis, took barely half the width it was offered. Both numbers were then
    /// recorded as its floor.
    /// </para>
    /// <para>
    /// <c>still too small hwnd=0x400F4 ... -- needs 1945x772; left untiled</c>
    /// </para>
    /// <para>
    /// It does not need 1945. Fifteen minutes earlier in the same session the same window was tiled
    /// happily in a 1136-wide tile and reported `clamps itself ... will not go over 1136x1000`. The
    /// width it "demanded" was a width it had chosen, and recording it locked the window out of
    /// every slot it would have accepted.
    /// </para>
    /// <para>
    /// The two are distinguishable from the one rectangle, and the test is per AXIS: a dimension is
    /// a floor only where the window came back BIGGER than the tile. Where it came back smaller it
    /// obeyed, and a window that underfills an axis has said nothing about a minimum on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWindowThatOverflowsOneAxisAndUnderfillsTheOther_RecordsAFloorOnlyWhereItOverflowed()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var mixed = Window(20);

        // Broadcast's shape: a height it will not go under, and a width it will not go over.
        // Beyond what a SHARE can reach as well: a neighbour may be squeezed to the layout's
        // floor ratio and no further, so this stays a window that genuinely cannot be fitted.
        mixed.MinimumSize = (100, 1000);
        mixed.MaximumSize = (1200, WorkArea.Height * 2);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(mixed);

        // Side by side, both tiles are 1080 tall and this window fits. Flipping the axis makes them
        // 540 tall, which is the arrangement that produced the bad reading on hardware.
        Assert.True(s.Registry.TryGetLeaf(mixed.Handle, out var leaf) && leaf is not null);
        Assert.True(LayoutTree.ToggleAxis(leaf!));

        Rounds(s, mixed, 3);
        Assert.False(InTree(s, mixed.Handle));

        // Announced again, the tree splits the work area the wide way and offers 960x1080 -- under
        // the width it was recorded as "needing" and comfortably over the height it actually
        // refused to go below. It fits, and only a floor that kept the width can turn it away.
        s.Workspace.RaiseWindowAdded(mixed);

        Assert.True(InTree(s, mixed.Handle));
    }

    /// <summary>
    /// An axis the window filled EXACTLY has demonstrated nothing, and must not become a floor.
    /// </summary>
    /// <remarks>
    /// A window landing on the width it was given is a window that took the width it was given.
    /// Reading that as "will not go under" pins it to whatever the widest tile it ever saw happened
    /// to be, which is the same mistake as recording the whole rectangle, one comparison narrower.
    /// </remarks>
    [Fact]
    public void AnAxisTheWindowFilledExactly_IsNotAFloor()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var tall = Window(20);

        // A height it will not go under, and nothing at all to say about its width.
        // Beyond what a SHARE can reach as well: a neighbour may be squeezed to the layout's
        // floor ratio and no further, so this stays a window that genuinely cannot be fitted.
        tall.MinimumSize = (0, 1000);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(tall);

        // Stacked, the tile is the full width and half the height: the window fills the width
        // exactly and raises only the height.
        Assert.True(s.Registry.TryGetLeaf(tall.Handle, out var leaf) && leaf is not null);
        Assert.True(LayoutTree.ToggleAxis(leaf!));

        Rounds(s, tall, 3);
        Assert.False(InTree(s, tall.Handle));

        // Offered half the width and all the height, which clears the only thing it ever refused.
        s.Workspace.RaiseWindowAdded(tall);

        Assert.True(InTree(s, tall.Handle));
    }

    /// <summary>
    /// A floor demonstrated on one axis is not forgotten by a later arrival that only tests the
    /// other one.
    /// </summary>
    /// <remarks>
    /// Per-axis recording has two halves and this is the one nothing else reaches. A window with a
    /// floor on BOTH axes only ever proves one of them per arrangement -- a narrow tall tile asks
    /// about its width, a wide short one asks about its height -- and clearing the axis that was
    /// not asked would make every arrangement overwrite what the last one learned.
    /// </remarks>
    [Fact]
    public void AFloorOnOneAxis_SurvivesAnArrivalThatOnlyTestsTheOther()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var boxed = Window(20);
        // Beyond what a SHARE can reach as well: a neighbour may be squeezed to the layout's
        // floor ratio and no further, so this stays a window that genuinely cannot be fitted.
        boxed.MinimumSize = (1800, 1000);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(boxed);

        // Narrow and tall: asks about the WIDTH only, and the width is refused.
        Rounds(s, boxed, 3);
        Assert.False(InTree(s, boxed.Handle));

        // Alone, both floors clear and it is tiled again.
        s.Workspace.RaiseWindowRemoved(neighbour);
        Assert.True(InTree(s, boxed.Handle));

        // Wide and short: asks about the HEIGHT only. Nothing here tests the width -- the window
        // fills it exactly -- so the width already on record has to survive the pass.
        var second = Window(30);
        s.Workspace.RaiseWindowAdded(second);
        Assert.True(s.Registry.TryGetLeaf(boxed.Handle, out var leaf) && leaf is not null);
        Assert.True(LayoutTree.ToggleAxis(leaf!));

        Rounds(s, boxed, 3);
        Assert.False(InTree(s, boxed.Handle));

        // Back to a narrow tall slot. It clears the height it just proved and NOT the width it
        // proved earlier, so it must stay out.
        s.Workspace.RaiseWindowAdded(boxed);

        Assert.False(InTree(s, boxed.Handle));
    }

    /// <summary>
    /// Closing a neighbour is not the only way a slot can grow, and it must not be the only way
    /// back into the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported from real use and reproduced immediately. Alt+O with NVIDIA Broadcast on screen
    /// stacks the group, its tile becomes 3424x244 against a floor of 772, and it is untiled --
    /// correct, a 244-pixel row genuinely cannot hold it. Pressing Alt+O again puts the columns
    /// back, 1383 tall, which clears that floor with room to spare. Nothing asked:
    /// </para>
    /// <para>
    /// <c>20:17:38 border around 0xA0876 [L=2041 T=8 W=1391 H=1376]</c> -- and that was the ONLY
    /// line. No guard state, no added, the window still floating at 8,1140.
    /// </para>
    /// <para>
    /// The retry was wired to one event, a window LEAVING, on the reasoning that admitting one only
    /// ever divides the space further. True, and beside the point: an orientation toggle, a resize
    /// chord and a drag all reshape slots without anything leaving, and every one of them left the
    /// user with a floating window and no way back except closing something.
    /// </para>
    /// <para>
    /// Affordable now for a reason. Re-offering the tree used to mean inserting the window,
    /// reflowing the display, measuring and undoing it; today a known floor is measured against the
    /// tile the tree WOULD hand out, arranging without moving anything. Asking often costs a
    /// dictionary scan that is empty on almost every pass.
    /// </para>
    /// </remarks>
    [Fact]
    public void WhenTheLayoutChangesShapeWithoutAnythingClosing_TheParkedWindowIsAskedAgain()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var trace = new RecordingTrace();
        adapter.Trace = trace;

        var neighbour = Window(10);
        var second = Window(30);
        var constrained = Window(20);

        // A floor NO arrangement on this work area can satisfy, branch or no branch. That is the
        // point: the window can never be let in, so the only thing left to observe is whether it
        // was ASKED -- which is the trigger this fact exists for.
        constrained.MinimumSize = (WorkArea.Width * 2, 100);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(constrained);

        Rounds(s, constrained, 3);
        Assert.False(InTree(s, constrained.Handle));

        // Nothing closes; the user simply reshapes what is left, and the OS reports the survivors
        // settling into it.
        Assert.True(s.Registry.TryGetLeaf(neighbour.Handle, out var survivor) && survivor is not null);
        Assert.True(LayoutTree.ToggleAxis(survivor!));
        trace.Lines.Clear();
        s.Workspace.RaiseWindowBoundsChanged(neighbour);

        Assert.Contains(trace.Lines, line => line.StartsWith("still too small", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reshape a CHORD performs reaches this adapter through no event at all, so it needs its
    /// own way in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first cut of this fix hung on WindowBoundsChanged and did nothing on hardware. A chord
    /// reflow moves windows through <c>TreeArranger</c>, <c>Win32Window.SetPosition</c> writes the
    /// asked-for rectangle straight into the cached bounds, and by the time the WinEvent lands
    /// <c>Win32Workspace.UpdateBounds</c> is comparing the rectangle against itself and reports NO
    /// change. That is documented in TreeArranger and it is the whole reason the focus border needs
    /// its own callback.
    /// </para>
    /// <para>
    /// So a compliant window moving raises nothing, and the two Alt+O presses that produced the
    /// report emitted not one <c>guard state</c> line for the parked window -- the retry never ran.
    /// The headless fact passed only because the test raised the event by hand: the stimulus was
    /// not what production produces.
    /// </para>
    /// <para>
    /// The chord's own completion is the signal, which is what the executor's AfterAction already
    /// is -- the same hook the border uses, and for the same reason.
    /// </para>
    /// </remarks>
    [Fact]
    public void WhenAChordReshapesTheLayout_TheParkedWindowIsAskedAgain()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var trace = new RecordingTrace();
        adapter.Trace = trace;

        var neighbour = Window(10);
        var second = Window(30);
        var constrained = Window(20);

        // Unsatisfiable on purpose, for the same reason as the drag fact above: what is under test
        // is that the chord's completion ASKS, not what the answer turns out to be.
        constrained.MinimumSize = (WorkArea.Width * 2, 100);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(constrained);

        Rounds(s, constrained, 3);
        Assert.False(InTree(s, constrained.Handle));

        // Nothing moves out of band and nothing closes, so this call is the ONLY thing that
        // happens -- exactly as it is in production, where the executor makes it after every chord.
        trace.Lines.Clear();
        adapter.RetryParkedWindows();

        Assert.Contains(trace.Lines, line => line.StartsWith("still too small", StringComparison.Ordinal));
    }

    /// <summary>The same call, and still not a pardon.</summary>
    [Fact]
    public void WhenAChordReshapesButStillDoesNotFit_TheParkedWindowStaysOut()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var second = Window(30);
        var constrained = Window(20);
        constrained.MinimumSize = (WorkArea.Width * 2, 100);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(constrained);

        Rounds(s, constrained, 3);
        Assert.False(InTree(s, constrained.Handle));

        adapter.RetryParkedWindows();

        Assert.False(InTree(s, constrained.Handle));
        Assert.True(InTree(s, neighbour.Handle));
    }

    /// <summary>
    /// And asking again is still not letting it in: a reshape that does NOT free enough leaves the
    /// window exactly where it was.
    /// </summary>
    /// <remarks>
    /// The retry re-measures rather than forgives, and the cheap path has to keep that promise or
    /// the whole floor becomes advisory.
    /// </remarks>
    [Fact]
    public void WhenTheLayoutReshapesButStillDoesNotFit_TheParkedWindowStaysOut()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var second = Window(30);
        var constrained = Window(20);

        // A floor no arrangement on this work area can satisfy, so no reshape can ever help.
        constrained.MinimumSize = (WorkArea.Width * 2, 100);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(constrained);

        Rounds(s, constrained, 3);
        Assert.False(InTree(s, constrained.Handle));

        Assert.True(s.Registry.TryGetLeaf(neighbour.Handle, out var survivor) && survivor is not null);
        Assert.True(LayoutTree.ToggleAxis(survivor!));
        s.Workspace.RaiseWindowBoundsChanged(neighbour);

        Assert.False(InTree(s, constrained.Handle));
        Assert.True(InTree(s, neighbour.Handle));
    }

    /// <summary>
    /// A window asking for more than its slot is a reason to RESHAPE the tree, not to leave it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported from real use, twice. Alt+O stacks the group, NVIDIA Broadcast's row is 453 tall
    /// against a floor of 772, and it was untiled. Correct, and still the wrong outcome: the user
    /// pressed a key about ORIENTATION and a window fell out of the layout.
    /// </para>
    /// <para>
    /// There was a third answer neither ejecting nor squashing everyone, and the maintainer named
    /// it: give the window its own branch. <c>[A, B, C]</c> stacked becomes
    /// <c>[[A, C] stacked, B] side by side</c> -- A and C get the stacking that was asked for, and
    /// B gets the full height rather than a third of it.
    /// </para>
    /// <para>
    /// MEASURED, never assumed. The trade buys one axis and sells half the other, so a window
    /// short of BOTH can come out of it no better off; the tree is reshaped, the tiles are computed
    /// without moving anything, and the floor is checked against the result before any of it is
    /// kept.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWindowAskingForMoreThanItsSlot_IsGivenItsOwnBranch_NotEjected()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var first = Window(10);
        var second = Window(30);
        var constrained = Window(20);

        // Taller than any share of a stacked work area and shorter than all of it.
        constrained.MinimumSize = (100, 700);

        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(constrained);

        Assert.True(s.Registry.TryGetLeaf(constrained.Handle, out var leaf) && leaf is not null);
        Assert.True(LayoutTree.ToggleAxis(leaf!));

        Rounds(s, constrained, 3);

        Assert.True(InTree(s, constrained.Handle));
        Assert.True(s.Registry.TryGetLeaf(constrained.Handle, out var settled) && settled is not null);
        Assert.True(TreeArranger.TileOf(settled!).Height >= 700);
    }

    /// <summary>
    /// The neighbours keep the orientation that was asked for. Reshaping to fit one window must not
    /// quietly cancel the chord for everyone else.
    /// </summary>
    [Fact]
    public void GivingAWindowItsOwnBranch_LeavesTheOthersOnTheAxisTheyAskedFor()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var first = Window(10);
        var second = Window(30);
        var constrained = Window(20);
        constrained.MinimumSize = (100, 700);

        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(constrained);

        Assert.True(s.Registry.TryGetLeaf(constrained.Handle, out var leaf) && leaf is not null);
        var asked = leaf!.Parent!.Axis == SplitAxis.Horizontal ? SplitAxis.Vertical : SplitAxis.Horizontal;
        Assert.True(LayoutTree.ToggleAxis(leaf!));

        Rounds(s, constrained, 3);

        Assert.True(s.Registry.TryGetLeaf(first.Handle, out var kept) && kept is not null);
        Assert.Equal(asked, kept!.Parent!.Axis);
    }

    /// <summary>
    /// And when a branch of its own does not help either, the window is still parked. Reshaping is
    /// an attempt, not a pardon.
    /// </summary>
    /// <remarks>
    /// The failure path costs nothing to undo: untiling the window collapses the branch that was
    /// built for it, and the neighbours are left on the axis the chord asked for -- exactly where
    /// they were before any of this existed.
    /// </remarks>
    [Fact]
    public void WhenABranchOfItsOwnStillDoesNotFit_TheWindowIsParkedAsBefore()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var first = Window(10);
        var second = Window(30);
        var constrained = Window(20);

        // No arrangement on this work area can satisfy it, branch or no branch.
        constrained.MinimumSize = (WorkArea.Width * 2, 100);

        s.Workspace.RaiseWindowAdded(first);
        s.Workspace.RaiseWindowAdded(second);
        s.Workspace.RaiseWindowAdded(constrained);

        Rounds(s, constrained, 3);

        Assert.False(InTree(s, constrained.Handle));
        Assert.True(InTree(s, first.Handle));
        Assert.True(InTree(s, second.Handle));
    }

    /// <summary>
    /// One sibling is enough. A window short of its share takes the shortfall out of the neighbour
    /// instead of leaving the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case that actually occurs, and the reason the branch alone was not enough. Real trees
    /// are BINARY -- a new window splits the focused tile in two -- so a constrained window usually
    /// has exactly ONE sibling and there is nobody to be stood beside.
    /// </para>
    /// <para>
    /// Measured on the reported desktop: Terminal 1710, Alacritty 849, Broadcast 849. A half and
    /// two quarters, not three equal thirds -- and toggling the inner pair moved only those two
    /// while Terminal stayed exactly where it was, which is what proves the shape. No group there
    /// has three children, so nothing could ever have been given a branch and the fix before this
    /// one could never fire.
    /// </para>
    /// <para>
    /// The cost is bounded by what the window needs and nothing else: it takes the shortfall, and
    /// the neighbour keeps the rest. On that desktop stacking the pair offers 688 each against a
    /// floor of 772, so the neighbour goes to 604 -- 84 pixels, not the half-a-screen the flat case
    /// would have cost.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWindowWithOneSibling_IsGivenTheShareItNeeds_NotEjected()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var constrained = Window(20);

        // More than half the height and well within what one neighbour can spare.
        constrained.MinimumSize = (100, 700);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(constrained);

        Assert.True(s.Registry.TryGetLeaf(constrained.Handle, out var leaf) && leaf is not null);
        Assert.True(LayoutTree.ToggleAxis(leaf!));

        Rounds(s, constrained, 3);

        Assert.True(InTree(s, constrained.Handle));
        Assert.True(InTree(s, neighbour.Handle));
        Assert.True(s.Registry.TryGetLeaf(constrained.Handle, out var settled) && settled is not null);
        Assert.True(TreeArranger.TileOf(settled!).Height >= 700);
    }

    /// <summary>
    /// And the neighbour keeps what the layout says it must. A share is taken, never seized.
    /// </summary>
    /// <remarks>
    /// The donor floor is the layout's own, the one the resize chord has always used. A request
    /// that would push a neighbour under it is refused outright rather than clamped, because most
    /// of the way to a size a window will not go under is the same as nowhere -- and the window is
    /// then parked, exactly as it was before any of this existed.
    /// </remarks>
    [Fact]
    public void AShareIsTakenFromTheNeighbour_NeverBelowTheLayoutsOwnFloor()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var constrained = Window(20);
        constrained.MinimumSize = (100, 700);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(constrained);

        Assert.True(s.Registry.TryGetLeaf(constrained.Handle, out var leaf) && leaf is not null);
        Assert.True(LayoutTree.ToggleAxis(leaf!));
        Rounds(s, constrained, 3);

        Assert.True(s.Registry.TryGetLeaf(neighbour.Handle, out var donor) && donor is not null);
        Assert.True(TreeArranger.TileOf(donor!).Height >= (int)(WorkArea.Height * 0.10));
    }

    /// <summary>
    /// A share fixes ONE axis, and a window short of both is still parked.
    /// </summary>
    /// <remarks>
    /// A group divides along a single axis, so the most a share can ever answer for is that one.
    /// Growing a window to the width it demanded says nothing about the height it also demanded --
    /// and taking the successful half as the whole answer would leave it tiled while still
    /// overflowing, which is the state every rule here exists to end.
    /// </remarks>
    [Fact]
    public void AWindowShortOfBothAxes_IsNotSavedByAShareOfOne()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var neighbour = Window(10);
        var constrained = Window(20);

        // The width one neighbour can spare; the height no arrangement on this work area can.
        constrained.MinimumSize = (1300, WorkArea.Height + 120);

        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(constrained);

        Rounds(s, constrained, 3);

        Assert.False(InTree(s, constrained.Handle));
        Assert.True(InTree(s, neighbour.Handle));
    }

    /// <summary>
    /// When the space is not in the window's own group, it is taken from an ANCESTOR's split.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found by driving four windows. Toggling the ROOT left NVIDIA Broadcast inside a subtree only
    /// 684 tall against a floor of 772: no share of its own group could reach, because 772 is more
    /// than that whole group measures. The rest has to come from the root's own split.
    /// </para>
    /// <para>
    /// Built BINARY on purpose, through a focused leaf, because that is the only shape production
    /// makes and the flat trees the other facts use would never reach this path at all.
    /// </para>
    /// <para>
    /// Here the window's own group divides WIDTH while the window is short of HEIGHT, so that level
    /// has nothing to offer and is skipped rather than squeezed -- and the root, which does divide
    /// height, is where the space is.
    /// </para>
    /// </remarks>
    [Fact]
    public void WhenTheSpaceIsNotInItsOwnGroup_ItIsTakenFromAnAncestor()
    {
        var s = OneDisplay();
        LeafNode? focus = null;
        var adapter = AdapterFocusing(s, () => focus);
        using var _adapter = adapter;

        var first = Window(10);
        var second = Window(30);
        var constrained = Window(20);
        constrained.MinimumSize = (100, 700);

        s.Workspace.RaiseWindowAdded(first);
        s.Registry.TryGetLeaf(first.Handle, out var firstLeaf);
        focus = firstLeaf;

        s.Workspace.RaiseWindowAdded(second);
        s.Registry.TryGetLeaf(second.Handle, out var secondLeaf);
        focus = secondLeaf;

        s.Workspace.RaiseWindowAdded(constrained);

        // [first, [second, constrained]] -- now stack the ROOT, which leaves the subtree half the
        // height and the constrained window short of a floor its own group cannot pay for.
        Assert.True(s.Registry.TryGetLeaf(first.Handle, out var rootChild) && rootChild is not null);
        Assert.True(LayoutTree.ToggleAxis(rootChild!));

        Rounds(s, constrained, 3);

        Assert.True(InTree(s, constrained.Handle));
        Assert.True(s.Registry.TryGetLeaf(constrained.Handle, out var settled) && settled is not null);
        Assert.True(TreeArranger.TileOf(settled!).Height >= 700);
    }

    /// <summary>
    /// And a walk that still falls short puts every group it touched back.
    /// </summary>
    /// <remarks>
    /// Gathering space from more than one place means taking it before knowing whether the total
    /// will be enough. A window that ends up parked anyway must not leave squeezed neighbours
    /// behind it -- that is the layout paying for an attempt that failed.
    /// </remarks>
    [Fact]
    public void AWalkThatStillFallsShort_LeavesTheNeighboursExactlyAsTheyWere()
    {
        var s = OneDisplay();
        LeafNode? focus = null;
        var adapter = AdapterFocusing(s, () => focus);
        using var _adapter = adapter;

        var first = Window(10);
        var second = Window(30);
        var constrained = Window(20);

        // Taller than the whole work area, so no walk up any tree can ever satisfy it.
        constrained.MinimumSize = (100, WorkArea.Height + 400);

        s.Workspace.RaiseWindowAdded(first);
        s.Registry.TryGetLeaf(first.Handle, out var firstLeaf);
        focus = firstLeaf;

        s.Workspace.RaiseWindowAdded(second);
        s.Registry.TryGetLeaf(second.Handle, out var secondLeaf);
        focus = secondLeaf;

        s.Workspace.RaiseWindowAdded(constrained);

        Assert.True(s.Registry.TryGetLeaf(first.Handle, out var rootChild) && rootChild is not null);
        Assert.True(LayoutTree.ToggleAxis(rootChild!));

        Rounds(s, constrained, 3);

        Assert.False(InTree(s, constrained.Handle));

        // The two survivors against each other, which needs no baseline and no constant. Left
        // squeezed, the donor sits on the layout floor at a tenth of the work area while the other
        // keeps the rest -- put back, they split it evenly, as two windows always do.
        Assert.True(s.Registry.TryGetLeaf(first.Handle, out var donor) && donor is not null);
        Assert.True(s.Registry.TryGetLeaf(second.Handle, out var other) && other is not null);
        Assert.True(TreeArranger.TileOf(donor!).Height >= TreeArranger.TileOf(other!).Height - TreeArranger.Gap);
    }

    /// <summary>
    /// A ceiling is remembered as carefully as a floor, and reported for anything that would resize
    /// the window past it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of a WM_GETMINMAXINFO range, and it was being thrown away: the verdict knew
    /// the window would not go over its size and recorded only that it HAD one, never the number.
    /// Growing a tile past it buys dead space and costs a neighbour real room -- measured with
    /// NVIDIA Broadcast, whose slot went to 1117 while the window stayed at 1000 and the window
    /// beside it was squashed to 258 in exchange.
    /// </para>
    /// <para>
    /// Per AXIS, exactly like the floor and wrong in the same way otherwise: a dimension is a
    /// ceiling only where the window came back SMALLER than its tile.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWindowThatCannotFillItsTile_HasItsCeilingRemembered()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var clamped = Window(10);
        clamped.MaximumSize = (WorkArea.Width - 400, WorkArea.Height - 200);
        s.Workspace.RaiseWindowAdded(clamped);

        Rounds(s, clamped, 4);

        var limits = adapter.LimitsOf(clamped.Handle);
        Assert.Equal(WorkArea.Width - 400, limits.MaxWidth);
        Assert.Equal(WorkArea.Height - 200, limits.MaxHeight);
    }

    /// <summary>
    /// An axis the window FILLED is no ceiling, and is reported as unbounded.
    /// </summary>
    /// <remarks>
    /// Recording it would pin the window to whatever the smallest slot it ever held happened to be
    /// -- the mirror of the floor bug that had NVIDIA Broadcast "needing" a width it had merely
    /// chosen.
    /// </remarks>
    [Fact]
    public void AnAxisTheWindowFilled_IsReportedAsHavingNoCeiling()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var clamped = Window(10);

        // Short of the work area on the height only; the width is taken in full.
        clamped.MaximumSize = (WorkArea.Width * 2, WorkArea.Height - 200);
        s.Workspace.RaiseWindowAdded(clamped);

        Rounds(s, clamped, 4);

        var limits = adapter.LimitsOf(clamped.Handle);
        Assert.Equal(int.MaxValue, limits.MaxWidth);
        Assert.Equal(WorkArea.Height - 200, limits.MaxHeight);
    }

    /// <summary>A window that has demonstrated nothing is reported as bounded by nothing.</summary>
    [Fact]
    public void AWindowThatFitsItsTile_HasNoLimitsToReport()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var ordinary = Window(10);
        s.Workspace.RaiseWindowAdded(ordinary);
        Rounds(s, ordinary, 4);

        Assert.Equal((0, 0, int.MaxValue, int.MaxValue), adapter.LimitsOf(ordinary.Handle));
    }

    /// <summary>
    /// A window whose size is not the tiler's to choose is marked as such, so the user can see it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked for after a session of watching NVIDIA Broadcast behave unlike everything else on the
    /// desktop: it overflows a tile, leaves a gap inside another, refuses a resize halfway, and
    /// takes a share out of its neighbour. Every one of those is correct and none of them is
    /// obvious, and a border identical to every other window's claims a precision the layout does
    /// not have over it.
    /// </para>
    /// <para>
    /// A floor OR a ceiling is enough. Either one means the tile is not the whole story about where
    /// the window will actually sit, which is the only thing the mark is saying.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWindowWithAFloor_IsMarkedAsConstrained()
    {
        var (s, adapter, _, constrained) = SideBySide();
        using var _adapter = adapter;

        Assert.False(adapter.IsConstrained(constrained.Handle));

        Rounds(s, constrained, 3);

        Assert.True(adapter.IsConstrained(constrained.Handle));
    }

    [Fact]
    public void AWindowWithOnlyACeiling_IsMarkedAsConstrainedToo()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var clamped = Window(10);
        clamped.MaximumSize = (WorkArea.Width - 400, WorkArea.Height - 200);
        s.Workspace.RaiseWindowAdded(clamped);

        Rounds(s, clamped, 4);

        Assert.True(adapter.IsConstrained(clamped.Handle));
    }

    /// <summary>An ordinary window is not marked, which is the whole point of marking anything.</summary>
    [Fact]
    public void AnOrdinaryWindow_IsNeverMarked()
    {
        var s = OneDisplay();
        var adapter = Adapter(s);
        using var _adapter = adapter;

        var ordinary = Window(10);
        s.Workspace.RaiseWindowAdded(ordinary);
        Rounds(s, ordinary, EnoughRounds);

        Assert.False(adapter.IsConstrained(ordinary.Handle));
    }

    /// <summary>And the mark is forgotten with the window, like everything else about it.</summary>
    [Fact]
    public void TheMark_IsForgottenWhenTheWindowGoes()
    {
        var (s, adapter, _, constrained) = SideBySide();
        using var _adapter = adapter;

        Rounds(s, constrained, 3);
        Assert.True(adapter.IsConstrained(constrained.Handle));

        s.Workspace.RaiseWindowRemoved(constrained);

        Assert.False(adapter.IsConstrained(constrained.Handle));
    }

    /// <summary>
    /// The whole hardware sequence, end to end, in one fact. Broadcast overflowed its half tile and
    /// was untiled in four seconds -- that part worked. Then it was re-admitted into a full-height
    /// column, which it could not fill, and THAT was read as a fight: twelve rounds and
    /// <c>gave up</c>, refused for the life of the handle. The window was punished for the exact
    /// constraint it had just been forgiven for on the other side of its range.
    /// </summary>
    [Fact]
    public void AWindowWithARange_OverflowsASmallTileAndUnderFillsALargeOne_AndSurvivesBoth()
    {
        var (s, adapter, neighbour, constrained) = SideBySide();
        using var _adapter = adapter;

        // Too small for its floor: untiled, so the neighbour gets the space back.
        Rounds(s, constrained, 2);
        Assert.False(InTree(s, constrained.Handle));

        // Alone on the work area the tile now clears the floor -- and exceeds the ceiling, so the
        // window cannot fill it. That is not a fight and must never become one.
        s.Workspace.RaiseWindowRemoved(neighbour);
        s.Workspace.RaiseWindowAdded(constrained);
        Assert.True(InTree(s, constrained.Handle));

        Rounds(s, constrained, EnoughRounds);

        Assert.True(InTree(s, constrained.Handle));
        Assert.Equal(Ceiling.Width, constrained.Bounds.Width);
    }

    /// <summary>
    /// A window with a floor its tile already clears is an ordinary window and must be judged as
    /// one. Nothing here may cost a window that never strains against its constraint.
    /// </summary>
    [Fact]
    public void AWindowWhoseFloorItsTileAlreadyMeets_IsNeverUntiled()
    {
        var s = OneDisplay();
        using var adapter = Adapter(s);

        var neighbour = Window(10);
        var roomy = Window(20);
        roomy.MinimumSize = (10, 10);
        s.Workspace.RaiseWindowAdded(neighbour);
        s.Workspace.RaiseWindowAdded(roomy);

        Rounds(s, roomy, EnoughRounds);

        Assert.True(InTree(s, roomy.Handle));
        Assert.True(InTree(s, neighbour.Handle));
    }
}
