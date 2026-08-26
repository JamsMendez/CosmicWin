using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// The reconciliation tick handing focus to the arriving desktop, for the switches CosmicWin did
/// NOT make.
/// </summary>
/// <remarks>
/// <para>
/// This closes a gap the reliability lens named on <c>dbaa393</c>: the handover LOGIC was proven
/// through the chord path, which reaches the same method, but the wiring in
/// <see cref="AppComposition"/> that is supposed to close the <c>Win+Ctrl+arrow</c> and Task View
/// half of the defect was exercised by nothing at all. A proven method nobody proved was CALLED is
/// half a feature.
/// </para>
/// <para>
/// The tick is the only thing that can notice those switches: they raise nothing this process
/// subscribes to, so the only way to see one is to ask, once per interval.
/// </para>
/// </remarks>
public sealed class AppCompositionDesktopFocusWiringTests
{
    /// <summary>
    /// Records the ORDER of what was done to it, not just how often. Counts cannot answer "was the
    /// layout applied before focus moved", and that ordering is half of what this suite exists for.
    /// </summary>
    private sealed class TracedWindow(nint handle, Rectangle bounds, List<string> log) : IWindow
    {
        /// <summary>Real WS_SYSMENU|WS_MAXIMIZEBOX|WS_MINIMIZEBOX, so the filter chain admits it.</summary>
        private const uint TileableStyle = 0x00080000u | 0x00010000u | 0x00020000u;

        public nint Handle { get; } = handle;

        public string Title => "Traced";

        public Rectangle Bounds { get; private set; } = bounds;

        public bool IsAlive => true;

        public bool CanReposition => true;

        public string ClassName => string.Empty;

        public string ProcessName => string.Empty;

        public uint Style => TileableStyle;

        public uint ExStyle => 0u;

        public bool IsOwned => false;

        public int TryActivateCallCount { get; private set; }

        public void SetPosition(Rectangle value)
        {
            Bounds = value;
            log.Add($"position:0x{Handle:X}");
        }

        public ActivationOutcome Activate()
        {
            TryActivateCallCount++;
            log.Add($"activate:0x{Handle:X}");
            return ActivationOutcome.Direct;
        }

        public bool TryActivate() => Activate().Confirmed();

        public bool Equals(IWindow? other) => other is not null && Handle == other.Handle;

        public override bool Equals(object? obj) => obj is IWindow other && Equals(other);

        public override int GetHashCode() => Handle.GetHashCode();
    }

