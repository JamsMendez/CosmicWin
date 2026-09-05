using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The focus border follows focus, and gets out of the way when there is nothing to frame.
/// </summary>
/// <remarks>
/// The drawing itself needs a live WPF window, a real HWND and a display, and is verified by hand
/// like the rest of the tray. What can be wrong HERE is everything around it: which window it
/// frames, whether it lets go, and whether it respects the pause the whole app respects.
/// </remarks>
public sealed class FocusBorderWiringTests
{
    private sealed record Call(nint Framed, Rectangle Window, double Scaling, int Thickness, bool Dashed);

    private sealed class RecordingFocusBorder : IFocusBorder
    {
        public List<Call> Shown { get; } = [];

        public int HideCallCount { get; private set; }

        /// <summary>Every colour it was told to use, in order. Null is the system accent.</summary>
        public List<uint?> Colours { get; } = [];

        public void ShowAround(nint framed, Rectangle window, double scaling, int thickness, bool dashed = false) =>
            Shown.Add(new Call(framed, window, scaling, thickness, dashed));

        public void UseColor(uint? rgb) => Colours.Add(rgb);

        public void Hide() => HideCallCount++;

        public void Dispose()
        {
        }
    }

    private sealed class NoForeground : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class ImmediateScheduler
    {
        private Action? _callback;

        public IDisposable Schedule(TimeSpan interval, Action callback)
        {
            _callback = callback;
            return new NullDisposable();
        }

        public void Fire() => _callback!();
    }

