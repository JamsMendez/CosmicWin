using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Where focus lands on a desktop switch when there is no layout in force.
/// </summary>
/// <remarks>
/// <para>
/// Reported from real use: with tiling off, switching desktops assigned no focus and remembered
/// nothing. Measured on hardware before a line was changed -- every single switch traced
/// <c>-- no survivor</c> and the OS foreground never moved.
/// </para>
/// <para>
/// It was never about desktops. The whole handover is LEAF-shaped end to end: the memory is written
/// from a resolved leaf, the survivor is a tree walk, the recall walks <c>LeavesOn</c>, and the
/// sweep walks them again. With the layout off nothing is a leaf, so the survivor walk answered
/// nothing and returned FIRST -- before the recall ever got a turn. The same shape as the focus
/// border, which framed only leaves and went dark in this mode for the same reason.
/// </para>
/// <para>
/// So the record answers from the WINDOW instead. It was always a handle; only the route to it
/// changes.
/// </para>
/// </remarks>
public sealed class ActionExecutorDesktopFocusWithTilingOffTests
{
    private static readonly Guid DesktopA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid DesktopB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private const int A1 = 0xA1;
    private const int A2 = 0xA2;
    private const int B1 = 0xB1;
    private const int B2 = 0xB2;

    private sealed class FakeVirtualDesktops : IVirtualDesktopService
    {
        private readonly Dictionary<int, Guid> _byIndex = new() { [1] = DesktopA, [2] = DesktopB };

        public bool IsSupported => true;

        public int Count => 2;

        public int CurrentIndex => CurrentDesktopId == DesktopA ? 1 : 2;

        public Guid CurrentDesktopId { get; set; } = DesktopA;

        public string? LastError => null;

        public bool TrySwitchTo(int oneBasedIndex)
        {
            if (!_byIndex.TryGetValue(oneBasedIndex, out var desktop))
            {
                return false;
            }

            CurrentDesktopId = desktop;
            return true;
        }

        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex) => true;
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;