    private sealed class Foreground : IForegroundWindowSource
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
        AppComposition Composition,
        MutableVirtualDesktops Desktops,
        ImmediateScheduler Scheduler,
        Foreground Foreground,
        TracedWindow Here,
        TracedWindow There,
        Guid ThereId,
        List<string> Log);

    private static readonly Guid HereId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ThereIdValue = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>
    /// Two desktops on one monitor, a window already filed on each. Both trees are populated
    /// DIRECTLY rather than through the workspace, so that no part of the arrangement under test is
    /// also the thing that set the scene.
    /// </summary>
    private static Harness Wire(bool arrivingIsEmpty = false)
    {
        var log = new List<string>();
        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);

        var registry = new WindowRegistry();
        var treeManager = new TreeManager([display], display, registry);

        var hereLeaf = new LeafNode(new WindowRef(new IntPtr(0xE1)));
        var thereLeaf = new LeafNode(new WindowRef(new IntPtr(0xE2)));
        var here = new TracedWindow(hereLeaf.Window.Handle, Rectangle.FromSize(0, 0, 1920, 1080), log);
        var there = new TracedWindow(thereLeaf.Window.Handle, Rectangle.FromSize(0, 0, 1920, 1080), log);
        registry.Register(here, hereLeaf);
        registry.Register(there, thereLeaf);

        treeManager.TryGetTree(HereId, display, out var hereTree);
        hereTree!.Root = hereLeaf;

        treeManager.TryGetTree(ThereIdValue, display, out var thereTree);
        thereTree!.Root = arrivingIsEmpty ? null : thereLeaf;

        var desktops = new MutableVirtualDesktops(HereId);
        var scheduler = new ImmediateScheduler();
        var foreground = new Foreground { Handle = here.Handle };

        var composition = AppComposition.Wire(
            new FakeWorkspace(), treeManager, registry, foreground,
            new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: scheduler.Schedule,
            hookFactory: writer => new LowLevelKeyboardHook(
                writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: _ => new NullDisposable(),
            virtualDesktops: desktops);

        return new Harness(
            composition, desktops, scheduler, foreground, here, there, ThereIdValue, log);
    }

    /// <summary>
    /// The gap this suite was written to close. A desktop change CosmicWin did not make must move
    /// focus onto the arriving desktop, and the tick is what has to notice.
    /// </summary>
    [Fact]
    public void ADesktopSwitchCosmicWinDidNotMake_HandsFocusOverOnTheTick()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            // No warm-up tick. An earlier version fired one "so the composition's idea of the
            // desktop we are on is settled", which it never did: the composition reads the current
            // desktop when it is WIRED, so there is nothing left for a tick to settle and firing
            // one changed nothing measurable. The baseline below stands on its own.
            Assert.Equal(0, harness.There.TryActivateCallCount);

            // Win+Ctrl+Right, or a click in Task View. Nothing raises an event; the id just changes.
            harness.Desktops.CurrentDesktopId = harness.ThereId;
            harness.Scheduler.Fire();

            Assert.Equal(1, harness.There.TryActivateCallCount);
        }
    }

    /// <summary>
    /// The other half of the finding: the layout is applied FIRST, so the window focus lands on has
    /// already been put where the arriving desktop wants it.
    /// </summary>
    /// <remarks>
    /// Order, not counts. Counts cannot tell these two apart, and "activate a window and then move
    /// it out from under the user" is exactly the shape this asserts against.
    /// </remarks>
    [Fact]
    public void TheArrivingLayoutIsApplied_BeforeFocusIsHandedOver()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            // MEASURED, not assumed: the log is empty both after wiring and after a tick that sees
            // no desktop change, so the fire-then-clear that used to stand here discarded nothing.
            harness.Desktops.CurrentDesktopId = harness.ThereId;
            harness.Scheduler.Fire();

            var activated = harness.Log.IndexOf($"activate:0x{harness.There.Handle:X}");
            var positioned = harness.Log.IndexOf($"position:0x{harness.There.Handle:X}");

            Assert.True(activated >= 0, $"The arriving window was never activated. Log: {string.Join(", ", harness.Log)}");
            Assert.True(positioned >= 0, $"The arriving layout was never applied. Log: {string.Join(", ", harness.Log)}");
            Assert.True(
                positioned < activated,
                $"The layout must be applied before focus moves. Log: {string.Join(", ", harness.Log)}");
        }
    }

    /// <summary>
    /// A tick with nothing to report must move nothing. Otherwise the handover would fire five times
    /// a second and fight every click the user makes.
    /// </summary>
    [Fact]
    public void WithNoDesktopChange_TheTickHandsFocusToNobody()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Scheduler.Fire();
            harness.Scheduler.Fire();
            harness.Scheduler.Fire();

            Assert.Equal(0, harness.There.TryActivateCallCount);
            Assert.Equal(0, harness.Here.TryActivateCallCount);
        }
    }

    /// <summary>
    /// Arriving at an empty desktop activates nothing. The wiring has to carry that answer through
    /// as faithfully as it carries the other one.
    /// </summary>
    [Fact]
    public void SwitchingToAnEmptyDesktop_ActivatesNothing()
    {
        var harness = Wire(arrivingIsEmpty: true);
        using (harness.Composition)
        {
            harness.Desktops.CurrentDesktopId = harness.ThereId;
            harness.Scheduler.Fire();

            Assert.Equal(0, harness.There.TryActivateCallCount);
            Assert.Equal(0, harness.Here.TryActivateCallCount);
        }
    }

    /// <summary>
    /// The handover happens ONCE per change, not once per tick for as long as the user stays there.
    /// </summary>
    [Fact]
    public void AfterTheHandover_LaterTicksLeaveFocusAlone()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Desktops.CurrentDesktopId = harness.ThereId;
            harness.Scheduler.Fire();
            Assert.Equal(1, harness.There.TryActivateCallCount);

            harness.Scheduler.Fire();
            harness.Scheduler.Fire();

            Assert.Equal(1, harness.There.TryActivateCallCount);
        }
    }
}