    private sealed class MutableVirtualDesktops(Guid current) : IVirtualDesktopService
    {
        public bool IsSupported => true;
        public int Count => 2;
        public int CurrentIndex { get; set; } = 1;
        public Guid CurrentDesktopId { get; set; } = current;
        public string? LastError => null;
        public bool TrySwitchTo(int oneBasedIndex) => true;
        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex) => true;
    }

    private sealed record Harness(
        AppComposition Composition, FakeWorkspace Workspace, NoForeground Foreground,
        RecordingFocusBorder Border, ImmediateScheduler Scheduler, LowLevelKeyboardHook Hook,
        FakeKeyboardHookPlatform Platform, TrayMenuController Tray, List<bool> Persisted,
        List<uint?> PersistedColours);

    private static Harness Wire(
        IVirtualDesktopService? virtualDesktops = null, bool focusBorderEnabled = true,
        uint? focusBorderColor = null)
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.5, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var foreground = new NoForeground();
        var border = new RecordingFocusBorder();
        var scheduler = new ImmediateScheduler();
        LowLevelKeyboardHook? hook = null;
        var platform = new FakeKeyboardHookPlatform();
        TrayMenuController? tray = null;
        var persistedColours = new List<uint?>();
        var persisted = new List<bool>();

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, foreground, new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer =>
            {
                hook = new LowLevelKeyboardHook(writer, platform, TimeSpan.FromSeconds(5), () => 0);
                return hook;
            },
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: controller =>
            {
                tray = controller;
                return new NullDisposable();
            },
            focusBorder: border,
            virtualDesktops: virtualDesktops,
            focusBorderEnabled: focusBorderEnabled,
            persistFocusBorder: persisted.Add,
            focusBorderColor: focusBorderColor,
            persistBorderColor: persistedColours.Add);

        return new Harness(
            composition, workspace, foreground, border, scheduler, hook!, platform, tray!, persisted,
            persistedColours);
    }

    [Fact]
    public void TheBorderFramesTheFocusedWindow_AtItsDisplaysScaling()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB01), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;

            harness.Scheduler.Fire();

            var call = harness.Border.Shown[^1];
            Assert.Equal(window.Handle, call.Framed);
            Assert.Equal(window.Bounds, call.Window);
            Assert.Equal(1.5, call.Scaling);
            Assert.Equal(BorderGeometry.DefaultThickness, call.Thickness);
        }
    }

    /// <summary>
    /// The border is told WHICH window it frames, not just where.
    /// </summary>
    /// <remarks>
    /// Reported from real use: a browser's dropdown menu overhangs the window that opened it, and
    /// the border was drawn across it. The overlay was topmost, so it sat above every popup on the
    /// desktop. The cure is to place it directly BELOW the window it frames -- the ring is entirely
    /// outside that window, so nothing of it is lost, and an owned popup always sits above its owner
    /// and therefore above the border too. That placement needs the framed window's handle, which is
    /// why it travels with the rectangle.
    /// </remarks>
    [Fact]
    public void TheBorderIsToldWhichWindowItFrames_NotOnlyWhere()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var first = new RecordingWindow(new IntPtr(0xB0A), Rectangle.FromSize(0, 0, 960, 1080));
            var second = new RecordingWindow(new IntPtr(0xB0B), Rectangle.FromSize(960, 0, 960, 1080));
            harness.Workspace.RaiseWindowAdded(first);
            harness.Workspace.RaiseWindowAdded(second);

            harness.Foreground.Handle = first.Handle;
            harness.Scheduler.Fire();
            Assert.Equal(first.Handle, harness.Border.Shown[^1].Framed);

            harness.Foreground.Handle = second.Handle;
            harness.Scheduler.Fire();
            Assert.Equal(second.Handle, harness.Border.Shown[^1].Framed);
        }
    }

    /// <summary>
    /// Turned off, the border draws nothing at all -- which leaves the window wearing the one
    /// Windows draws for itself, and that is the whole point of the switch.
    /// </summary>
    /// <remarks>
    /// It HIDES rather than simply skipping. The overlay is created once and reused forever, so a
    /// switch that only stopped calling ShowAround would strand the last frame it drew on screen.
    /// </remarks>
    [Fact]
    public void WithTheBorderTurnedOff_NothingIsFramed()
    {
        var harness = Wire(focusBorderEnabled: false);
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB0C), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;

            harness.Scheduler.Fire();

            Assert.Empty(harness.Border.Shown);
            Assert.True(harness.Border.HideCallCount > 0);
        }
    }

    /// <summary>
    /// Turning it back on reaches the screen immediately, not on the next tick. A setting the user
    /// has to wait half a second to see reads as one that did not work.
    /// </summary>
    [Fact]
    public void TurningTheBorderOn_FramesTheFocusedWindowWithoutWaitingForTheTick()
    {
        var harness = Wire(focusBorderEnabled: false);
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB0D), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;
            harness.Scheduler.Fire();
            Assert.Empty(harness.Border.Shown);

            harness.Tray.ToggleFocusBorder();

            Assert.Equal(window.Handle, harness.Border.Shown[^1].Framed);
        }
    }

    /// <summary>Turning it off from the menu lets go of the frame it was already drawing.</summary>
    [Fact]
    public void TurningTheBorderOff_LetsGoOfTheFrameItWasDrawing()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB0E), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;
            harness.Scheduler.Fire();
            Assert.NotEmpty(harness.Border.Shown);
            var framedBefore = harness.Border.Shown.Count;
            var hiddenBefore = harness.Border.HideCallCount;

            harness.Tray.ToggleFocusBorder();

            Assert.True(harness.Border.HideCallCount > hiddenBefore);

            // And it STAYS off: the tick must not quietly bring it back.
            harness.Scheduler.Fire();
            Assert.Equal(framedBefore, harness.Border.Shown.Count);
        }
    }

    /// <summary>
    /// The choice outlives the process. Without this the menu item would work perfectly and forget
    /// itself at the next logon, which is worse than not offering it.
    /// </summary>
    [Fact]
    public void TogglingTheBorder_HandsTheNewValueToWhoeverPersistsIt()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.ToggleFocusBorder();
            harness.Tray.ToggleFocusBorder();

            Assert.Equal([false, true], harness.Persisted);
        }
    }

    /// <summary>Nothing focused is not nothing to do: the border has to let go, or it hangs over a window that is gone.</summary>
    [Fact]
    public void WithNothingFocused_TheBorderIsHidden()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Scheduler.Fire();

            Assert.Empty(harness.Border.Shown);
            Assert.True(harness.Border.HideCallCount > 0);
        }
    }

    /// <summary>Pause means hands off, and a border left drawn would say the opposite on screen.</summary>
    [Fact]
    public void WhilePaused_TheBorderIsHidden()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB02), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;
            harness.Scheduler.Fire();
            var framed = harness.Border.Shown.Count;

            harness.Hook.IsPaused = true;
            harness.Scheduler.Fire();

            Assert.Equal(framed, harness.Border.Shown.Count);
            Assert.True(harness.Border.HideCallCount > 0);
        }
    }

    /// <summary>
    /// The fix for the reported lag. A chord that re-lays the tree must move the border with it,
    /// not leave it on the old rectangle until the next reconciliation tick.
    /// </summary>
    /// <remarks>
    /// Reported from real use, and worst on <c>Alt+O</c>, where toggling the split axis moves every
    /// window at once and the border sat behind all of them for up to a full interval. The
    /// scheduler is deliberately NEVER fired here: if the border only moved on the tick, this fact
    /// would time out.
    /// </remarks>
    [Fact]
    public async Task AChordMovesTheBorderWithoutWaitingForTheTick()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var left = new RecordingWindow(new IntPtr(0xB03), Rectangle.FromSize(0, 0, 960, 1080));
            var right = new RecordingWindow(new IntPtr(0xB04), Rectangle.FromSize(960, 0, 960, 1080));
            harness.Workspace.RaiseWindowAdded(left);
            harness.Workspace.RaiseWindowAdded(right);
            harness.Foreground.Handle = left.Handle;
            harness.Border.Shown.Clear();

            Assert.True(harness.Platform.Raise(KeyboardKey.O, isKeyDown: true, ModifierKeys.Alt));

            var moved = await WaitUntil(() => harness.Border.Shown.Count > 0, TimeSpan.FromSeconds(2));
            Assert.True(moved, "The border should follow the chord, not the reconciliation tick.");
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>
    /// The border follows the WINDOW, not only the chord that moved it.
    /// </summary>
    /// <remarks>
    /// Reported from real use and worst on Windows Terminal, which ANIMATES its resize: CosmicWin
    /// applies the geometry, the window keeps arriving for several frames, and a border refreshed
    /// once after the chord trails it the whole way. Every frame of that movement raises a bounds
    /// change, so following it is both exact and free of any interval to tune.
    /// </remarks>
    [Fact]
    public void WhenTheFocusedWindowKeepsMoving_TheBorderFollowsEveryFrame()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var window = new RecordingWindow(new IntPtr(0xB05), Rectangle.FromSize(0, 0, 800, 600));
            harness.Workspace.RaiseWindowAdded(window);
            harness.Foreground.Handle = window.Handle;
            harness.Scheduler.Fire();
            harness.Border.Shown.Clear();

            window.SimulateExternalMove(Rectangle.FromSize(0, 0, 900, 600));
            harness.Workspace.RaiseWindowBoundsChanged(window);

            // Where the window ENDS UP, not where it was passing through. The tiling adapter listens
            // to the same event and was subscribed first, so it snaps the window back into its tile
            // before this handler reads the rectangle -- and a border that followed the transient
            // position would land somewhere the window never stays.
            Assert.NotEmpty(harness.Border.Shown);
            Assert.Equal(window.Bounds, harness.Border.Shown[^1].Window);
        }
    }

    /// <summary>
    /// A reflow nobody asked for still has to move the border: closing a neighbour stretches the
    /// focused window, and the border has to arrive with it.
    /// </summary>
    /// <remarks>
    /// Reported from real use. The two paths that existed both miss this. There is no chord, so
    /// <c>AfterAction</c> never runs; and the bounds event cannot save it either, because
    /// <c>TreeArranger</c> reaches the window through the SAME <c>IWindow</c> instance the workspace
    /// caches, so <c>Win32Window.SetPosition</c> updates <c>Bounds</c> before the WinEvent arrives
    /// and <c>Win32Workspace.UpdateBounds</c> then sees no change to report. That left the
    /// reconciliation tick as the only surviving path, and the border a full interval behind. The
    /// scheduler is deliberately NEVER fired here: if the border only moved on the tick, this fact
    /// would fail.
    /// </remarks>
    [Fact]
    public void WhenClosingANeighbourStretchesTheFocusedWindow_TheBorderFollowsWithoutTheTick()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var staying = new RecordingWindow(new IntPtr(0xB08), Rectangle.FromSize(0, 0, 1920, 540));
            var closing = new RecordingWindow(new IntPtr(0xB09), Rectangle.FromSize(0, 540, 1920, 540));
            harness.Workspace.RaiseWindowAdded(staying);
            harness.Workspace.RaiseWindowAdded(closing);
            harness.Foreground.Handle = staying.Handle;
            harness.Scheduler.Fire();
            harness.Border.Shown.Clear();

            harness.Workspace.RaiseWindowRemoved(closing);

            Assert.NotEmpty(harness.Border.Shown);
            Assert.Equal(staying.Bounds, harness.Border.Shown[^1].Window);
        }
    }

    /// <summary>
    /// An app CosmicWin does not tile taking the foreground means no tiled window is active, and the
    /// border has to let go of the one behind it.
    /// </summary>
    /// <remarks>
    /// Reported from real use with Sticky Notes, listed in <c>exceptions.conf</c> and made
    /// fullscreen: the border kept framing the tiled window underneath and, because the overlay is
    /// topmost, drew itself across the app in front. The border must ask a STRICTER question than
    /// the chords do. <c>ActionExecutor.ResolveFocusedLeaf</c> deliberately falls back to the last
    /// known leaf when the foreground is untracked -- its own remarks name "a dialog or a non-tiled
    /// app" -- because dropping a focus chord there would strand the user outside the tiled world.
    /// That is right for a chord and wrong for a border, whose whole job is to say which window is
    /// active right now.
    /// </remarks>
    [Fact]
    public void WhenAnExcludedAppTakesTheForeground_TheBorderLetsGoOfTheWindowBehindIt()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var tiled = new RecordingWindow(new IntPtr(0xB0A), Rectangle.FromSize(0, 0, 1920, 1080));
            harness.Workspace.RaiseWindowAdded(tiled);
            harness.Foreground.Handle = tiled.Handle;
            harness.Scheduler.Fire();
            Assert.NotEmpty(harness.Border.Shown);

            harness.Border.Shown.Clear();
            var hiddenBefore = harness.Border.HideCallCount;

            // Excluded, so it never reaches the workspace and is never registered -- exactly what
            // the real exception list produces for Sticky Notes.
            harness.Foreground.Handle = new IntPtr(0xDEAD);
            harness.Scheduler.Fire();

            Assert.Empty(harness.Border.Shown);
            Assert.True(harness.Border.HideCallCount > hiddenBefore);
        }
    }

    /// <summary>
    /// The foreground handle can lag a virtual-desktop switch and keep naming the cloaked window on
    /// the desktop being left. Registry membership alone is global, so the border must also require
    /// that the leaf belongs to the tree currently being viewed.
    /// </summary>
    [Fact]
    public void AfterAnExternalDesktopSwitch_ThePreviousDesktopsFocusBorderIsHidden()
    {
        var desktopOne = new Guid("11111111-1111-1111-1111-111111111111");
        var desktopTwo = new Guid("22222222-2222-2222-2222-222222222222");
        var desktops = new MutableVirtualDesktops(desktopOne);
        var harness = Wire(desktops);
        using (harness.Composition)
        {
            var leftBehind = new RecordingWindow(new IntPtr(0xB0D), Rectangle.FromSize(0, 0, 1920, 1080));
            harness.Workspace.RaiseWindowAdded(leftBehind);
            harness.Foreground.Handle = leftBehind.Handle;
            harness.Scheduler.Fire();
            Assert.NotEmpty(harness.Border.Shown);

            harness.Border.Shown.Clear();
            var hiddenBefore = harness.Border.HideCallCount;
            desktops.CurrentDesktopId = desktopTwo;
            desktops.CurrentIndex = 2;

            harness.Scheduler.Fire();

            Assert.Empty(harness.Border.Shown);
            Assert.True(harness.Border.HideCallCount > hiddenBefore);
        }
    }

    /// <summary>
    /// Closing the window the border is ON is the one reflow the moved-handle filter cannot answer:
    /// the focused window did not move, it stopped existing.
    /// </summary>
    /// <remarks>
    /// Its handle is in no moved list, so the filter reads "the focused window did not move" and
    /// leaves the border where it is -- framing a rectangle whose window is gone, until the next
    /// reconciliation tick. The foreground is deliberately left on the dying window here: Windows
    /// has not handed it to a survivor yet when the removal arrives, which is precisely the moment
    /// the filter is blind at. No tiled window is active at that instant, so the honest answer is
    /// to draw nothing and let the tick or the next foreground change place it. The scheduler is
    /// NEVER fired after the close: if the border only let go on the tick, this fact would fail.
    /// </remarks>
    [Fact]
    public void WhenTheFocusedWindowCloses_TheBorderLetsGoWithoutTheTick()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var staying = new RecordingWindow(new IntPtr(0xB0B), Rectangle.FromSize(0, 0, 1920, 540));
            var closing = new RecordingWindow(new IntPtr(0xB0C), Rectangle.FromSize(0, 540, 1920, 540));
            harness.Workspace.RaiseWindowAdded(staying);
            harness.Workspace.RaiseWindowAdded(closing);
            harness.Foreground.Handle = closing.Handle;
            harness.Scheduler.Fire();
            Assert.NotEmpty(harness.Border.Shown);

            harness.Border.Shown.Clear();
            var hiddenBefore = harness.Border.HideCallCount;

            harness.Workspace.RaiseWindowRemoved(closing);

            Assert.Empty(harness.Border.Shown);
            Assert.True(harness.Border.HideCallCount > hiddenBefore);
        }
    }

    /// <summary>
    /// A window the border is NOT on moving is nothing to redraw for. During a reflow every tile
    /// reports a move, and answering all of them is work with nothing to show.
    /// </summary>
    [Fact]
    public void WhenAnUnfocusedWindowMoves_TheBorderIsLeftAlone()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            var focused = new RecordingWindow(new IntPtr(0xB06), Rectangle.FromSize(0, 0, 960, 1080));
            var other = new RecordingWindow(new IntPtr(0xB07), Rectangle.FromSize(960, 0, 960, 1080));
            harness.Workspace.RaiseWindowAdded(focused);
            harness.Workspace.RaiseWindowAdded(other);
            harness.Foreground.Handle = focused.Handle;
            harness.Scheduler.Fire();
            harness.Border.Shown.Clear();

            other.SimulateExternalMove(Rectangle.FromSize(900, 0, 1020, 1080));
            harness.Workspace.RaiseWindowBoundsChanged(other);

            Assert.Empty(harness.Border.Shown);
        }
    }

    /// <summary>
    /// The stored colour reaches the overlay at STARTUP, not on the first focus change. Applied
    /// lazily, the first window framed after launch would wear last month's accent for a moment.
    /// </summary>
    [Fact]
    public void TheConfiguredColour_IsAppliedAsTheAppComesUp()
    {
        var harness = Wire(focusBorderColor: 0xFF8800u);
        using (harness.Composition)
        {
            Assert.Equal(0xFF8800u, harness.Border.Colours[0]);
        }
    }

    /// <summary>
    /// No stored colour still SAYS so. The overlay would default to the accent anyway, but leaving
    /// it unsaid means the two paths differ, and the one nobody exercises is the one that rots.
    /// </summary>
    [Fact]
    public void WithNoStoredColour_TheAccentIsAskedForExplicitly()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            Assert.Equal([null], harness.Border.Colours);
        }
    }

    /// <summary>
    /// A colour chosen in the tray reaches the screen and the disk. Both, and in that order: the
    /// same rule the border toggle follows, because a preference that only survives is not a
    /// setting the user can see working.
    /// </summary>
    [Fact]
    public void AColourChosenInTheTray_IsAppliedAndPersisted()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.SetBorderColor(0x00A0FFu);

            Assert.Equal(0x00A0FFu, harness.Border.Colours[^1]);
            Assert.Equal([0x00A0FFu], harness.PersistedColours);
        }
    }

    /// <summary>The way home: handing back the accent is a choice like any other, not an absence.</summary>
    [Fact]
    public void TheAccentCanBeChosenBack_AndIsPersistedAsSuch()
    {
        var harness = Wire(focusBorderColor: 0xFF8800u);
        using (harness.Composition)
        {
            harness.Tray.SetBorderColor(null);

            Assert.Null(harness.Border.Colours[^1]);
            Assert.Equal([(uint?)null], harness.PersistedColours);
        }
    }

    /// <summary>
    /// The tray reads the live colour, so the item can seed its picker with what is on screen right
    /// now rather than with whatever the file said at launch.
    /// </summary>
    [Fact]
    public void TheTrayReportsTheColourCurrentlyInUse()
    {
        var harness = Wire(focusBorderColor: 0xFF8800u);
        using (harness.Composition)
        {
            Assert.Equal(0xFF8800u, harness.Tray.BorderColor);

            harness.Tray.SetBorderColor(0x00A0FFu);

            Assert.Equal(0x00A0FFu, harness.Tray.BorderColor);
        }
    }

    /// <summary>
    /// Colour and switch are independent. Choosing a colour must not turn the border on, and it
    /// must not write the OTHER setting away either -- the file carries both, and a save that
    /// rebuilt it from one of them would silently reset the other.
    /// </summary>
    [Fact]
    public void ChoosingAColour_LeavesTheOnOffSwitchAlone()
    {
        var harness = Wire(focusBorderEnabled: false);
        using (harness.Composition)
        {
            harness.Tray.SetBorderColor(0x00A0FFu);

            Assert.False(harness.Tray.IsFocusBorderEnabled);
            Assert.Empty(harness.Persisted);
        }
    }
}