        public nint GetActiveWindowOfThreadOwning(nint hwnd) => 0;
    }

    private sealed record Harness(
        ActionExecutor Executor,
        FakeForegroundWindowSource Foreground,
        FakeVirtualDesktops Desktops,
        List<nint> Activated,
        Dictionary<nint, Guid> LivesOn,
        Dictionary<nint, Rectangle> Bounds);

    /// <summary>
    /// The core of the report. Left desktop B on its SECOND window, so a fallback that took the
    /// first thing it found could not pass this by accident.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_ReturningToADesktop_ActivatesTheWindowThatHeldFocusThere()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);
        await GoTo(harness, desktop: 2, from: A1);

        Assert.Equal(B2, harness.Activated[^1]);
    }

    /// <summary>
    /// One record per desktop, not one for the machine -- the same guarantee the tiled path already
    /// makes. A single slot would have the last departure answering for both.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_EachDesktopRemembersItsOwnWindow()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A2);
        await GoTo(harness, desktop: 1, from: B2);
        Assert.Equal(A2, harness.Activated[^1]);

        await GoTo(harness, desktop: 2, from: A2);
        Assert.Equal(B2, harness.Activated[^1]);
    }

    /// <summary>
    /// A desktop nobody has left yet has nothing to hand back, and nothing is invented. With a tree
    /// the first tile answers; without one there is no such thing as "the first tile", and picking
    /// some window out of the workspace would drag the user somewhere they never asked to go.
    /// Windows already chose a foreground on arrival, and that choice is left alone.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_AFirstVisitToADesktopActivatesNothing()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);

        Assert.Empty(harness.Activated);
    }

    /// <summary>
    /// The record is keyed by desktop, but a window can be SENT elsewhere between being remembered
    /// and being recalled. Activating one that now lives on another desktop would take the user
    /// there -- the exact opposite of what the chord asked for.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_ARememberedWindowThatHasMovedAwayIsNotRecalled()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);
        harness.Activated.Clear();

        // B2 was sent to desktop A while the user was away.
        harness.LivesOn[B2] = DesktopA;
        await GoTo(harness, desktop: 2, from: A1);

        Assert.Empty(harness.Activated);
    }

    /// <summary>
    /// <see cref="Guid.Empty"/> is the shell DECLINING to say, which it answers for any window
    /// merely mid-creation. This repository has already paid once for reading that as "somewhere
    /// else" -- every arriving window was filed under a desktop nobody was looking at and tiling
    /// stopped outright -- so it must not cost a window its recall either.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_AWindowTheShellWillNotPlaceIsStillRecalled()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);
        harness.Activated.Clear();

        harness.LivesOn[B2] = Guid.Empty;
        await GoTo(harness, desktop: 2, from: A1);

        Assert.Equal(B2, harness.Activated[^1]);
    }

    /// <summary>
    /// A remembered window that has since CLOSED is not asked for. Found on hardware, in a trace
    /// line reading <c>recalled=0x40884 activated=False</c> for a window killed minutes earlier.
    /// </summary>
    /// <remarks>
    /// A refused activation is harmless on its own -- but Windows REUSES handles, and the moment
    /// that number names a different window the recall would activate a stranger. The tiled path
    /// has always checked liveness for exactly this reason; the untiled one was reading the shell's
    /// desktop answer alone, and a dead handle draws Guid.Empty, which is deliberately read as
    /// "here".
    /// </remarks>
    [Fact]
    public async Task WithTilingOff_ARememberedWindowThatHasClosedIsNotRecalled()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);
        harness.Activated.Clear();

        // B2 is gone: nothing can locate it any more, which is what a closed window looks like from
        // every reader CosmicWin has.
        harness.Bounds.Remove(B2);
        harness.LivesOn.Remove(B2);
        await GoTo(harness, desktop: 2, from: A1);

        Assert.Empty(harness.Activated);
    }

    /// <summary>
    /// Sending a window away is not arriving anywhere. The user stays put, so the record for the
    /// desktop in view names the window being sent at this very instant -- recalling it would hand
    /// focus straight back to what is leaving.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_SendingAWindowAwayRecallsNothing()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);
        harness.Activated.Clear();

        harness.Foreground.Handle = A1;
        await harness.Executor.ScheduleAsync(
            new HotkeyAction(HotkeyActionKind.MoveWindowToDesktop, 2), CancellationToken.None);

        Assert.Empty(harness.Activated);
    }

    /// <summary>
    /// The second half of the report, and it is not about tiling at all: the memory was only ever
    /// written by CosmicWin's OWN switch chord. Leave a desktop with Win+Ctrl+arrow or Task View
    /// and nothing was recorded, so coming back had nothing to hand out.
    /// </summary>
    /// <remarks>
    /// The reconciliation tick is the only observer of a switch CosmicWin did not make, and by the
    /// time it notices, the current desktop id already names the ARRIVING one -- too late to record
    /// where the user was. So it notes the foreground every pass instead, while the answer is still
    /// true, and the record is at worst one interval old.
    /// </remarks>
    [Fact]
    public async Task WithTilingOff_ASwitchCosmicWinDidNotMakeIsStillRemembered()
    {
        var harness = Build();

        // On desktop B, working on its second window -- noted by the tick, never by a chord.
        harness.Desktops.CurrentDesktopId = DesktopB;
        harness.Foreground.Handle = B2;
        harness.Executor.NoteFocusOnCurrentDesktop();

        // The user walks away with Win+Ctrl+arrow: no chord, so nothing else records anything.
        harness.Desktops.CurrentDesktopId = DesktopA;
        harness.Foreground.Handle = A1;
        harness.Executor.NoteFocusOnCurrentDesktop();

        await GoTo(harness, desktop: 2, from: A1);

        Assert.Equal(B2, harness.Activated[^1]);
    }

    /// <summary>
    /// And the tick's note never invents a record for a window there is nothing to say about --
    /// an empty desktop, or a foreground CosmicWin cannot locate.
    /// </summary>
    [Fact]
    public async Task WithTilingOff_NotingAnEmptyForegroundRemembersNothing()
    {
        var harness = Build();

        harness.Desktops.CurrentDesktopId = DesktopB;
        harness.Foreground.Handle = 0;
        harness.Executor.NoteFocusOnCurrentDesktop();

        harness.Desktops.CurrentDesktopId = DesktopA;
        await GoTo(harness, desktop: 2, from: A1);

        Assert.Empty(harness.Activated);
    }

    private static async Task GoTo(Harness harness, int desktop, nint from)
    {
        harness.Foreground.Handle = from;
        await harness.Executor
            .ScheduleAsync(new HotkeyAction(HotkeyActionKind.SwitchDesktop, desktop), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One monitor, two desktops, two windows each -- and NO tree at all, which is the whole point:
    /// with tiling off nothing is ever added to one, so every lookup the handover used to make
    /// answers nothing.
    /// </summary>
    private static Harness Build()
    {
        var registry = new WindowRegistry();
        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);

        var desktops = new FakeVirtualDesktops();
        var treeManager = new TreeManager([display], display, registry)
        {
            CurrentDesktop = () => desktops.CurrentDesktopId,
        };

        var bounds = new Dictionary<nint, Rectangle>
        {
            [A1] = Rectangle.FromSize(0, 0, 960, 1080),
            [A2] = Rectangle.FromSize(960, 0, 960, 1080),
            [B1] = Rectangle.FromSize(0, 0, 960, 1080),
            [B2] = Rectangle.FromSize(960, 0, 960, 1080),
        };

        var livesOn = new Dictionary<nint, Guid>
        {
            [A1] = DesktopA,
            [A2] = DesktopA,
            [B1] = DesktopB,
            [B2] = DesktopB,
        };

        var activated = new List<nint>();
        var foreground = new FakeForegroundWindowSource { Handle = A1 };

        treeManager.TryGetTree(DesktopA, display, out var treeA);

        var executor = new ActionExecutor(treeA!, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
            VirtualDesktops = desktops,
            TilingEnabled = () => false,
            ResolveWindowBounds = handle => bounds.TryGetValue(handle, out var rect) ? rect : null,
            ResolveWindowDesktop = handle => livesOn.GetValueOrDefault(handle),
            ActivateUntrackedWindow = handle =>
            {
                activated.Add(handle);
                return true;
            },
        };

        return new Harness(executor, foreground, desktops, activated, livesOn, bounds);
    }
}
